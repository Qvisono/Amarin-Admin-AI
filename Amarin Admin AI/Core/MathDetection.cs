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

        if (!text.Any(c => OperatorChars.Contains(c, StringComparison.Ordinal)))
        {
            return false;
        }

        // Только цифры и минус — это диапазон цен, а не выражение.
        return hasLetter || (hasDigit && text.Length > 3);
    }
}
