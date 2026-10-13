using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

/// <summary>
/// Файлы лисёнка Розы в папке профиля: <c>fox/state.json</c> (что он помнит о человеке) и
/// <c>fox/diary.json</c> (дневник по дням).
/// </summary>
/// <remarks>
/// Здесь, а не в ProjectFox: эти файлы переносит архив данных и стирает «Удалить все данные», а
/// ядро на сборку Розы ссылаться не может. Поэтому здесь только то, что нужно обоим, — имена,
/// чтение и запись с шифрованием, слияние дневника, — а сами типы состояния и записи дневника
/// живут у Розы: слияние идёт по JSON, по полю даты, и их устройства не знает.
/// </remarks>
internal static class FoxData
{
    public const string FolderName = "fox";

    public const string StateFileName = "state.json";

    public const string DiaryFileName = "diary.json";

    /// <summary>Поле даты записи дневника (<c>yyyy-MM-dd</c>) — по нему сливаются дневники.</summary>
    public const string DiaryDateField = "date";

    public static string StatePath(string root) => Path.Combine(root, FolderName, StateFileName);

    public static string DiaryPath(string root) => Path.Combine(root, FolderName, DiaryFileName);

    /// <summary>
    /// Текст файла — открытый или под DPAPI. <c>null</c> — файла нет, он занят или зашифрован не
    /// для этой учётной записи Windows.
    /// </summary>
    public static string? Read(string path)
    {
        try
        {
            return File.Exists(path) ? AtRestCipher.DecryptFile(File.ReadAllBytes(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Пишет файл целиком подменой: под DPAPI, если профиль шифрует переписку, — дневник Розы
    /// пересказывает её. <c>false</c> — не записалось.
    /// </summary>
    public static bool Write(string path, string text, bool encrypt)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { } folder)
            {
                Directory.CreateDirectory(folder);
            }

            var bytes = encrypt ? AtRestCipher.EncryptFile(text) : Encoding.UTF8.GetBytes(text);
            if (bytes is null)
            {
                return false;
            }

            AppDataFile.WriteAtomicBytes(path, bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Сливает два дневника по дню: записи без пары берутся из обоих, у совпавших дней —
    /// своя (<paramref name="incomingWins"/> = false) или пришедшая. Порядок — по дате.
    /// </summary>
    /// <remarks>
    /// Записи без даты и битый JSON с одной из сторон не роняют слияние: битая сторона считается
    /// пустой, запись без даты отбрасывается — дневник ведётся по дням, и без дня она ничья.
    /// </remarks>
    public static string MergeDiary(string? mine, string? incoming, bool incomingWins)
    {
        var byDate = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        void Take(string? json, bool overwrite)
        {
            foreach (var entry in Entries(json))
            {
                if (DateOf(entry) is { } date && (overwrite || !byDate.ContainsKey(date)))
                {
                    byDate[date] = entry.DeepClone();
                }
            }
        }

        Take(mine, overwrite: false);
        Take(incoming, overwrite: incomingWins);
        return new JsonArray([.. byDate.Values]).ToJsonString(Indented);
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static IEnumerable<JsonNode> Entries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) is JsonArray array ? array.OfType<JsonNode>().ToList() : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? DateOf(JsonNode entry)
    {
        if (entry is not JsonObject item)
        {
            return null;
        }

        foreach (var (name, value) in item)
        {
            if (string.Equals(name, DiaryDateField, StringComparison.OrdinalIgnoreCase) &&
                value is JsonValue text && text.TryGetValue<string>(out var date) &&
                DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                return date;
            }
        }

        return null;
    }
}
