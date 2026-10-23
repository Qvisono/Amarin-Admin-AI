using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Amarin.Core;

/// <summary>Чем кончился вызов инструмента.</summary>
public enum AuditOutcome
{
    /// <summary>Исполнен и сообщил об успехе.</summary>
    Ok,

    /// <summary>Исполнен, но сам сообщил о неудаче.</summary>
    Failed,

    /// <summary>Не исполнялся: запрет шлюза, отказ человека, выключенный инструмент.</summary>
    Refused,

    /// <summary>Не исполнялся или оборван: ход отменили.</summary>
    Cancelled
}

/// <summary>Что сказал SynGuard про раунд, в котором был вызов.</summary>
public enum AuditGuard
{
    /// <summary>Проверки не было: защита выключена или в раунде одно чтение.</summary>
    Off,

    Safe,

    /// <summary>Защитник счёл вызов атакой, и решал человек.</summary>
    Flagged,

    /// <summary>Проверка не состоялась: сеть, таймаут, отказ провайдера.</summary>
    Failed,

    /// <summary>Ответ защитника не разобран — вызовы пошли без вердикта.</summary>
    Unparsed
}

/// <summary>Одна строка журнала аудита: что, где, кем разрешено и чем кончилось.</summary>
public sealed class AuditEntry
{
    /// <summary>Уникальный номер строки — по нему архив данных сливает журналы без повторов.</summary>
    public string Id { get; set; } = "";

    public DateTime Time { get; set; }

    public string? ChatId { get; set; }

    /// <summary>Заголовок на момент вызова: чат потом переименуют или удалят, а строка останется.</summary>
    public string? ChatTitle { get; set; }

    /// <summary>Кто вызвал: подпись агента или null — сам чат.</summary>
    public string? Agent { get; set; }

    /// <summary>Удалённая машина, на которой выполнялся вызов; null — этот ПК.</summary>
    public string? Target { get; set; }

    public string? CallId { get; set; }

    public string Tool { get; set; } = "";

    /// <summary>Аргументы как их прислала модель, без секретов и с потолком длины.</summary>
    public string Args { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ArgsTruncated { get; set; }

    public AuditOutcome Outcome { get; set; }

    public ApprovalSource ApprovedBy { get; set; }

    public AuditGuard Guard { get; set; }

    /// <summary>Коротко, что ответил инструмент или почему отказано.</summary>
    public string? Result { get; set; }

    /// <summary>
    /// Звено цепочки (1.37.0): SHA-256 открытого текста предыдущей строки того же файла;
    /// <see cref="AuditLog.ChainStart"/> — строка первая в файле; null — строка до 1.37.0.
    /// </summary>
    /// <remarks>
    /// Хеш берётся от строки байт в байт, как она лежит (у зашифрованной — после расшифровки), а
    /// не от пересобранного объекта: пересборка зависела бы от часового пояса и порядка полей, и
    /// цепочка «рвалась» бы от переезда в другой пояс. Ставит его только запись — из чужого
    /// архива звено не приезжает, импорт цепляет строку к хвосту своего файла.
    /// </remarks>
    public string? Prev { get; set; }
}

/// <summary>Итог проверки цепочки журнала аудита.</summary>
/// <param name="Started">В журнале есть хоть одна строка с цепочкой (журнал 1.37.0 и новее).</param>
/// <param name="BrokenFile">Месяц, где цепочка разорвана (<c>yyyy-MM</c>); null — цела.</param>
/// <param name="BrokenLine">Номер строки разрыва в файле, с единицы.</param>
public sealed record AuditChainReport(bool Started, string? BrokenFile, int BrokenLine)
{
    public bool Intact => BrokenFile is null;
}

/// <summary>Откуда пришёл вызов — чат, его заголовок и агент, если вызывал он.</summary>
internal sealed record AuditOrigin(string? ChatId, string? ChatTitle, string? Agent);

/// <summary>
/// Журнал аудита: каждая запись в систему и каждый отказ — строкой в <c>audit/yyyy-MM.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// Отдельно от переписок намеренно. Журнал действий (<see cref="ActionJournal"/>) собирается
/// из чатов, и удалённый чат уносит с собой всё, что в нём делалось; здесь же строка живёт,
/// пока её не удалят вместе с журналом. Файл только дописывается — строка за строкой, каждая
/// отдельным открытием на дозапись, — и ни одна операция программы его не переписывает.
/// </para>
/// <para>
/// Удачное чтение не пишется: журнал утонул бы в «прочитал файл», и запись, ради которой его
/// открыли, в нём стало бы не найти. Ошибки диска проглатываются — не записанная строка не
/// повод останавливать работу, — но оставляют след в журнале сбоев.
/// </para>
/// </remarks>
internal sealed class AuditLog
{
    /// <summary>Потолок аргументов в строке. Скрипт на сотню килобайт журналу не нужен целиком.</summary>
    public const int ArgsLimit = 16 * 1024;

