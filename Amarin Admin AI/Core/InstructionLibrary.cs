using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Одна инструкция пользователя: как решать задачи на определённую тему.
/// </summary>
/// <remarks>
/// Неизменяемая запись: кэш <see cref="InstructionLibrary"/> раздаёт одни и те же объекты
/// ходу чата, инструменту и странице настроек сразу, и правка на месте в одном из них молча
/// поменяла бы то, что видят остальные. Изменённая копия делается через <c>with</c> и
/// уходит в <see cref="InstructionLibrary.Save"/>.
/// </remarks>
public sealed record Instruction
{
    /// <summary>
    /// Имя файла без расширения. Для заведённых в программе — восемь шестнадцатеричных знаков,
    /// у положенного в папку руками — как его назвал человек.
    /// </summary>
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>Слова и фразы, по которым модель узнаёт тему. Регистр не важен.</summary>
    public IReadOnlyList<string> Triggers { get; init; } = [];

    public string Text { get; init; } = "";

    /// <summary>Выключенная инструкция лежит на диске, но модель о ней не знает.</summary>
    public bool Enabled { get; init; } = true;

    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Инструкции пользователя: по файлу <c>instructions/&lt;id&gt;.md</c> в папке профиля.
/// </summary>
/// <remarks>
/// По файлу на инструкцию, а не одним JSON: текст инструкции — это Markdown, который человек
/// пишет, пересылает и правит руками, и положенный в папку <c>.md</c> без всякой шапки
/// подхватывается как есть — название берётся из имени файла.
/// <para>
/// Один объект на программу, как <see cref="SpendLedger"/>: на него ссылаются движок чата и
/// инструмент <c>read_instruction</c>, и смена профиля лишь переводит его на другую папку
/// (<see cref="UseRoot"/>), ничего не пересобирая.
/// </para>
/// <para>
/// Список нужен на каждый ход чата, а кольцо контекста собирает системный промпт и вовсе на
/// каждое обновление экрана. Поэтому <see cref="Snapshot"/> держит кэш: папку обходит не чаще
/// <see cref="RescanInterval"/>, и перечитывает только те файлы, у которых сменились время
/// записи или длина — то и другое приходит из самого перечисления папки, сами файлы при этом
/// не открываются. Свои записи сбрасывают кэш сразу, правка руками видна через две секунды.
/// </para>
/// </remarks>
internal sealed class InstructionLibrary
{
    /// <summary>Длиннее название не влезает ни в карточку, ни в строку списка для модели.</summary>
    public const int NameLimit = 60;

    public const int TriggerLimit = 12;

    public const int TriggerLengthLimit = 40;

    /// <summary>
    /// С заголовком ответа инструмента текст обязан уложиться в срез
    /// <see cref="ChatToolPreview.FormatForApi"/> (12 000 знаков), иначе модель прочла бы
    /// инструкцию без конца и не узнала бы об этом.
    /// </summary>
    public const int TextLimit = 10_000;

    /// <summary>
    /// Файл больше этого — не инструкция, а случайно попавший в папку документ. Читать его
    /// в память на каждый обход незачем.
    /// </summary>
    private const long FileSizeLimit = 512 * 1024;

    internal const string FolderName = "instructions";

    internal const string Extension = ".md";

    internal static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<long> _clock;

    private string _folder;
    private IReadOnlyList<Instruction> _cached = [];
    private long _scannedAt;
    private bool _stale = true;

    /// <param name="clock">
    /// Монотонные часы в тиках <see cref="Stopwatch"/>. Подменяются в тестах: иначе проверка
    /// перерыва между обходами стоила бы настоящие секунды.
    /// </param>
    public InstructionLibrary(string? root = null, Func<long>? clock = null)
    {
        _folder = Path.Combine(root ?? AppPaths.Root, FolderName);
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Папка с файлами инструкций активного профиля.</summary>
    public string Folder
    {
        get
        {
            lock (_gate)
            {
                return _folder;
            }
        }
    }

    /// <summary>Сколько файлов разобрано за жизнь объекта. Только для тестов кэша.</summary>
    internal int ParseCount { get; private set; }

    /// <summary>Переводит библиотеку на папку другого профиля.</summary>
    public void UseRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        lock (_gate)
        {
            _folder = Path.Combine(root, FolderName);
            _files.Clear();
            _cached = [];
            _stale = true;
        }
    }

