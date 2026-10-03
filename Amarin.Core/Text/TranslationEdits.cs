namespace Amarin.Core;

/// <summary>Строка перевода в редакторе: ключ, оригинал, перевод и правил ли её человек.</summary>
internal sealed record TranslationRow(string Key, string Original, string Translation, bool Edited);

/// <summary>
/// Правка машинного перевода (I4): что человек поправил руками и как это не потерять.
/// </summary>
/// <remarks>
/// <para>
/// Файл языка — плоский словарь «ключ → строка», и интерфейс берёт из него только ключи
/// <c>S.*</c>. Поэтому список поправленных ключей лежит рядом, служебной строкой
/// <see cref="EditedKey"/> через запятую: прежние версии программы его просто не заметят, а
/// формат файла не меняется.
/// </para>
/// <para>
/// Поправленное руками модель больше не трогает: при новом переводе того же языка такие ключи
/// не отправляются ей вовсе (и не оплачиваются), а в файле остаются как были.
/// </para>
/// </remarks>
internal static class TranslationEdits
{
    public const string EditedKey = "$edited";

    public static HashSet<string> Edited(IReadOnlyDictionary<string, string>? map) =>
        map is not null && map.TryGetValue(EditedKey, out var list)
            ? list.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Строки для редактора: все ключи оригинала, перевод из файла (нет — пусто).</summary>
    public static IReadOnlyList<TranslationRow> Rows(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string>? translated)
    {
        var edited = Edited(translated);
        return source
            .Where(pair => pair.Key.StartsWith("S.", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new TranslationRow(
                pair.Key,
                pair.Value,
                translated?.GetValueOrDefault(pair.Key) ?? "",
                edited.Contains(pair.Key)))
            .ToList();
    }

    /// <summary>Поиск по ключу, оригиналу и переводу — без регистра.</summary>
    public static bool Matches(TranslationRow row, string query) =>
        string.IsNullOrWhiteSpace(query) ||
        row.Key.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.Original.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.Translation.Contains(query, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ключи, где правка потеряла или добавила плейсхолдер <c>{0}</c>: такая строка уронила бы
    /// <c>Loc.Format</c> или показала бы дыру вместо числа.
    /// </summary>
    public static IReadOnlyList<string> PlaceholderProblems(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string> changes) =>
        changes
            .Where(pair => source.TryGetValue(pair.Key, out var original) &&
                           !LanguageTranslator.SamePlaceholders(original, pair.Value))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Применяет правки: новые значения и пометка «правлено руками». Пустая правка не считается.</summary>
    public static Dictionary<string, string> Apply(IReadOnlyDictionary<string, string> map, IReadOnlyDictionary<string, string> changes)
    {
        var result = new Dictionary<string, string>(map, StringComparer.Ordinal);
        var edited = Edited(map);
        foreach (var (key, value) in changes)
        {
            if (string.IsNullOrWhiteSpace(value) || !key.StartsWith("S.", StringComparison.Ordinal))
            {
                continue;
            }

            result[key] = value.Trim();
            edited.Add(key);
        }

        SetEdited(result, edited);
        return result;
    }

    /// <summary>Снимает пометку «правлено руками» — ключ снова доступен новому переводу.</summary>
    public static Dictionary<string, string> Unmark(IReadOnlyDictionary<string, string> map, string key)
    {
        var result = new Dictionary<string, string>(map, StringComparer.Ordinal);
        var edited = Edited(map);
        edited.Remove(key);
        SetEdited(result, edited);
        return result;
    }

    /// <summary>Что отправить модели при новом переводе языка: всё, кроме поправленного руками.</summary>
    public static Dictionary<string, string> ToTranslate(IReadOnlyDictionary<string, string> source, IReadOnlyDictionary<string, string>? existing)
    {
        var edited = Edited(existing);
        return source
            .Where(pair => !edited.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    /// <summary>Новый перевод поверх прежнего файла: поправленное руками и пометка остаются.</summary>
    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string>? existing, IReadOnlyDictionary<string, string> fresh)
    {
        var edited = Edited(existing);
        var result = new Dictionary<string, string>(fresh, StringComparer.Ordinal);
        foreach (var key in edited)
        {
            if (existing is not null && existing.TryGetValue(key, out var value))
            {
                result[key] = value;
            }
        }

        SetEdited(result, edited);
        return result;
    }

    private static void SetEdited(Dictionary<string, string> map, HashSet<string> edited)
    {
        if (edited.Count == 0)
        {
            map.Remove(EditedKey);
        }
        else
        {
            map[EditedKey] = string.Join(',', edited.Order(StringComparer.Ordinal));
        }
    }
}
