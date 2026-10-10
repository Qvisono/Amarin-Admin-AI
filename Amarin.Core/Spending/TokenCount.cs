using System.Globalization;

namespace Amarin.Core;

/// <summary>
/// Число токенов для человека: как его показать и как прочесть набранное.
/// </summary>
/// <remarks>
/// Пакеты у перепродавцов считают миллионами, и «10 000 000» в строке ключа — то же, что
/// «10 млн», только втрое длиннее. Набирают лимит тоже по-разному: с пробелами, с «M» на конце
/// или по-русски «млн», — и все эти записи значат одно число.
/// </remarks>
internal static class TokenCount
{
    /// <summary>До миллиона — цифрами с пробелами между разрядами, дальше — миллионами.</summary>
    public static string Format(long tokens)
    {
        if (tokens >= 1_000_000)
        {
            var millions = Math.Round(tokens / 1_000_000m, tokens >= 100_000_000 ? 0 : 2);
            return Loc.Format("S.Tokens.Millions", millions.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ','));
        }

        return Math.Max(0, tokens).ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');
    }

    /// <summary>
    /// Читает лимит. Пусто — лимита нет (<paramref name="limit"/> = <c>null</c>). <c>false</c> — это
    /// не число токенов.
    /// </summary>
    public static bool TryParseLimit(string? text, out long? limit)
    {
        limit = null;
        var compact = new string((text ?? "")
            .Where(symbol => !char.IsWhiteSpace(symbol) && symbol is not ('_' or '\''))
            .ToArray())
            .ToLowerInvariant();

        if (compact.Length == 0)
        {
            return true;
        }

        var (digits, scale) = SplitScale(compact);

        // Запятая и точка — дробь у «1,5 млн»; у числа без множителя это разделитель разрядов.
        digits = scale == 1 ? digits.Replace(",", "").Replace(".", "") : digits.Replace(',', '.');
        if (!decimal.TryParse(digits, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            return false;
        }

        var tokens = value * scale;
        if (tokens < 1 || tokens > long.MaxValue / 2 || tokens != decimal.Truncate(tokens))
        {
            return false;
        }

        limit = (long)tokens;
        return true;
    }

    /// <summary>Множители, которыми пишут пакеты: латиницей и по-русски.</summary>
    private static readonly (string Suffix, long Scale)[] Scales =
    [
        ("млн", 1_000_000), ("тыс", 1_000), ("m", 1_000_000), ("k", 1_000), ("м", 1_000_000), ("к", 1_000)
    ];

    private static (string Digits, long Scale) SplitScale(string text)
    {
        foreach (var (suffix, scale) in Scales)
        {
            var trimmed = text.TrimEnd('.');
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                return (trimmed[..^suffix.Length], scale);
            }
        }

        return (text, 1);
    }
}
