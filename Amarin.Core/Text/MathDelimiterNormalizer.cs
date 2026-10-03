using System.Text;

namespace Amarin.Core;

/// <summary>
/// Приводит формулы к долларам.
/// </summary>
/// <remarks>
/// Модели пишут формулы двумя способами: <c>$…$</c> и <c>\(…\)</c> для строки, <c>$$…$$</c> и
/// <c>\[…\]</c> для отдельной. Разметка понимает только доллары, поэтому вторая пара
/// переводится в первую до разбора. Внутри блоков кода и коротких вставок в обратных кавычках
/// ничего не трогаем: там <c>\(</c> — это регулярное выражение, а не формула.
/// <para>
/// Тем же проходом ищутся формулы, оставленные вовсе без разделителей (<see cref="MathAutoWrap"/>):
/// ограды кода уже отслеживаются здесь, и второй проход по строкам повторял бы ту же работу.
/// </para>
/// </remarks>
public static class MathDelimiterNormalizer
{
    public static string ToDollars(string? text)
    {
        var source = text ?? "";
        if (!MathAutoWrap.MayContainMath(source))
        {
            return source;
        }

        var result = new StringBuilder(source.Length + 16);
        var fence = "";

        // Внутри выключной формулы на несколько строк: её середина долларов не несёт, и без
        // этого флага MathAutoWrap завернул бы её второй раз — прямо внутри $$…$$.
        var display = false;
        var first = true;

        foreach (var line in source.Split('\n'))
        {
            // Флагом, а не по длине результата: иначе пустая первая строка теряла свой перевод.
            if (!first)
            {
                result.Append('\n');
            }

            first = false;

            var opener = FenceMarker(line);
            if (fence.Length > 0)
            {
                // Внутри блока кода — только ищем его конец.
                if (opener.Length > 0 && opener[0] == fence[0] && opener.Length >= fence.Length)
                {
                    fence = "";
                }

                result.Append(line);
                continue;
            }

            if (opener.Length > 0)
            {
                fence = opener;
                result.Append(line);
                continue;
            }

            var converted = new StringBuilder(line.Length + 8);
            AppendConverted(converted, line);
            var mathLine = converted.ToString();

            var pairs = CountDisplayDelimiters(mathLine);
            if (display || pairs > 0)
            {
                result.Append(mathLine);
            }
            else
            {
                result.Append(MathAutoWrap.WrapLine(mathLine));
            }

            if (pairs % 2 == 1)
            {
                display = !display;
            }
        }

        // Ничего не поменялось — отдаём исходный объект: вызывающие сравнивают по ссылке,
        // чтобы не разбирать разметку заново.
        var output = result.ToString();
        return string.Equals(output, source, StringComparison.Ordinal) ? source : output;
    }

    private static int CountDisplayDelimiters(string line)
    {
        var count = 0;
        for (var at = line.IndexOf("$$", StringComparison.Ordinal);
             at >= 0;
             at = line.IndexOf("$$", at + 2, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>Открывающая или закрывающая ограда блока кода: <c>```</c> либо <c>~~~</c>.</summary>
    private static string FenceMarker(string line)
    {
        var text = line.TrimStart();
        var indent = line.Length - text.Length;
        if (indent > 3 || text.Length < 3)
        {
            return "";
        }

        var marker = text[0];
        if (marker is not ('`' or '~'))
        {
            return "";
        }

        var count = 0;
        while (count < text.Length && text[count] == marker)
        {
            count++;
        }

        return count >= 3 ? new string(marker, count) : "";
    }

    private static void AppendConverted(StringBuilder result, string line)
    {
        var index = 0;
        while (index < line.Length)
        {
            var c = line[index];

            if (c == '`')
            {
                // Вставка кода целиком, вместе с её кавычками.
                var run = 0;
                while (index + run < line.Length && line[index + run] == '`')
                {
                    run++;
                }

                var ticks = new string('`', run);
                var close = line.IndexOf(ticks, index + run, StringComparison.Ordinal);
                var end = close < 0 ? line.Length : close + run;
                result.Append(line, index, end - index);
                index = end;
                continue;
            }

            if (c != '\\' || index + 1 >= line.Length)
            {
                result.Append(c);
                index++;
                continue;
            }

            switch (line[index + 1])
            {
                case '(' or ')':
                    result.Append('$');
                    break;

                case '[' or ']':
                    result.Append("$$");
                    break;

                default:
                    // Любая другая экранированная пара уходит как есть — в том числе «\\»,
                    // иначе следующий проход принял бы её вторую косую за начало формулы.
                    result.Append(c).Append(line[index + 1]);
                    break;
            }

            index += 2;
        }
    }
}
