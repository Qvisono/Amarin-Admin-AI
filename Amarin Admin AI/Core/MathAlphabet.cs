using System.Text;

namespace Amarin.Core;

/// <summary>
/// Перевод латиницы в математические начертания Unicode: <c>\mathbb{R}</c> — это не буква R
/// жирным, а отдельный знак ℝ. Полужирное и курсивное начертания шрифт делает сам, поэтому
/// здесь только те два стиля, которых иначе не получить.
/// </summary>
public static class MathAlphabet
{
    public static string Convert(string text, MathStyleKind style)
    {
        if (string.IsNullOrEmpty(text) || style is not (MathStyleKind.Blackboard or MathStyleKind.Calligraphic))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            builder.Append(style == MathStyleKind.Blackboard ? Blackboard(c) : Calligraphic(c));
        }

        return builder.ToString();
    }

    private static string Blackboard(char c)
    {
        // Восемь букв Unicode вынес в основной блок задолго до появления остальных.
        var special = c switch
        {
            'C' => "ℂ",
            'H' => "ℍ",
            'N' => "ℕ",
            'P' => "ℙ",
            'Q' => "ℚ",
            'R' => "ℝ",
            'Z' => "ℤ",
            _ => null
        };

        if (special is not null)
        {
            return special;
        }

        return c switch
        {
            >= 'A' and <= 'Z' => char.ConvertFromUtf32(0x1D538 + (c - 'A')),
            >= 'a' and <= 'z' => char.ConvertFromUtf32(0x1D552 + (c - 'a')),
            >= '0' and <= '9' => char.ConvertFromUtf32(0x1D7D8 + (c - '0')),
            _ => c.ToString()
        };
    }

    private static string Calligraphic(char c)
    {
        var special = c switch
        {
            'B' => "ℬ",
            'E' => "ℰ",
            'F' => "ℱ",
            'H' => "ℋ",
            'I' => "ℐ",
            'L' => "ℒ",
            'M' => "ℳ",
            'R' => "ℛ",
            'e' => "ℯ",
            'g' => "ℊ",
            'o' => "ℴ",
            _ => null
        };

        if (special is not null)
        {
            return special;
        }

        return c switch
        {
            >= 'A' and <= 'Z' => char.ConvertFromUtf32(0x1D49C + (c - 'A')),
            >= 'a' and <= 'z' => char.ConvertFromUtf32(0x1D4B6 + (c - 'a')),
            _ => c.ToString()
        };
    }
}
