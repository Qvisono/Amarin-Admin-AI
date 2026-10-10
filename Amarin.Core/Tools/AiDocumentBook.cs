using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Документы, которые создал сам ИИ (<c>ai-documents.json</c> в папке профиля): их правка не
/// спрашивает человека.
/// </summary>
/// <remarks>
/// <para>
/// Человек просил: документ, созданный нейросетью, не требует разрешения ни на создание, ни на
/// изменения. Создание решает шлюз по месту (<see cref="DocumentZone"/>), а правка — по этой книге:
/// молча меняется только то, что ИИ сам и создал, а не любой файл человека.
/// </para>
/// <para>
/// Файл узнаётся по пути и времени создания: человек, удаливший документ и положивший на его место
/// свой с тем же именем, получает другой файл, и о нём снова спрашивают. Сохранение в Word или
/// Excel время создания не меняет — Windows переносит его на файл, заменивший прежний, — поэтому
/// документ, поправленный человеком, остаётся документом ИИ. После каждой своей записи отметка
/// обновляется: подмена файла готовой копией могла дать ему новое время создания.
/// </para>
/// <para>
/// В архив данных книга не едет: в ней пути этой машины.
/// </para>
/// </remarks>
internal sealed class AiDocumentBook
{
    internal const string FileName = "ai-documents.json";

    /// <summary>Сколько документов помнить: дальше — забываются самые старые.</summary>
    internal const int Capacity = 2000;

    private static readonly JsonSerializerOptions Json = new(AppJson.Options) { WriteIndented = false };

    private readonly Lock _gate = new();
    private string _root;
    private List<Entry>? _entries;

    public AiDocumentBook(string root) => _root = root;

    /// <summary>Профиль сменился: книга того профиля читается при первом вопросе.</summary>
    public void UseRoot(string root)
    {
        lock (_gate)
        {
            _root = root;
            _entries = null;
        }
    }

    /// <summary>Создал ли этот файл ИИ — тот самый файл, а не другой на том же месте.</summary>
    public bool Owns(string path)
    {
        if (Created(path) is not { } created)
        {
            return false;
        }

        var full = Full(path);
        lock (_gate)
        {
            return Loaded().Any(entry => SamePath(entry.Path, full) && entry.CreatedUtc == created);
        }
    }

    /// <summary>ИИ записал этот документ: запомнить его или обновить отметку.</summary>
    public void Note(string path)
    {
        if (Created(path) is not { } created)
        {
            return;
        }

        var full = Full(path);
        lock (_gate)
        {
            var entries = Loaded();
            entries.RemoveAll(entry => SamePath(entry.Path, full));
            entries.Add(new Entry(full, created));
            if (entries.Count > Capacity)
            {
                entries.RemoveRange(0, entries.Count - Capacity);
            }

            Save(entries);
        }
    }

    private List<Entry> Loaded()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        try
        {
            var file = Path.Combine(_root, FileName);
            _entries = File.Exists(file) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Нечитаемая книга — не повод отказывать: о документах просто снова спросят.
            _entries = [];
        }

        return _entries;
    }

    private void Save(List<Entry> entries)
    {
        try
        {
            AppDataFile.WriteAtomic(Path.Combine(_root, FileName), JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не записалась — до перезапуска книга помнит в памяти, а после о документе спросят.
        }
    }

    private static DateTime? Created(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetCreationTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record Entry(string Path, DateTime CreatedUtc);
}
