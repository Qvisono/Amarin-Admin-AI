using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Core;

/// <summary>
/// Находит формулы, которые модель оставила без долларов, и заворачивает их в <c>$…$</c>.
/// </summary>
/// <remarks>
/// <para>
/// Правило «формула — в долларах» модели соблюдают через раз: в одном ответе рядом с
/// нарисованной формулой стоит строка голым LaTeX (<c>z = 2(\cos \frac{\pi}{3} + …)</c>) или
/// обычным текстом (<c>cos π/3</c>), и человек видит её сырой. Разметка понимает только
/// доллары, поэтому здесь, до разбора, доллары и дописываются.
/// </para>
/// <para>
/// Ошибиться в сторону «формулы» дороже, чем пропустить её: превращённая в формулу строка
/// PowerShell, путь или дата ломают ответ сильнее, чем одна сырая строка. Поэтому правил три,
/// и все осторожные:
/// </para>
/// <list type="number">
/// <item>голая команда LaTeX из известных (<c>\frac</c>, <c>\pi</c>, <c>\sqrt</c>…) — отрезок
/// вокруг неё до первого слова прозы;</item>
/// <item>строка (или её часть после подписи «…: »), целиком состоящая из математики и с явным
/// её признаком — греческой буквой, функцией, степенью, корнем или равенством с буквой;</item>
/// <item>посреди прозы — подряд идущие слова без прозы между ними, прошедшие тот же фильтр,
/// что и целая строка (<c>угол 3π/4</c>, <c>если x = 5, то</c>).</item>
/// </list>
/// <para>
/// Строки с долларом не трогаются вовсе — там уже есть формулы или цены с переменными
/// оболочки, и дописанный доллар спутал бы пары. Код в обратных кавычках — тоже. Ограды блоков
/// кода отсекает вызывающий (<see cref="MathDelimiterNormalizer"/>).
/// </para>
/// </remarks>
public static class MathAutoWrap
{
    /// <summary>Имена функций, которые в обычном тексте пишут без обратной косой.</summary>
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "sin", "cos", "tan", "tg", "cot", "ctg", "sec", "csc",
        "arcsin", "arccos", "arctan", "arctg", "arcctg",
        "sinh", "cosh", "tanh", "sh", "ch", "th",
        "ln", "lg", "log", "exp", "lim", "max", "min", "arg", "det", "mod", "Re", "Im"
    };

    /// <summary>Команды разметки, которых нет в таблице символов, но которые бывают только в формулах.</summary>
    private static readonly HashSet<string> StructuralCommands = new(StringComparer.Ordinal)
    {
        "frac", "dfrac", "tfrac", "cfrac", "sqrt", "left", "right", "binom", "over",
        "mathbb", "mathbf", "mathrm", "mathcal", "overline", "hat", "bar", "vec", "tilde",
        "dot", "ddot", "operatorname", "cdots", "ldots", "dots"
    };

    private static readonly Dictionary<char, string> UnicodeOperators = new()
    {
        ['−'] = "-",
        ['×'] = "\\times",
        ['·'] = "\\cdot",
        ['*'] = "\\cdot",
        ['÷'] = "\\div",
        ['≤'] = "\\le",
        ['≥'] = "\\ge",
        ['≠'] = "\\ne",
        ['≈'] = "\\approx",
        ['±'] = "\\pm",
        ['∓'] = "\\mp",
        ['∞'] = "\\infty",
        ['→'] = "\\to"
    };

    private const string Superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";

    /// <summary>Знаки, из которых может состоять формула, кроме букв и цифр.</summary>
    private const string MathMarks = "\\{}()[]^_=+-*/<>|!'.,&−×·÷≤≥≠≈±∓√∞∑∏∫∂∇→←⇒⇔∈∉⊂⊆∪∩°…⁰¹²³⁴⁵⁶⁷⁸⁹";

    private const string OperatorOnly = "=+-*/<>−×·÷≤≥≠≈±∓→←⇒⇔∈∉⊂⊆";

    private static readonly Regex ListPrefix = new(
        @"^(?:\s*(?:[-*+]|\d{1,3}[.)])\s+|\s*>\s?)+",
        RegexOptions.CultureInvariant);

    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s", RegexOptions.CultureInvariant);

    private static readonly Regex Word = new(@"\S+", RegexOptions.CultureInvariant);

    /// <summary>Быстрый отсев: в строке нет ничего, с чего могла бы начаться формула.</summary>
    public static bool MayContainMath(string line)
    {
        foreach (var c in line)
        {
            if (c is '\\' or '/' or '=' or '^' or '√' || IsGreek(c) || Superscripts.Contains(c))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Возвращает строку с дописанными долларами вокруг найденных формул.</summary>
    /// <param name="line">Одна строка ответа вне блока кода.</param>
    public static string WrapLine(string line)
    {
        if (line.Length == 0 ||
            line.Contains('$', StringComparison.Ordinal) ||
            Heading.IsMatch(line) ||
            !MayContainMath(line))
        {
            return line;
        }

        if (line.TrimStart().StartsWith('|'))
        {
            return WrapTableRow(line);
        }

        // Вставки кода в обратных кавычках остаются как есть: формулу ищем только вне их.
        if (line.Contains('`', StringComparison.Ordinal))
        {
            return MapOutsideCode(line, WrapInline);
        }

        var prefix = ListPrefix.Match(line) is { Success: true } marker ? marker.Value : "";
        var body = line[prefix.Length..];
        return prefix + (TryWrapWholeFormula(body) ?? WrapInline(body));
    }

    // ───────────────────────── разбиение строки ─────────────────────────

    private static string WrapTableRow(string line)
    {
        var cells = line.Split('|');
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i];
            var trimmed = cell.Trim();
            if (trimmed.Length == 0 || !MayContainMath(trimmed))
            {
                continue;
            }

            var lead = cell[..(cell.Length - cell.TrimStart().Length)];
            var tail = cell[cell.TrimEnd().Length..];
            var wrapped = trimmed.Contains('`', StringComparison.Ordinal)
                ? MapOutsideCode(trimmed, WrapInline)
                : TryWrapWholeFormula(trimmed) ?? WrapInline(trimmed);
            cells[i] = lead + wrapped + tail;
        }

        return string.Join('|', cells);
    }

    private static string MapOutsideCode(string line, Func<string, string> map)
    {
        var result = new StringBuilder(line.Length + 8);
        var index = 0;
        while (index < line.Length)
        {
            var tick = line.IndexOf('`', index);
            if (tick < 0)
            {
                result.Append(map(line[index..]));
                break;
            }

            result.Append(map(line[index..tick]));

            var run = 0;
            while (tick + run < line.Length && line[tick + run] == '`')
            {
                run++;
            }

            var close = line.IndexOf(new string('`', run), tick + run, StringComparison.Ordinal);
            var end = close < 0 ? line.Length : close + run;
            result.Append(line, tick, end - tick);
            index = end;
        }

        return result.ToString();
    }

    // ───────────────────────── правило 2: строка-формула ─────────────────────────

    /// <summary>
    /// Весь текст — формула обычными символами. Подпись перед двоеточием и выделение
    /// жирным остаются снаружи.
    /// </summary>
    private static string? TryWrapWholeFormula(string body)
    {
        if (body.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        if (WrapFormulaCore(body) is { } whole)
        {
            return whole;
        }

        var colon = body.LastIndexOf(": ", StringComparison.Ordinal);
        if (colon > 0 && WrapFormulaCore(body[(colon + 2)..]) is { } tail)
        {
            return body[..(colon + 2)] + tail;
        }

        return null;
    }

    private static string? WrapFormulaCore(string text)
    {
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var core = text.Trim();

        // Точка или запятая в конце — это конец предложения, а не часть формулы.
        var end = "";
        while (core.Length > 0 && core[^1] is '.' or ',' or ';')
        {
            end = core[^1] + end;
            core = core[..^1].TrimEnd();
        }

        var wrapper = "";
        foreach (var mark in (string[])["**", "__", "*", "_"])
        {
            if (core.Length > mark.Length * 2 && core.StartsWith(mark, StringComparison.Ordinal) &&
                core.EndsWith(mark, StringComparison.Ordinal))
            {
                wrapper = mark;
                core = core[mark.Length..^mark.Length].Trim();
                break;
            }
        }

        if (PlainToLatex(core) is not { } latex)
        {
            return null;
        }

        return lead + wrapper + "$" + latex + "$" + wrapper + end;
    }

    private enum Kind
    {
        Number,
        Letter,
        Function,
        Operator,
        Open,
        Close,
        Superscript,
        Root
    }

    private readonly record struct Token(Kind Kind, string Text);

    /// <summary>
    /// Переводит формулу, набранную обычным текстом, в LaTeX; <c>null</c> — это не формула.
    /// </summary>
    /// <remarks>
    /// Формулой считается текст только из чисел, одиночных букв, известных функций, знаков и
    /// скобок — и с явным признаком математики. Одна кириллическая буква или слово длиннее
    /// трёх латинских букв — и это уже проза.
    /// </remarks>
    internal static string? PlainToLatex(string text)
    {
        if (!TryLex(text, out var tokens) || tokens.Count < 2)
        {
            return null;
        }

        var greek = false;
        var function = false;
        var equals = false;
        var letter = false;
        var strongMark = false;
        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case Kind.Function:
                    function = true;
                    break;
                case Kind.Letter:
                    letter = true;
                    greek |= IsGreek(token.Text[0]);
                    break;
                case Kind.Superscript or Kind.Root:
                    strongMark = true;
                    break;
                case Kind.Operator:
                    equals |= token.Text is "=";
                    strongMark |= token.Text is "^" or "≤" or "≥" or "≠" or "≈";
                    break;
            }
        }

        if (!(greek || function || strongMark || (equals && letter)))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length + 16);
        AppendLatex(builder, tokens, 0, tokens.Count);
        return builder.ToString();
    }

    /// <summary>Переводит звенья <paramref name="from"/>..<paramref name="to"/> (не включая) в LaTeX.</summary>
    private static void AppendLatex(StringBuilder builder, List<Token> tokens, int from, int to)
    {
        var first = builder.Length;
        for (var i = from; i < to; i++)
        {
            var token = tokens[i];
            if (builder.Length > first && token.Kind is not (Kind.Superscript or Kind.Close))
            {
                builder.Append(' ');
            }

            switch (token.Kind)
            {
                case Kind.Function:
                    builder.Append('\\').Append(token.Text);
                    break;

                case Kind.Superscript:
                    builder.Append("^{").Append(token.Text).Append('}');
                    break;

                case Kind.Root:
                    i = AppendRoot(builder, tokens, i, to);
                    break;

                case Kind.Operator when UnicodeOperators.TryGetValue(token.Text[0], out var command):
                    builder.Append(command);
                    break;

                default:
                    builder.Append(token.Text);
                    break;
            }
        }
    }

    /// <summary>√x и √(…) — корень из следующего звена; скобки звена уходят в черту корня.</summary>
    /// <returns>Индекс последнего звена, вошедшего под корень.</returns>
    private static int AppendRoot(StringBuilder builder, List<Token> tokens, int root, int to)
    {
        var next = root + 1;
        builder.Append("\\sqrt{");
        if (next >= to)
        {
            builder.Append('}');
            return root;
        }

        var last = next;
        if (tokens[next] is { Kind: Kind.Open, Text: "(" })
        {
            var depth = 0;
            for (var j = next; j < to; j++)
            {
                depth += tokens[j].Kind switch { Kind.Open => 1, Kind.Close => -1, _ => 0 };
                if (depth == 0)
                {
                    AppendLatex(builder, tokens, next + 1, j);
                    builder.Append('}');
                    return j;
                }
            }

            // Скобка не закрылась — под корень уходит всё до конца.
            AppendLatex(builder, tokens, next + 1, to);
            builder.Append('}');
            return to - 1;
        }

        // Показатель, прилипший к звену, остаётся над корнем: √x² — это корень из x².
        if (last + 1 < to && tokens[last + 1].Kind == Kind.Superscript)
        {
            last++;
        }

        AppendLatex(builder, tokens, next, last + 1);
        builder.Append('}');
        return last;
    }

    private static bool TryLex(string text, out List<Token> tokens)
    {
        tokens = [];
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsAsciiDigit(c))
            {
                var start = i;
                while (i < text.Length &&
                       (char.IsAsciiDigit(text[i]) ||
                        (text[i] is '.' or ',' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))))
                {
                    i++;
                }

                // «2,5» — десятичная запятая, а не перечисление: в фигурных скобках LaTeX не
                // ставит после неё пробел.
                tokens.Add(new Token(Kind.Number, text[start..i].Replace(",", "{,}", StringComparison.Ordinal)));
                continue;
            }

            if (IsFormulaLetter(c))
            {
                var start = i;
                while (i < text.Length && IsFormulaLetter(text[i]))
                {
                    i++;
                }

                if (!TryLexLetters(text[start..i], tokens))
                {
                    return false;
                }

                // x1, a12 — индекс, набранный без подчёркивания.
                if (i < text.Length && char.IsAsciiDigit(text[i]) && tokens[^1].Kind == Kind.Letter)
                {
                    var digits = i;
                    while (i < text.Length && char.IsAsciiDigit(text[i]))
                    {
                        i++;
                    }

                    tokens[^1] = tokens[^1] with { Text = tokens[^1].Text + "_{" + text[digits..i] + "}" };
                }

                continue;
            }

            if (Superscripts.Contains(c))
            {
                var digits = new StringBuilder();
                while (i < text.Length && Superscripts.IndexOf(text[i]) is var at and >= 0)
                {
                    digits.Append((char)('0' + at));
                    i++;
                }

                tokens.Add(new Token(Kind.Superscript, digits.ToString()));
                continue;
            }

            Token? mark = c switch
            {
                '(' or '[' or '{' => new Token(Kind.Open, c.ToString()),
                ')' or ']' or '}' => new Token(Kind.Close, c.ToString()),
                '√' => new Token(Kind.Root, "√"),
                '=' or '+' or '-' or '/' or '^' or '<' or '>' or '|' or '!' or '\'' or ',' or '.' or
                    '−' or '×' or '·' or '*' or '÷' or '≤' or '≥' or '≠' or '≈' or '±' or '∓' or '∞' or '→'
                    => new Token(Kind.Operator, c.ToString()),
                _ => null
            };

            if (mark is null)
            {
                return false;
            }

            tokens.Add(mark.Value);
            i++;
        }

        return true;
    }

    /// <summary>
    /// Разбирает сплошную группу букв: функция, «i» перед функцией, одиночные переменные подряд.
    /// </summary>
    /// <remarks>
    /// Больше трёх переменных подряд без знаков между ними в формулах не пишут — это слово, и
    /// строка с ним формулой не считается.
    /// </remarks>
    private static bool TryLexLetters(string run, List<Token> tokens)
    {
        if (Functions.Contains(run))
        {
            tokens.Add(new Token(Kind.Function, run));
            return true;
        }

        // «isin», «icos» — мнимая единица, прилипшая к функции.
        if (run.Length > 1 && run[0] == 'i' && Functions.Contains(run[1..]))
        {
            tokens.Add(new Token(Kind.Letter, "i"));
            tokens.Add(new Token(Kind.Function, run[1..]));
            return true;
        }

        if (run.Length > 3)
        {
            return false;
        }

        foreach (var c in run)
        {
            tokens.Add(new Token(Kind.Letter, c.ToString()));
        }

        return true;
    }

    // ───────────────────────── правила 1 и 3: внутри прозы ─────────────────────────

    /// <summary>Голый LaTeX и формулы обычным текстом посреди прозы.</summary>
    private static string WrapInline(string text)
    {
        if (text.Length == 0 || !MayContainMath(text))
        {
            return text;
        }

        return text.Contains('\\', StringComparison.Ordinal)
            ? WrapBareLatex(text)
            : WrapPlainRuns(text);
    }

    private static string WrapBareLatex(string text)
    {
        var words = Word.Matches(text);
        if (words.Count == 0)
        {
            return text;
        }

        var result = new StringBuilder(text.Length + 8);
        var copied = 0;

        // Слова левее уже ушли в предыдущую формулу, и расширяться туда нельзя.
        var floor = 0;
        var i = 0;
        while (i < words.Count)
        {
            if (!HasKnownCommand(words[i].Value) || !IsMathWord(words[i].Value))
            {
                i++;
                continue;
            }

            var first = i;
            while (first > floor && IsMathWord(words[first - 1].Value) && !IsProseWord(words[first - 1].Value))
            {
                first--;
            }

            var last = i;
            while (last + 1 < words.Count && IsMathWord(words[last + 1].Value) && !IsProseWord(words[last + 1].Value))
            {
                last++;
            }

            // Фигурные скобки обязаны закрыться внутри формулы: \text{где x} тянет её дальше.
            while (last + 1 < words.Count && BraceDepth(words, first, last) > 0)
            {
                last++;
            }

            // Отрезок не начинают и не заканчивают голым знаком: «Итого = \frac12» — это
            // «Итого =» и формула, а не формула, начатая с равенства.
            while (first < i && IsOperatorOnly(words[first].Value))
            {
                first++;
            }

            while (last > i && IsOperatorOnly(words[last].Value))
            {
                last--;
            }

            var start = words[first].Index;
            var end = words[last].Index + words[last].Length;

            // Знак препинания в конце — конец предложения.
            while (end > start && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?')
            {
                end--;
            }

            (start, end) = TrimUnbalancedParens(text, start, end);
            if (end <= start)
            {
                i = last + 1;
                continue;
            }

            result.Append(text, copied, start - copied);
            result.Append('$').Append(text, start, end - start).Append('$');
            copied = end;
            i = last + 1;
            floor = i;
        }

        result.Append(text, copied, text.Length - copied);
        return result.ToString();
    }

    private static int BraceDepth(MatchCollection words, int first, int last)
    {
        var depth = 0;
        for (var i = first; i <= last; i++)
        {
            foreach (var c in words[i].Value)
            {
                depth += c switch { '{' => 1, '}' => -1, _ => 0 };
            }
        }

        return depth;
    }

    /// <summary>
    /// Снимает с краёв отрезка скобку без пары: «(где \alpha)» — скобка принадлежит прозе.
    /// </summary>
    private static (int Start, int End) TrimUnbalancedParens(string text, int start, int end)
    {
        while (end > start)
        {
            var open = 0;
            var close = 0;
            for (var k = start; k < end; k++)
            {
                open += text[k] == '(' ? 1 : 0;
                close += text[k] == ')' ? 1 : 0;
            }

            if (close > open && text[end - 1] == ')')
            {
                end--;
            }
            else if (open > close && text[start] == '(')
            {
                start++;
            }
            else
            {
                break;
            }
        }

        return (start, end);
    }

    private static bool HasKnownCommand(string word)
    {
        for (var i = 0; i < word.Length - 1; i++)
        {
            if (word[i] != '\\' || !char.IsAsciiLetter(word[i + 1]))
            {
                continue;
            }

            var end = i + 1;
            while (end < word.Length && char.IsAsciiLetter(word[end]))
            {
                end++;
            }

            var name = word[(i + 1)..end];
            if (StructuralCommands.Contains(name) || MathSymbols.TryGet(name, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Слово может быть частью формулы: только буквы, цифры и математические знаки.</summary>
    private static bool IsMathWord(string word)
    {
        foreach (var c in word)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || IsGreek(c) || MathMarks.Contains(c) ||
                  c is ';' or ':' or '?'))
            {
                return false;
            }
        }

        // Двоеточие внутри слова — путь или адрес (C:\, http://), а не формула.
        var colon = word.IndexOf(':');
        return colon < 0 || colon == word.Length - 1;
    }

    /// <summary>Слово прозы: сплошные латинские буквы, не функция и не одна переменная.</summary>
    private static bool IsProseWord(string word)
    {
        var letters = word.TrimEnd('.', ',', ';', ':', '!', '?');
        if (letters.Length < 2 || !letters.All(char.IsAsciiLetter))
        {
            return false;
        }

        return !Functions.Contains(letters);
    }

    private static bool IsOperatorOnly(string word) => word.All(c => OperatorOnly.Contains(c));

    /// <summary>
    /// Формулы обычным текстом посреди фразы: «угол 3π/4», «если x = 5, то».
    /// </summary>
    /// <remarks>
    /// Кандидат — подряд идущие слова без прозы между ними; формулой он становится, только если
    /// его принимает <see cref="PlainToLatex"/>, то есть с тем же явным признаком математики,
    /// что и у целой строки.
    /// </remarks>
    private static string WrapPlainRuns(string text)
    {
        var words = Word.Matches(text);
        var result = new StringBuilder(text.Length + 8);
        var copied = 0;
        var i = 0;
        while (i < words.Count)
        {
            if (!IsMathWord(words[i].Value) || IsProseWord(words[i].Value))
            {
                i++;
                continue;
            }

            var first = i;
            var last = i;
            while (last + 1 < words.Count && IsMathWord(words[last + 1].Value) && !IsProseWord(words[last + 1].Value))
            {
                last++;
            }

            i = last + 1;

            while (first < last && IsOperatorOnly(words[first].Value))
            {
                first++;
            }

            while (last > first && IsOperatorOnly(words[last].Value))
            {
                last--;
            }

            var start = words[first].Index;
            var end = words[last].Index + words[last].Length;
            while (end > start && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?')
            {
                end--;
            }

            (start, end) = TrimUnbalancedParens(text, start, end);
            if (end <= start || PlainToLatex(text[start..end]) is not { } latex)
            {
                continue;
            }

            result.Append(text, copied, start - copied);
            result.Append('$').Append(latex).Append('$');
            copied = end;
        }

        if (copied == 0)
        {
            return text;
        }

        result.Append(text, copied, text.Length - copied);
        return result.ToString();
    }

    // ───────────────────────── символы ─────────────────────────

    private static bool IsGreek(char c) => c is >= 'Α' and <= 'ω' and not '΢';

    private static bool IsFormulaLetter(char c) => char.IsAsciiLetter(c) || IsGreek(c);
}
