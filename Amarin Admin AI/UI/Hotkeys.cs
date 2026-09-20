using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Перевод сочетания клавиш между записью в настройках и нажатием в окне.
/// </summary>
/// <remarks>
/// Отделено от <see cref="HotkeyMap"/>, потому что там нет и не должно быть WPF: правила о
/// записи проверяются тестами без окна, а здесь остаётся только перевод в <see cref="Key"/> и
/// <see cref="ModifierKeys"/> и обратно.
/// </remarks>
internal static class Hotkeys
{
    /// <summary>
    /// Клавиши, которые сочетанием быть не могут.
    /// </summary>
    /// <remarks>
    /// Сами модификаторы — потому что нажаты вместе с любым сочетанием. Esc — потому что им
    /// отменяют запись и закрывают приближённую ленту. Tab и системные клавиши — потому что
    /// ими ходят по интерфейсу, и отнимать их у клавиатуры нельзя.
    /// </remarks>
    private static readonly Key[] Unusable =
    [
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt,
        Key.LeftShift, Key.RightShift, Key.LWin, Key.RWin,
        Key.System, Key.Escape, Key.Tab, Key.Apps, Key.None,
        Key.ImeProcessed, Key.DeadCharProcessed
    ];

    /// <summary>Нажатие подходит на роль сочетания: есть модификатор и полезная клавиша.</summary>
    public static bool TryRecord(Key key, ModifierKeys modifiers, [NotNullWhen(true)] out string? gesture)
    {
        gesture = null;

        // Alt приходит в Key.System, а настоящая клавиша — в SystemKey; разбирать её должен
        // вызывающий, здесь это уже развёрнутое значение. Требование Ctrl или Alt проверяет
        // сам HotkeyMap — там же оно и объяснено.
        if (Unusable.Contains(key))
        {
            return false;
        }

        return HotkeyMap.TryParse(Write(key, modifiers), out gesture);
    }

    /// <summary>Нажатие — это записанное сочетание.</summary>
    public static bool Matches(string gesture, Key key, ModifierKeys modifiers)
    {
        if (Unusable.Contains(key) || !HotkeyMap.TryParse(gesture, out var expected))
        {
            return false;
        }

        // Без учёта регистра: длинные имена клавиш в settings.json человек мог поправить руками,
        // и «numpad1» обязан остаться той же клавишей, что «NumPad1».
        return HotkeyMap.TryParse(Write(key, modifiers), out var pressed) &&
               string.Equals(expected, pressed, StringComparison.OrdinalIgnoreCase);
    }

    private static string Write(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(Name(key));
        return string.Join('+', parts);
    }

    /// <summary>
    /// Имя клавиши в записи.
    /// </summary>
    /// <remarks>
    /// Цифровой ряд у WPF зовётся <c>D1</c>, а человек ждёт «1»; цифровая клавиатура остаётся
    /// <c>NumPad1</c> — это вправду другая клавиша, и путать их нельзя.
    /// </remarks>
    private static string Name(Key key)
    {
        var raw = key.ToString();
        if (raw.Length == 2 && raw[0] == 'D' && char.IsAsciiDigit(raw[1]))
        {
            return raw[1].ToString(CultureInfo.InvariantCulture);
        }

        return raw;
    }
}
