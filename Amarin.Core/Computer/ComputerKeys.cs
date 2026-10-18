using System.Collections.Frozen;

namespace Amarin.Core;

/// <summary>
/// Сочетание клавиш от модели — в имена, которые понимает порт: модификаторы первыми, одна
/// обычная клавиша последней.
/// </summary>
/// <remarks>
/// Модели пишут одно и то же по-разному (Ctrl, control, ⌃; Return, Enter; Del, Delete), а
/// правила безопасности сравнивают имена. Без одного словаря Win+R прошёл бы как «super+r».
/// </remarks>
internal static class ComputerKeys
{
    public const string Ctrl = "ctrl";
    public const string Alt = "alt";
    public const string Shift = "shift";
    public const string Win = "win";

    private static readonly FrozenSet<string> Modifiers =
        new[] { Ctrl, Alt, Shift, Win }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["control"] = Ctrl,
        ["ctl"] = Ctrl,
        ["option"] = Alt,
        ["menu"] = "apps",
        ["windows"] = Win,
        ["super"] = Win,
        ["meta"] = Win,
        ["cmd"] = Win,
        ["lwin"] = Win,
        ["return"] = "enter",
        ["esc"] = "escape",
        ["del"] = "delete",
        ["ins"] = "insert",
        ["bksp"] = "backspace",
        ["back"] = "backspace",
        ["pgup"] = "pageup",
        ["pgdn"] = "pagedown",
        ["page_up"] = "pageup",
        ["page_down"] = "pagedown",
        ["arrowup"] = "up",
        ["arrowdown"] = "down",
        ["arrowleft"] = "left",
        ["arrowright"] = "right",
        ["prtsc"] = "printscreen",
        ["context_menu"] = "apps"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> Named = new[]
    {
        "enter", "escape", "tab", "space", "backspace", "delete", "insert", "home", "end", "pageup",
        "pagedown", "up", "down", "left", "right", "printscreen", "apps", "capslock", "numlock"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Разбирает «ctrl+shift+s», «Enter», «alt+f4». Null — в строке есть незнакомая клавиша или
    /// две обычных: такое сочетание не нажать одним разом.
    /// </summary>
    public static IReadOnlyList<string>? Parse(string? text)
    {
        var parts = (text ?? "").Split(['+', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var modifiers = new List<string>();
        string? key = null;
        foreach (var part in parts)
        {
            var name = Normalize(part);
            if (name is null)
            {
                return null;
            }

            if (Modifiers.Contains(name))
            {
                if (!modifiers.Contains(name))
                {
                    modifiers.Add(name);
                }

                continue;
            }

            if (key is not null)
            {
                return null;
            }

            key = name;
        }

        // Порядок модификаторов — один на всех: правила и тесты сравнивают сочетания строкой.
        var ordered = new List<string>(modifiers.Count + 1);
        ordered.AddRange(new[] { Ctrl, Alt, Shift, Win }.Where(modifiers.Contains));
        if (key is not null)
        {
            ordered.Add(key);
        }

        return ordered;
    }

    /// <summary>Сочетание одной строкой: «ctrl+shift+s».</summary>
    public static string Join(IReadOnlyList<string> chord) => string.Join('+', chord);

    public static bool IsModifier(string name) => Modifiers.Contains(name);

    private static string? Normalize(string part)
    {
        var lower = part.ToLowerInvariant();
        if (Aliases.TryGetValue(lower, out var alias))
        {
            return alias;
        }

        if (Modifiers.Contains(lower) || Named.Contains(lower))
        {
            return lower;
        }

        if (lower.Length == 1 && char.IsAsciiLetterOrDigit(lower[0]))
        {
            return lower;
        }

        // F1–F24.
        if (lower.Length is 2 or 3 && lower[0] == 'f' &&
            int.TryParse(lower.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) &&
            number is >= 1 and <= 24)
        {
            return lower;
        }

        return null;
    }
}
