namespace Amarin.Core;

/// <summary>
/// Отсев ложных формул.
/// </summary>
/// <remarks>
/// Доллар в тексте — это не только формула. В ответах этого приложения регулярно встречаются
/// переменные PowerShell и цены, а разметка видит между двумя долларами формулу. Поэтому
/// содержимое между одинарными <c>$</c> проходит проверку: похоже ли это на математику вообще.
/// Двойные <c>$$</c> проверять незачем — так случайно не пишут.
/// </remarks>
public static class MathDetection
{
    private const string OperatorChars = "=+-−*/<>≤≥≈≠·×÷±∑∫∞";

    public static bool LooksLikeMath(string? content)
    {
        var text = (content ?? "").Trim();
        if (text.Length == 0)
        {
            return false;
        }

        // Двоеточие в формуле почти не встречается, зато с него начинаются $env:PATH,
        // C:\путь и http://адрес — этот случай отсеиваем раньше всего остального.
        if (!text.Contains('\\', StringComparison.Ordinal) && text.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        // Команда, степень, индекс или группа — сомнений не остаётся.
        if (text.Contains('\\', StringComparison.Ordinal) ||
            text.Contains('^', StringComparison.Ordinal) ||
            text.Contains('_', StringComparison.Ordinal) ||
            text.Contains('{', StringComparison.Ordinal))
        {
            return true;
        }

        var hasLetter = text.Any(char.IsLetter);
        var hasDigit = text.Any(char.IsDigit);

        // Одиночное обозначение: $x$, $n$, $R$.
        if (text.Length <= 3 && text.All(char.IsLetterOrDigit))
        {
            return hasLetter;
        }

        // |z|, f(x), (x, y), z' — знаков операций нет, но скобки и одиночные буквы бывают
        // только в формуле. Без этой ветки модуль «$|z|$» показывался сырым вместе с долларами.
        if (IsBareExpression(text))
        {
            return true;
        }

        if (!text.Any(c => OperatorChars.Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        // Только цифры и минус — это диапазон цен, а не выражение.
        return hasLetter || (hasDigit && text.Length > 3);
    }

    private const string StructureChars = "()[]|'′";

    /// <summary>
    /// Выражение из букв, цифр и скобок: каждое слово в нём — одна-две буквы или имя функции.
    /// </summary>
    /// <remarks>
    /// Слово длиннее — уже проза или имя переменной оболочки (<c>$(Get-Date)</c>), и формулой
    /// такое не считается.
    /// </remarks>
    private static bool IsBareExpression(string text)
    {
        var structured = false;
        var run = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetter(c) && (char.IsAsciiLetter(c) || c is >= 'Α' and <= 'ω'))
            {
                if (run == 0)
                {
                    start = i;
                }

                run++;
                continue;
            }

            if (run > 2 && !MathSymbols.TryGet(text.Substring(start, run), out _, out _))
            {
                return false;
            }

            run = 0;
            if (i == text.Length)
            {
                break;
            }

            if (StructureChars.Contains(c, StringComparison.Ordinal))
            {
                structured = true;
            }
            else if (!(char.IsAsciiDigit(c) || c is ',' or '.' or ' '))
            {
                return false;
            }
        }

        return structured && text.Any(char.IsLetter);
    }
}