    /// <summary>
    /// Все инструкции, включая выключенные: сначала старые, потом новые. Никогда не бросает.
    /// </summary>
    public IReadOnlyList<Instruction> Snapshot()
    {
        lock (_gate)
        {
            var now = _clock();
            if (!_stale && Stopwatch.GetElapsedTime(_scannedAt, now) < RescanInterval)
            {
                return _cached;
            }

            Rescan();
            _scannedAt = now;
            _stale = false;
            return _cached;
        }
    }

    /// <summary>Только те, что человек оставил включёнными, — их и видит модель.</summary>
    public IReadOnlyList<Instruction> EnabledSnapshot() =>
        Snapshot().Where(instruction => instruction.Enabled).ToList();

    /// <summary>
    /// Ищет по идентификатору, а если такого нет — по названию. Регистр не важен ни там, ни там:
    /// файловая система Windows его тоже не различает, а модель пишет название как придётся.
    /// </summary>
    public Instruction? Find(string? idOrName)
    {
        var key = idOrName?.Trim().Trim('[', ']').Trim();
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var all = Snapshot();
        return all.FirstOrDefault(item => string.Equals(item.Id, key, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(item => string.Equals(item.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Записывает инструкцию. Пустой или негодный идентификатор заменяется новым.
    /// </summary>
    /// <returns>Записанная (приведённая) инструкция или null, если диск отказал.</returns>
    public Instruction? Save(Instruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        lock (_gate)
        {
            var normalized = Normalize(instruction);
            if (!IsUsableId(normalized.Id))
            {
                normalized = normalized with { Id = NewId() };
            }

            try
            {
                AppDataFile.WriteAtomic(PathFor(normalized.Id), Serialize(normalized));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null;
            }

            _stale = true;
            return normalized;
        }
    }

    /// <summary>Удаляет файл инструкции. Уже удалённая — тоже успех.</summary>
    public bool Delete(string id)
    {
        lock (_gate)
        {
            if (!IsUsableId(id))
            {
                return false;
            }

            try
            {
                File.Delete(PathFor(id));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            _stale = true;
            return true;
        }
    }

    public Instruction? SetEnabled(string id, bool enabled) =>
        Find(id) is { } found && string.Equals(found.Id, id, StringComparison.OrdinalIgnoreCase)
            ? found.Enabled == enabled ? found : Save(found with { Enabled = enabled })
            : null;

    /// <summary>
    /// Заводит инструкции из файлов. Каждая получает новый идентификатор, а совпавшее название —
    /// приписку « (2)»: две строки с одним именем в списке для модели читались бы как одна.
    /// </summary>
    public InstructionImport Import(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var imported = new List<Instruction>();
        var skipped = 0;
        var taken = Snapshot().Select(item => item.Name).ToList();

        foreach (var path in paths)
        {
            Instruction? parsed = null;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length <= FileSizeLimit)
                {
                    parsed = Parse(
                        File.ReadAllText(path),
                        Path.GetFileNameWithoutExtension(path),
                        DateTime.Now);
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }

            if (parsed is null)
            {
                skipped++;
                continue;
            }

            var name = UniqueName(parsed.Name, taken);
            var saved = Save(parsed with { Id = "", Name = name, CreatedAt = DateTime.Now });
            if (saved is null)
            {
                skipped++;
                continue;
            }

            taken.Add(saved.Name);
            imported.Add(saved);
        }

        return new InstructionImport(imported, skipped);
    }

    /// <summary>Сохраняет инструкцию в файл того же вида, что лежит в папке профиля.</summary>
    public static bool Export(Instruction instruction, string path)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        try
        {
            AppDataFile.WriteAtomic(path, Serialize(Normalize(instruction)));
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Название, которого ещё нет среди <paramref name="taken"/>: «Имя», «Имя (2)», …</summary>
    internal static string UniqueName(string name, IReadOnlyCollection<string> taken)
    {
        var baseName = TrimName(name);
        bool Taken(string candidate) =>
            taken.Any(existing => string.Equals(existing, candidate, StringComparison.OrdinalIgnoreCase));

        if (!Taken(baseName))
        {
            return baseName;
        }

        for (var number = 2; ; number++)
        {
            var suffix = $" ({number})";
            var head = baseName.Length + suffix.Length > NameLimit
                ? baseName[..(NameLimit - suffix.Length)].TrimEnd()
                : baseName;
            if (!Taken(head + suffix))
            {
                return head + suffix;
            }
        }
    }

    /// <summary>Название одной строкой и не длиннее <see cref="NameLimit"/>.</summary>
    public static string TrimName(string? name)
    {
        var flat = Flatten(name);
        return flat.Length > NameLimit ? flat[..NameLimit].TrimEnd() : flat;
    }

    /// <summary>
    /// Приводит список триггеров: режет по запятым и точкам с запятой, обрезает пробелы,
    /// убирает повторы без учёта регистра и держит пределы по числу и длине.
    /// </summary>
    /// <remarks>
    /// Запятая — разделитель и в поле ввода, и в шапке файла, поэтому внутри триггера её
    /// не бывает: иначе записанное прочиталось бы обратно двумя словами.
    /// </remarks>
    public static IReadOnlyList<string> NormalizeTriggers(IEnumerable<string>? triggers)
    {
        var result = new List<string>();
        if (triggers is null)
        {
            return result;
        }

        foreach (var raw in triggers)
        {
            foreach (var piece in (raw ?? "").Split([',', ';'], StringSplitOptions.TrimEntries))
            {
                var trigger = Flatten(piece);
                if (trigger.Length == 0)
                {
                    continue;
                }

                if (trigger.Length > TriggerLengthLimit)
                {
                    trigger = trigger[..TriggerLengthLimit].TrimEnd();
                }

                if (result.Any(existing => string.Equals(existing, trigger, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                result.Add(trigger);
                if (result.Count == TriggerLimit)
                {
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>Начало текста одной строкой — для карточки в списке.</summary>
    public static string Preview(string text, int limit = 140) => PromptLibrary.Preview(text, limit);

    /// <summary>
    /// Разбирает файл инструкции. Шапка между строками <c>---</c> необязательна: без неё
    /// название — <paramref name="fallbackName"/>, текст — весь файл.
    /// </summary>
    /// <returns>Null, если текста нет: пустую инструкцию модели читать нечего.</returns>
    internal static Instruction? Parse(string content, string fallbackName, DateTime fallbackCreated)
    {
        var text = (content ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');

        string? name = null;
        var triggers = new List<string>();
        var enabled = true;
        var created = fallbackCreated;
        var bodyStart = 0;

        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var close = Array.FindIndex(lines, 1, line => line.Trim() is "---" or "...");
            if (close > 0)
            {
                string? listKey = null;
                for (var i = 1; i < close; i++)
                {
                    var line = lines[i];
                    var trimmed = line.Trim();

                    // Список в духе YAML — «triggers:» и под ним строки «- слово». Так пишут
                    // шапку руками чаще, чем через запятую, и терять такие триггеры обидно.
                    if (listKey == "triggers" && trimmed.StartsWith('-'))
                    {
                        triggers.Add(Unquote(trimmed[1..]));
                        continue;
                    }

                    listKey = null;
                    var colon = line.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var key = line[..colon].Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim();
                    switch (key)
                    {
                        case "name":
                        case "title":
                            name = Unquote(value);
                            break;
                        case "triggers":
                        case "tags":
                            if (value.Length == 0)
                            {
                                listKey = "triggers";
                            }
                            else
                            {
                                triggers.AddRange(value.Trim('[', ']').Split(',').Select(Unquote));
                            }

                            break;
                        case "enabled":
                            enabled = !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                                      !value.Equals("no", StringComparison.OrdinalIgnoreCase) &&
                                      value != "0";
                            break;
                        case "created":
                            if (DateTime.TryParse(
                                    Unquote(value),
                                    CultureInfo.InvariantCulture,
                                    DateTimeStyles.RoundtripKind,
                                    out var stamp))
                            {
                                created = stamp.Kind == DateTimeKind.Utc ? stamp.ToLocalTime() : stamp;
                            }

                            break;
                    }
                }

                bodyStart = close + 1;
            }
        }

        var body = string.Join("\n", lines.Skip(bodyStart)).Trim('\n', ' ', '\t');
        if (body.Length == 0)
        {
            return null;
        }

        var finalName = TrimName(name);
        if (finalName.Length == 0)
        {
            finalName = TrimName(fallbackName);
        }

        if (finalName.Length == 0)
        {
            return null;
        }

        return new Instruction
        {
            Name = finalName,
            Triggers = NormalizeTriggers(triggers),
            Text = body,
            Enabled = enabled,
            CreatedAt = created
        };
    }

    /// <summary>Файл инструкции: шапка и текст. Переводы строк — как принято в Windows.</summary>
    internal static string Serialize(Instruction instruction)
    {
        var builder = new StringBuilder();
        builder.Append("---\r\n");
        builder.Append("name: ").Append(instruction.Name).Append("\r\n");
        builder.Append("triggers: ").Append(string.Join(", ", instruction.Triggers)).Append("\r\n");
        builder.Append("enabled: ").Append(instruction.Enabled ? "true" : "false").Append("\r\n");
        builder.Append("created: ")
            .Append(instruction.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))
            .Append("\r\n");
        builder.Append("---\r\n\r\n");
        builder.Append(instruction.Text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
        builder.Append("\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// Годится ли строка в имя файла папки инструкций. Идентификатор сюда приходит только из
    /// собственного списка, но проверка держит и на случай ошибки: путь, собранный из
    /// «..\settings», перетёр бы чужой файл.
    /// </summary>
    internal static bool IsUsableId(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        id.Length <= 120 &&
        id.Trim() == id &&
        id != "." && id != ".." &&
        id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        id.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) < 0;

    private string PathFor(string id) => Path.Combine(_folder, id + Extension);

    private string NewId()
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (!File.Exists(PathFor(id)))
            {
                return id;
            }
        }
    }

    private static Instruction Normalize(Instruction instruction) => instruction with
    {
        Id = instruction.Id?.Trim() ?? "",
        Name = TrimName(instruction.Name),
        Triggers = NormalizeTriggers(instruction.Triggers),
        Text = (instruction.Text ?? "").Replace("\r\n", "\n").Trim('\n', ' ', '\t'),
        CreatedAt = instruction.CreatedAt == default ? DateTime.Now : instruction.CreatedAt
    };

    private void Rescan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var directory = new DirectoryInfo(_folder);
            if (directory.Exists)
            {
                foreach (var file in directory.EnumerateFiles("*" + Extension))
                {
                    // Шаблон «*.md» у Windows ловит и «x.md.tmp» недописанного файла.
                    if (!file.Extension.Equals(Extension, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    seen.Add(file.FullName);
                    if (_files.TryGetValue(file.FullName, out var known) &&
                        known.Stamp == file.LastWriteTimeUtc &&
                        known.Length == file.Length)
                    {
                        continue;
                    }

                    _files[file.FullName] = new CachedFile(
                        file.LastWriteTimeUtc,
                        file.Length,
                        Read(file));
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Папку не прочитать — остаётся то, что успели узнать раньше.
            return;
        }

        foreach (var gone in _files.Keys.Where(path => !seen.Contains(path)).ToList())
        {
            _files.Remove(gone);
        }

        _cached = _files.Values
            .Select(file => file.Instruction)
            .OfType<Instruction>()
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Instruction? Read(FileInfo file)
    {
        var id = Path.GetFileNameWithoutExtension(file.Name);
        if (!IsUsableId(id) || file.Length > FileSizeLimit)
        {
            return null;
        }

        try
        {
            ParseCount++;
            return Parse(File.ReadAllText(file.FullName), id, file.CreationTime) is { } parsed
                ? parsed with { Id = id }
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Flatten(string? value) =>
        string.Join(" ", (value ?? "").Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 &&
               (trimmed[0] == '"' && trimmed[^1] == '"' || trimmed[0] == '\'' && trimmed[^1] == '\'')
            ? trimmed[1..^1].Trim()
            : trimmed;
    }

    private sealed record CachedFile(DateTime Stamp, long Length, Instruction? Instruction);
}

/// <summary>Итог импорта: что завелось и сколько файлов пропущено (пустые, нечитаемые).</summary>
internal sealed record InstructionImport(IReadOnlyList<Instruction> Imported, int Skipped);
