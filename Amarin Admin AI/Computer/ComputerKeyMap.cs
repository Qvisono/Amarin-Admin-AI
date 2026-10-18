using System.Collections.Frozen;
using Amarin.Core;

namespace Amarin.Computer;

/// <summary>Имена клавиш ядра (<see cref="ComputerKeys"/>) — в виртуальные коды Windows.</summary>
internal static class ComputerKeyMap
{
    private static readonly FrozenDictionary<string, ushort> Named = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        [ComputerKeys.Ctrl] = 0x11,
        [ComputerKeys.Alt] = 0x12,
        [ComputerKeys.Shift] = 0x10,
        [ComputerKeys.Win] = 0x5B,
        ["enter"] = 0x0D,
        ["escape"] = 0x1B,
        ["tab"] = 0x09,
        ["space"] = 0x20,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["up"] = 0x26,
        ["down"] = 0x28,
        ["left"] = 0x25,
        ["right"] = 0x27,
        ["printscreen"] = 0x2C,
        ["apps"] = 0x5D,
        ["capslock"] = 0x14,
        ["numlock"] = 0x90
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Клавиши «расширенного» блока: без пометки Windows приняла бы стрелку за цифру на цифровом
    /// блоке при выключенном NumLock, а Delete — за точку.
    /// </summary>
    private static readonly FrozenSet<ushort> Extended = new ushort[]
    {
        0x2E, 0x2D, 0x24, 0x23, 0x21, 0x22, 0x26, 0x28, 0x25, 0x27, 0x5B, 0x5D, 0x2C, 0x90
    }.ToFrozenSet();

    public static ushort VirtualKey(string name)
    {
        if (Named.TryGetValue(name, out var vk))
        {
            return vk;
        }

        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length > 1 && name[0] == 'f' &&
            int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var number) &&
            number is >= 1 and <= 24)
        {
            return (ushort)(0x70 + number - 1);
        }

        // Ядро пропускает только разобранные им имена; сюда незнакомое не доходит.
        throw new ArgumentException("Unknown key: " + name, nameof(name));
    }

    public static bool IsExtended(ushort vk) => Extended.Contains(vk);
}