    /// <summary>Потолок краткого итога.</summary>
    public const int ResultLimit = 500;

    /// <summary>Звено первой строки файла: до неё в этом месяце ничего не было.</summary>
    public const string ChainStart = "-";

    private static readonly JsonSerializerOptions LineOptions = CreateLineOptions();

    /// <summary>Имена аргументов, чьи значения в журнал не попадают никогда.</summary>
    private static readonly string[] SecretNames =
        ["password", "passwd", "pwd", "secret", "token", "api_key", "apikey", "key_value", "credential"];

    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<string?>> _secrets;
    private string _root;

    public AuditLog(string root, Func<IReadOnlyList<string?>>? secrets = null)
    {
        _root = root;
        _secrets = secrets ?? (() => []);
    }

    /// <summary>
    /// Шифровать ли новые строки (<see cref="AppSettings.EncryptChats"/>).
    /// </summary>
    /// <remarks>
    /// Свойство, а не параметр конструктора: журнал заводится до того, как прочитаны настройки
    /// профиля. Уже записанные строки не переписываются — журнал только дописывается, — а
    /// читаются оба формата.
    /// </remarks>
    public Func<bool> Encrypt { get; set; } = static () => false;

    /// <summary>
    /// Копия новой строки наружу — в журнал событий Windows (1.37.0): строка и хеш её открытого
    /// текста. Null — копии нет.
    /// </summary>
    /// <remarks>
    /// Зовётся только из <see cref="Record"/>: строки чужого журнала, влитые импортом архива, —
    /// действия другой машины, и в журнал Windows этой их класть нельзя. Ставит её корень
    /// композиции; тесты её не ставят и в настоящий журнал Windows не пишут.
    /// </remarks>
    public Action<AuditEntry, string>? Mirror { get; set; }

    /// <summary>Папка журнала — в папке профиля, рядом с чатами.</summary>
    public string Directory
    {
        get
        {
            lock (_gate)
            {
                return Path.Combine(_root, "audit");
            }
        }
    }

    /// <summary>Переезд на другой профиль: у него свой журнал.</summary>
    public void UseRoot(string root)
    {
        lock (_gate)
        {
            _root = root;
        }
    }

    /// <summary>Нужна ли строка в журнале: всё, что пишет в систему, и всякий отказ.</summary>
    public static bool Worth(Tools.ToolEffect effect, AuditOutcome outcome) =>
        effect == Tools.ToolEffect.Write || outcome is AuditOutcome.Refused;

