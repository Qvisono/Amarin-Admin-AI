namespace Amarin.Core;

/// <summary>Чем является пункт палитры команд. Порядок — порядок групп в выдаче.</summary>
internal enum PaletteKind
{
    Action,
    Setting,
    Command,
    Recipe,
    Chat
}

/// <summary>Пункт палитры команд (1.37.0): то, что можно найти и выполнить одним поиском.</summary>
/// <param name="Id">Что выполнить: действие, команда, рецепт или чат; у настройки — её место.</param>
/// <param name="Title">Название, по которому ищут и которое показывают.</param>
/// <param name="Detail">Справа от названия: сочетание клавиш, путь «Страница › Подстраница», дата.</param>
internal sealed record PaletteItem(PaletteKind Kind, string Id, string Title, string Detail = "");

/// <summary>Найденный пункт и где в названии совпадение — его выделяют полужирным.</summary>
internal readonly record struct PaletteMatch(PaletteItem Item, int Start, int Length);

/// <summary>
/// Поиск палитры команд: все слова запроса должны найтись в названии или пути пункта, выше —
/// совпадение с начала названия, потом с начала слова, потом где угодно.
/// </summary>
/// <remarks>
/// Группы идут в постоянном порядке (<see cref="PaletteKind"/>), а не перемешаны по очкам: человек
/// привыкает, где искать настройку, а где чат, и прыгающие группы пришлось бы читать заново.
/// </remarks>
internal static class PaletteSearch
{
    /// <summary>Сколько пунктов группы показывать на запрос: дальше человек уточняет запрос.</summary>
    internal static int LimitOf(PaletteKind kind) => kind switch
    {
        PaletteKind.Command or PaletteKind.Recipe => 4,
        _ => 6
    };

    /// <summary>Пустой запрос: действия и последние чаты — то, за чем палитру открывают чаще.</summary>
    internal const int EmptyActions = 8;

    internal const int EmptyChats = 5;

    public static IReadOnlyList<PaletteMatch> Find(IReadOnlyList<PaletteItem> items, string? query)
    {
        ArgumentNullException.ThrowIfNull(items);
        var words = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return
            [
                .. items.Where(item => item.Kind == PaletteKind.Action).Take(EmptyActions).Select(item => new PaletteMatch(item, 0, 0)),
                .. items.Where(item => item.Kind == PaletteKind.Chat).Take(EmptyChats).Select(item => new PaletteMatch(item, 0, 0))
            ];
        }

        var scored = new List<(PaletteMatch Match, int Score)>();
        foreach (var item in items)
        {
            var title = Normalize(item.Title);
            var path = Normalize(item.Detail);
            if (!words.All(word => title.Contains(word, StringComparison.Ordinal) || path.Contains(word, StringComparison.Ordinal)))
            {
                continue;
            }

            var start = title.IndexOf(words[0], StringComparison.Ordinal);
            var score = start switch
            {
                0 => 300,
                > 0 when WordStart(title, words[0]) is >= 0 => 200,
                > 0 => 100,
                _ => 50
            };

            // Совпадение с начала слова выделяется там, где слово начинается, а не первым
            // вхождением внутри другого слова.
            if (score == 200)
            {
                start = WordStart(title, words[0]);
            }

            // Короче — ближе к запросу: «Тема» выше «Тема оформления по расписанию».
            scored.Add((new PaletteMatch(item, Math.Max(start, 0), start >= 0 ? words[0].Length : 0), (score * 1000) - Math.Min(title.Length, 999)));
        }

        return
        [
            .. scored
                .GroupBy(entry => entry.Match.Item.Kind)
                .OrderBy(group => group.Key)
                .SelectMany(group => group
                    .OrderByDescending(entry => entry.Score)
                    .Take(LimitOf(group.Key))
                    .Select(entry => entry.Match))
        ];
    }

    /// <summary>Регистр и «ё» не различаются; длина строки не меняется — по ней выделяют совпадение.</summary>
    internal static string Normalize(string text) =>
        text.ToLowerInvariant().Replace('ё', 'е');

    private static int WordStart(string title, string word)
    {
        for (var at = title.IndexOf(word, StringComparison.Ordinal); at >= 0; at = title.IndexOf(word, at + 1, StringComparison.Ordinal))
        {
            if (at == 0 || !char.IsLetterOrDigit(title[at - 1]))
            {
                return at;
            }
        }

        return -1;
    }
}
