using System.Globalization;
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
        Append(new AuditEntry
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
        });
    }

    /// <summary>Дописывает строку. Не бросает.</summary>
    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = JsonSerializer.Serialize(entry, LineOptions);
        if (SafeEncrypt())
        {
            // Отказ Windows шифровать не повод терять строку: журнал и нужен затем, чтобы след
            // остался, поэтому она ляжет открытой, а в журнал сбоев — пометка.
            if (AtRestCipher.EncryptLine(line) is { } sealedLine)
            {
                line = sealedLine;
            }
            else
            {
                CrashLog.Write("audit: encryption failed, line written in plain text");
            }
        }

        var bytes = Encoding.UTF8.GetBytes(line + "\n");

        try
        {
            lock (_gate)
            {
                var folder = Path.Combine(_root, "audit");
                System.IO.Directory.CreateDirectory(folder);

                // Одна строка — одно открытие на дозапись: оборванная запись портит не больше
                // своей строки, а чтение ниже пропускает её, не теряя соседних.
                using var stream = new FileStream(
                    Path.Combine(folder, FileName(entry.Time)),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CrashLog.Write("audit: " + ex.Message);
        }
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