    /// <summary>Собирает строку и дописывает её, если она того стоит.</summary>
    public void Record(
        AuditOrigin? origin,
        string? callId,
        string toolName,
        string? argumentsJson,
        Tools.ToolEffect effect,
        AuditOutcome outcome,
        ApprovalSource approvedBy,
        AuditGuard guard,
        string? result)
    {
        if (!Worth(effect, outcome))
        {
            return;
        }

        var secrets = SafeSecrets();
        var (args, truncated) = PrepareArgs(argumentsJson, secrets);
        var entry = new AuditEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = DateTime.Now,
            ChatId = origin?.ChatId,
            ChatTitle = origin?.ChatTitle,
            Agent = origin?.Agent,
            Target = ExecutionTarget.Current is { } machine ? $"{machine.Name} ({machine.Address})" : null,
            CallId = callId,
            Tool = toolName,
            Args = args,
            ArgsTruncated = truncated,
            Outcome = outcome,
            ApprovedBy = approvedBy,
            Guard = guard,
            Result = Trim(CrashReport.Scrub(result ?? "", secrets), ResultLimit)
        };

        if (Write(entry) is { } hash)
        {
            SafeMirror(entry, hash);
        }
    }

    /// <summary>Дописывает строку, сцепляя её с хвостом файла. Не бросает.</summary>
    public void Append(AuditEntry entry) => _ = Write(entry);

    /// <summary>Дописывает строку; хеш её открытого текста или null — не записалась.</summary>
    /// <remarks>
    /// Всё — от чтения хвоста до записи — под одним замком: вызовы раунда пишут параллельно, и
    /// две строки, прочитавшие один хвост, сослались бы на одну предыдущую. Хвост читается с диска
    /// каждый раз, без памяти о прошлой записи: импорт архива пишет своим экземпляром журнала, и
    /// запомненный хвост после него был бы чужим.
    /// </remarks>
    private string? Write(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            lock (_gate)
            {
                var folder = Path.Combine(_root, "audit");
                System.IO.Directory.CreateDirectory(folder);

                // Одна строка — одно открытие: оборванная запись портит не больше своей строки,
                // а чтение ниже пропускает её, не теряя соседних.
                using var stream = new FileStream(
                    Path.Combine(folder, FileName(entry.Time)),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.Read);
                var (tail, endsWithNewLine) = ReadTail(stream);
                entry.Prev = tail is null ? ChainStart : LinkOf(tail);

                var plain = JsonSerializer.Serialize(entry, LineOptions);
                var line = plain;
                if (SafeEncrypt())
                {
                    // Отказ Windows шифровать не повод терять строку: журнал и нужен затем, чтобы
                    // след остался, поэтому она ляжет открытой, а в журнал сбоев — пометка.
                    if (AtRestCipher.EncryptLine(plain) is { } sealedLine)
                    {
                        line = sealedLine;
                    }
                    else
                    {
                        CrashLog.Write("audit: encryption failed, line written in plain text");
                    }
                }

                // Оборванная прежде строка без перевода в конце склеилась бы с новой, и обе стали
                // бы нечитаемыми; с переводом она остаётся своей строкой — и звеном цепочки.
                var bytes = Encoding.UTF8.GetBytes((endsWithNewLine ? "" : "\n") + line + "\n");
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes, 0, bytes.Length);
                return Hash(plain);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("audit: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Проверка цепочки: каждая строка с 1.37.0 называет хеш предыдущей, и подменённая, вставленная
    /// или удалённая посреди файла строка рвёт звено.
    /// </summary>
    /// <remarks>
    /// Строка без звена — строка версии до 1.37.0: журнал прежних версий или запись после отката
    /// «Вернуть v…». Разрывом она не считается, ведь подменённую или вставленную такую строку и так
    /// выдаёт звено следующей. Звено, у которого предыдущая строка не расшифровывается (DPAPI
    /// чужой учётной записи Windows), пропускается, а не считается разрывом: подмены там не видно,
    /// но и доказательства её нет. Оборванная строка своего звена не называет, зато её хеш
    /// проверяет следующая. Строка расшифровывается один раз: DPAPI — самое дорогое в проверке
    /// большого журнала.
    /// </remarks>
    public AuditChainReport VerifyChain(CancellationToken cancellationToken = default)
    {
        var started = false;
        foreach (var file in Files())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var month = Path.GetFileNameWithoutExtension(file);
            var first = true;
            string? previousLink = null; // null — предыдущая строка не расшифровывается.
            var number = 0;
            foreach (var raw in ReadLines(file))
            {
                number++;
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                var plain = AtRestCipher.DecryptLine(line);
                if (plain is not null && Parse(plain)?.Prev is { Length: > 0 } link)
                {
                    started = true;
                    var broken = link == ChainStart
                        ? !first
                        : first || (previousLink is not null && link != previousLink);
                    if (broken)
                    {
                        return new AuditChainReport(true, month, number);
                    }
                }

                first = false;
                previousLink = plain is null ? null : Hash(plain);
            }
        }

        return new AuditChainReport(started, null, 0);
    }

    /// <summary>Все строки журнала, новые сверху. Битые строки пропускаются.</summary>
    public List<AuditEntry> ReadAll(CancellationToken cancellationToken = default)
    {
        var entries = new List<AuditEntry>();
        foreach (var file in Files())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var line in ReadLines(file))
            {
                if (Parse(line) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        entries.Sort((left, right) => right.Time.CompareTo(left.Time));
        return entries;
    }

    /// <summary>Файлы журнала по месяцам, от старых к новым.</summary>
    public List<string> Files()
    {
        var folder = Directory;
        try
        {
            if (!System.IO.Directory.Exists(folder))
            {
                return [];
            }

            return System.IO.Directory
                .EnumerateFiles(folder, "*.jsonl")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Дописывает строки из чужого журнала (архив данных), пропуская те, что уже есть.
    /// </summary>
    /// <returns>Сколько строк добавилось.</returns>
    public int Merge(IEnumerable<AuditEntry> incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var known = ReadAll().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var added = 0;
        foreach (var entry in incoming.OrderBy(item => item.Time))
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !known.Add(entry.Id))
            {
                continue;
            }

            Append(entry);
            added++;
        }

        return added;
    }

    /// <summary>Удаляет журнал целиком — только по явной просьбе человека.</summary>
    public void DeleteAll()
    {
        var folder = Directory;
        try
        {
            lock (_gate)
            {
                if (System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.Delete(folder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("audit delete: " + ex.Message);
        }
    }

    /// <summary>Строка журнала из JSON; null — строка битая.</summary>
    public static AuditEntry? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || AtRestCipher.DecryptLine(line.Trim()) is not { } plain)
        {
            return null;
        }

        try
        {
            var entry = JsonSerializer.Deserialize<AuditEntry>(plain, LineOptions);
            return entry is { Tool.Length: > 0 } ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Выгрузка в JSON — массивом, с отступами: её открывают и читают.</summary>
    public static string ToJson(IEnumerable<AuditEntry> entries) =>
        JsonSerializer.Serialize(entries.ToList(), ExportOptions);

    /// <summary>
    /// Выгрузка в CSV для Excel: UTF-8 с BOM, запятые, кавычки по правилам RFC 4180.
    /// </summary>
    /// <remarks>
    /// Ячейка, начинающаяся с <c>= + - @</c>, табуляции или перевода каретки, получает впереди
    /// апостроф: иначе табличная программа сочтёт её формулой, а аргументы в журнале пишет
    /// модель — это готовая инъекция формул в таблицу того, кто журнал откроет.
    /// </remarks>
    public static string ToCsv(IEnumerable<AuditEntry> entries)
    {
        var csv = new StringBuilder();
        csv.Append('﻿');
        csv.AppendLine("time,chat_id,chat_title,agent,call_id,tool,outcome,approved_by,guard,args,result");
        foreach (var entry in entries)
        {
            csv.AppendJoin(
                    ',',
                    Cell(entry.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                    Cell(entry.ChatId),
                    Cell(entry.ChatTitle),
                    Cell(entry.Agent),
                    Cell(entry.CallId),
                    Cell(entry.Tool),
                    Cell(JsonNamingPolicy.CamelCase.ConvertName(entry.Outcome.ToString())),
                    Cell(JsonNamingPolicy.CamelCase.ConvertName(entry.ApprovedBy.ToString())),
                    Cell(JsonNamingPolicy.CamelCase.ConvertName(entry.Guard.ToString())),
                    Cell(entry.Args),
                    Cell(entry.Result))
                .Append("\r\n");
        }

        return csv.ToString();
    }

    internal static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    /// <summary>Имя файла месяца. Время местное: человек ищет «что было в марте» по своим часам.</summary>
    internal static string FileName(DateTime time) =>
        time.ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".jsonl";

    /// <summary>Аргументы для журнала: секреты вырезаны, длина ограничена.</summary>
    internal static (string Args, bool Truncated) PrepareArgs(string? json, IReadOnlyList<string?> secrets)
    {
        var text = json ?? "";
        try
        {
            if (text.Length > 0 && JsonNode.Parse(text) is JsonObject node)
            {
                MaskSecretFields(node);
                text = node.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Грязные аргументы от модели пишутся как есть: журнал — не место их чинить.
        }

        text = CrashReport.Scrub(text, secrets);
        return text.Length > ArgsLimit ? (text[..ArgsLimit], true) : (text, false);
    }

    private static void MaskSecretFields(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(pair => pair.Key).ToList())
                {
                    if (SecretNames.Any(secret => name.Contains(secret, StringComparison.OrdinalIgnoreCase)))
                    {
                        obj[name] = "***";
                    }
                    else if (obj[name] is { } child)
                    {
                        MaskSecretFields(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                    {
                        MaskSecretFields(item);
                    }
                }

                break;
        }
    }

    /// <summary>Звено к строке: хеш её открытого текста, а у нерасшифровываемой — самой строки.</summary>
    internal static string LinkOf(string line)
    {
        var trimmed = line.Trim();
        return Hash(AtRestCipher.DecryptLine(trimmed) ?? trimmed);
    }

    private static string Hash(string plain) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(plain)));

    /// <summary>
    /// Последняя непустая строка файла (null — файл пуст) и кончается ли файл переводом строки.
    /// </summary>
    /// <remarks>
    /// Читается с конца кусками: файл месяца растёт до мегабайт, а нужна одна строка.
    /// </remarks>
    private static (string? Tail, bool EndsWithNewLine) ReadTail(FileStream stream)
    {
        var length = stream.Length;
        if (length == 0)
        {
            return (null, true);
        }

        const int chunk = 64 * 1024;
        var collected = new List<byte>();
        var position = length;
        var buffer = new byte[chunk];
        var endsWithNewLine = false;
        var first = true;
        while (position > 0)
        {
            var size = (int)Math.Min(chunk, position);
            position -= size;
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, size);
            if (first)
            {
                endsWithNewLine = buffer[size - 1] == (byte)'\n';
                first = false;
            }

            collected.InsertRange(0, buffer.AsSpan(0, size).ToArray());
            var text = Encoding.UTF8.GetString(collected.ToArray()).TrimEnd('\n', '\r');
            var cut = text.LastIndexOf('\n');
            if (cut >= 0 || position == 0)
            {
                var tail = text[(cut + 1)..].Trim();
                if (tail.Length > 0 || position == 0)
                {
                    return (tail.Length > 0 ? tail : null, endsWithNewLine);
                }
            }
        }

        return (null, endsWithNewLine);
    }

    private void SafeMirror(AuditEntry entry, string hash)
    {
        try
        {
            Mirror?.Invoke(entry, hash);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write("audit mirror: " + ex.Message);
        }
    }

    private bool SafeEncrypt()
    {
        try
        {
            return Encrypt();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private IReadOnlyList<string?> SafeSecrets()
    {
        try
        {
            return _secrets();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        string[] lines;
        try
        {
            // Писатель держит файл открытым на дозапись с FileShare.Read — читать можно.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            lines = reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return lines;
    }

    private static string Trim(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "…";

    private static JsonSerializerOptions CreateLineOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static readonly JsonSerializerOptions ExportOptions = new(LineOptions) { WriteIndented = true };
}
