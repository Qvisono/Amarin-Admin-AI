using System.Globalization;
using System.Text;

namespace Amarin.Core;

/// <summary>Значение, которое даёт формула или ячейка.</summary>
internal readonly record struct XValue(XKind Kind, double Number = 0, string Text = "")
{
    public static XValue Empty { get; } = new(XKind.Empty);

    public static XValue Of(double number) =>
        double.IsFinite(number) ? new XValue(XKind.Number, number) : Error("#NUM!");

    public static XValue Of(string text) => new(XKind.Text, Text: text);

    public static XValue Of(bool value) => new(XKind.Bool, value ? 1 : 0);

    public static XValue Error(string code) => new(XKind.Error, Text: code);
}

internal enum XKind
{
    Empty,
    Number,
    Text,
    Bool,
    Error
}

/// <summary>Формулу посчитать нельзя: функция или ссылка, которых здесь нет, или круг ссылок.</summary>
internal sealed class FormulaUnsupportedException(string reason) : Exception(reason);

/// <summary>
/// Подсчёт формул Excel — тот, что нужен таблицам, которые собирает модель: арифметика, ссылки и
/// диапазоны (в том числе на другой лист), сравнения и распространённые функции.
/// </summary>
/// <remarks>
/// <para>
/// Без него книга, собранная формулами, до открытия в Excel не знала ни одного своего значения:
/// read_file показывал голые формулы, а сделать из такой книги PDF было не из чего — и модель
/// предлагала человеку открыть файл и сохранить PDF самому.
/// </para>
/// <para>
/// Порядок операций — как в Excel, а не как в школе: унарный минус связывает сильнее степени
/// (<c>-2^2</c> = 4), степень левоассоциативна (<c>2^3^2</c> = 64), <c>%</c> — постфикс. Чего здесь
/// нет (динамические массивы, поиск, даты), не угадывается, а помечается непосчитанным
/// (<see cref="FormulaUnsupportedException"/>): такую ячейку посчитает Excel при открытии.
/// </para>
/// </remarks>
internal sealed class ExcelCalc
{
    private readonly Func<string?, int, int, CellSource> _cells;
    private readonly Dictionary<(string Sheet, int Column, int Row), XValue> _done = [];
    private readonly HashSet<(string Sheet, int Column, int Row)> _busy = [];

    /// <param name="cells">Что лежит в ячейке листа (null — текущий лист вычисления).</param>
    public ExcelCalc(Func<string?, int, int, CellSource> cells) => _cells = cells;

    /// <summary>Что лежит в ячейке: значение, формула (без «=») или формула, которую не прочесть.</summary>
    internal readonly record struct CellSource(XValue Value, string? Formula = null, bool Opaque = false, string Sheet = "")
    {
        public static CellSource None(string sheet) => new(XValue.Empty, Sheet: sheet);
    }

    /// <summary>Значение ячейки; формула считается один раз. Null — посчитать нельзя.</summary>
    public XValue? TryValue(string sheet, int column, int row)
    {
        try
        {
            return Value(sheet, column, row);
        }
        catch (FormulaUnsupportedException)
        {
            return null;
        }
    }

    private XValue Value(string? sheet, int column, int row)
    {
        var source = _cells(sheet, column, row);
        var key = (source.Sheet.ToUpperInvariant(), column, row);
        if (source.Opaque)
        {
            throw new FormulaUnsupportedException("opaque formula");
        }

        if (source.Formula is not { } formula)
        {
            return source.Value;
        }

        if (_done.TryGetValue(key, out var known))
        {
            return known;
        }

        if (!_busy.Add(key))
        {
            throw new FormulaUnsupportedException("circular reference");
        }

        try
        {
            var parser = new Parser(formula);
            var tree = parser.Parse();
            var value = Evaluate(tree, new Context(source.Sheet, column, row));
            _done[key] = value;
            return value;
        }
        finally
        {
            _busy.Remove(key);
        }
    }

    // ───────────────────────── вычисление ─────────────────────────

    private readonly record struct Context(string Sheet, int Column, int Row);

    private XValue Evaluate(Node node, Context at) => node switch
    {
        Literal literal => literal.Value,
        Reference reference => Deref(reference, at),
        RangeNode range => range.IsSingle ? Deref(range.From, at) : throw new FormulaUnsupportedException("range used as a value"),
        Unary unary => Negate(unary, at),
        Percent percent => Number(Evaluate(percent.Operand, at), value => value / 100),
        Binary binary => Apply(binary, at),
        Call call => Function(call, at),
        _ => throw new FormulaUnsupportedException("unknown node")
    };

    private XValue Deref(Reference reference, Context at)
    {
        var value = Value(reference.Sheet ?? at.Sheet, reference.Column, reference.Row);
        return value.Kind == XKind.Empty ? XValue.Of(0) : value;
    }

    private XValue Negate(Unary unary, Context at)
    {
        var value = Evaluate(unary.Operand, at);
        return unary.Minus ? Number(value, number => -number) : value;
    }

    private static XValue Number(XValue value, Func<double, double> change) =>
        ToNumber(value, out var number) is { } error ? error : XValue.Of(change(number));

    private XValue Apply(Binary binary, Context at)
    {
        var left = Evaluate(binary.Left, at);
        var right = Evaluate(binary.Right, at);
        if (left.Kind == XKind.Error)
        {
            return left;
        }

        if (right.Kind == XKind.Error)
        {
            return right;
        }

        switch (binary.Op)
        {
            case "&":
                return XValue.Of(ToText(left) + ToText(right));
            case "=" or "<>" or "<" or ">" or "<=" or ">=":
                var order = Compare(left, right);
                return XValue.Of(binary.Op switch
                {
                    "=" => order == 0,
                    "<>" => order != 0,
                    "<" => order < 0,
                    ">" => order > 0,
                    "<=" => order <= 0,
                    _ => order >= 0
                });
        }

        if (ToNumber(left, out var a) is { } leftError)
        {
            return leftError;
        }

        if (ToNumber(right, out var b) is { } rightError)
        {
            return rightError;
        }

        return binary.Op switch
        {
            "+" => XValue.Of(a + b),
            "-" => XValue.Of(a - b),
            "*" => XValue.Of(a * b),
            "/" => b == 0 ? XValue.Error("#DIV/0!") : XValue.Of(a / b),
            "^" => XValue.Of(Math.Pow(a, b)),
            _ => throw new FormulaUnsupportedException("operator " + binary.Op)
        };
    }

    /// <summary>Сравнение по правилам Excel: числа между собой, текст без регистра, текст больше числа.</summary>
    private static int Compare(XValue left, XValue right)
    {
        static int Rank(XValue value) => value.Kind switch
        {
            XKind.Text => 1,
            XKind.Bool => 2,
            _ => 0
        };

        var byKind = Rank(left).CompareTo(Rank(right));
        if (byKind != 0)
        {
            return byKind;
        }

        return left.Kind == XKind.Text
            ? string.Compare(left.Text, right.Text, StringComparison.OrdinalIgnoreCase)
            : left.Number.CompareTo(right.Number);
    }

    /// <summary>Значение числом; ошибка — если оно не число и числом не читается.</summary>
    private static XValue? ToNumber(XValue value, out double number)
    {
        number = 0;
        switch (value.Kind)
        {
            case XKind.Number or XKind.Bool:
                number = value.Number;
                return null;
            case XKind.Empty:
                return null;
            case XKind.Text when double.TryParse(value.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return null;
            case XKind.Error:
                return value;
            default:
                return XValue.Error("#VALUE!");
        }
    }

    /// <summary>Значение текстом — как Excel показывает его в формате «Общий».</summary>
    internal static string ToText(XValue value) => value.Kind switch
    {
        XKind.Number => FormatNumber(value.Number),
        XKind.Bool => value.Number != 0 ? "TRUE" : "FALSE",
        XKind.Empty => "",
        _ => value.Text
    };

    /// <summary>Число как в ячейке формата «Общий»: до 15 значащих цифр, без лишних нулей.</summary>
    internal static string FormatNumber(double number)
    {
        var rounded = double.Parse(number.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return rounded.ToString("R", CultureInfo.InvariantCulture);
    }

    // ───────────────────────── функции ─────────────────────────

    private XValue Function(Call call, Context at)
    {
        switch (call.Name)
        {
            case "SUM":
                return Aggregate(call, at, numbers => numbers.Sum());
            case "AVERAGE":
                return Aggregate(call, at, numbers => numbers.Count == 0 ? double.NaN : numbers.Average(), empty: "#DIV/0!");
            case "MIN":
                return Aggregate(call, at, numbers => numbers.Count == 0 ? 0 : numbers.Min());
            case "MAX":
                return Aggregate(call, at, numbers => numbers.Count == 0 ? 0 : numbers.Max());
            case "PRODUCT":
                return Aggregate(call, at, numbers => numbers.Count == 0 ? 0 : numbers.Aggregate(1.0, (product, number) => product * number));
            case "COUNT":
                return XValue.Of(Values(call, at).Count(value => value.Kind == XKind.Number));
            case "COUNTA":
                return XValue.Of(Values(call, at).Count(value => value.Kind != XKind.Empty));
            case "ROUND" or "ROUNDUP" or "ROUNDDOWN":
                return Round(call, at);
            case "ABS":
                return Number(Arg(call, 0, at), Math.Abs);
            case "INT":
                return Number(Arg(call, 0, at), Math.Floor);
            case "SQRT":
                return Number(Arg(call, 0, at), number => number < 0 ? double.NaN : Math.Sqrt(number));
            case "POWER":
                return Two(call, at, Math.Pow);
            case "MOD":
                return Two(call, at, (number, divisor) => divisor == 0 ? double.NaN : number - (divisor * Math.Floor(number / divisor)), "#DIV/0!");
            case "PI":
                return XValue.Of(Math.PI);
            case "ROW":
                return call.Args.Count == 0 ? XValue.Of(at.Row) : XValue.Of(Target(call.Args[0]).Row);
            case "COLUMN":
                return call.Args.Count == 0 ? XValue.Of(at.Column) : XValue.Of(Target(call.Args[0]).Column);
            case "IF":
                return If(call, at);
            case "IFERROR":
                var tried = Arg(call, 0, at);
                return tried.Kind == XKind.Error ? Arg(call, 1, at) : tried;
            case "AND" or "OR":
                return Logical(call, at);
            case "NOT":
                return ToNumber(Arg(call, 0, at), out var not) is { } notError ? notError : XValue.Of(not == 0);
            case "TRUE":
                return XValue.Of(true);
            case "FALSE":
                return XValue.Of(false);
            case "CONCAT" or "CONCATENATE":
                return XValue.Of(string.Concat(Values(call, at).Select(ToText)));
            case "LEN":
                return XValue.Of(ToText(Arg(call, 0, at)).Length);
            case "UPPER":
                return XValue.Of(ToText(Arg(call, 0, at)).ToUpperInvariant());
            case "LOWER":
                return XValue.Of(ToText(Arg(call, 0, at)).ToLowerInvariant());
            case "TRIM":
                return XValue.Of(string.Join(' ', ToText(Arg(call, 0, at)).Split(' ', StringSplitOptions.RemoveEmptyEntries)));
            default:
                throw new FormulaUnsupportedException("function " + call.Name);
        }
    }

    private static Reference Target(Node node) => node switch
    {
        Reference reference => reference,
        RangeNode range => range.From,
        _ => throw new FormulaUnsupportedException("ROW/COLUMN of a value")
    };

    private XValue Arg(Call call, int index, Context at) =>
        index < call.Args.Count ? Evaluate(call.Args[index], at) : throw new FormulaUnsupportedException(call.Name + " argument missing");

    /// <summary>Значения аргументов, диапазоны — по ячейкам.</summary>
    private List<XValue> Values(Call call, Context at)
    {
        var values = new List<XValue>();
        foreach (var arg in call.Args)
        {
            if (arg is RangeNode { IsSingle: false } range)
            {
                for (var row = Math.Min(range.From.Row, range.To.Row); row <= Math.Max(range.From.Row, range.To.Row); row++)
                {
                    for (var column = Math.Min(range.From.Column, range.To.Column); column <= Math.Max(range.From.Column, range.To.Column); column++)
                    {
                        values.Add(Value(range.From.Sheet ?? at.Sheet, column, row));
                    }
                }
            }
            else
            {
                values.Add(Evaluate(arg, at));
            }
        }

        return values;
    }

    /// <summary>Сумма и прочие свёртки: в диапазоне текст и пустые не считаются, ошибка — передаётся.</summary>
    private XValue Aggregate(Call call, Context at, Func<List<double>, double> fold, string? empty = null)
    {
        var numbers = new List<double>();
        foreach (var value in Values(call, at))
        {
            switch (value.Kind)
            {
                case XKind.Error:
                    return value;
                case XKind.Number or XKind.Bool:
                    numbers.Add(value.Number);
                    break;
            }
        }

        if (numbers.Count == 0 && empty is not null)
        {
            return XValue.Error(empty);
        }

        return XValue.Of(fold(numbers));
    }

    private XValue Two(Call call, Context at, Func<double, double, double> apply, string? nanError = null)
    {
        if (ToNumber(Arg(call, 0, at), out var a) is { } first)
        {
            return first;
        }

        if (ToNumber(Arg(call, 1, at), out var b) is { } second)
        {
            return second;
        }

        var result = apply(a, b);
        return double.IsNaN(result) && nanError is not null ? XValue.Error(nanError) : XValue.Of(result);
    }

    private XValue Round(Call call, Context at)
    {
        if (ToNumber(Arg(call, 0, at), out var number) is { } error)
        {
            return error;
        }

        var digits = 0.0;
        if (call.Args.Count > 1 && ToNumber(Arg(call, 1, at), out digits) is { } digitsError)
        {
            return digitsError;
        }

        var scale = Math.Pow(10, Math.Truncate(digits));
        var scaled = number * scale;
        var rounded = call.Name switch
        {
            "ROUNDUP" => Math.Sign(scaled) * Math.Ceiling(Math.Abs(scaled) - 1e-9),
            "ROUNDDOWN" => Math.Sign(scaled) * Math.Floor(Math.Abs(scaled) + 1e-9),
            _ => Math.Round(scaled, MidpointRounding.AwayFromZero)
        };
        return XValue.Of(rounded / scale);
    }

    private XValue If(Call call, Context at)
    {
        var condition = Arg(call, 0, at);
        if (ToNumber(condition, out var test) is { } error)
        {
            return error;
        }

        return test != 0
            ? call.Args.Count > 1 ? Arg(call, 1, at) : XValue.Of(true)
            : call.Args.Count > 2 ? Arg(call, 2, at) : XValue.Of(false);
    }

    private XValue Logical(Call call, Context at)
    {
        var all = call.Name == "AND";
        foreach (var value in Values(call, at))
        {
            if (value.Kind == XKind.Error)
            {
                return value;
            }

            if (value.Kind is XKind.Empty or XKind.Text)
            {
                continue;
            }

            var truth = value.Number != 0;
            if (all && !truth)
            {
                return XValue.Of(false);
            }

            if (!all && truth)
            {
                return XValue.Of(true);
            }
        }

        return XValue.Of(all);
    }

    // ───────────────────────── разбор ─────────────────────────

    private abstract record Node;

    private sealed record Literal(XValue Value) : Node;

    /// <param name="Sheet">Лист ссылки; null — тот же, что у формулы.</param>
    private sealed record Reference(string? Sheet, int Column, int Row) : Node;

    private sealed record RangeNode(Reference From, Reference To) : Node
    {
        public bool IsSingle => From.Column == To.Column && From.Row == To.Row;
    }

    private sealed record Unary(bool Minus, Node Operand) : Node;

    private sealed record Percent(Node Operand) : Node;

    private sealed record Binary(string Op, Node Left, Node Right) : Node;

    private sealed record Call(string Name, IReadOnlyList<Node> Args) : Node;

    /// <summary>Разбор формулы рекурсивным спуском, уровни — в порядке операций Excel.</summary>
    private sealed class Parser(string text)
    {
        private int _at;

        public Node Parse()
        {
            var node = Comparison();
            Skip();
            return _at == text.Length ? node : throw new FormulaUnsupportedException($"unexpected '{text[_at]}'");
        }

        private Node Comparison()
        {
            var left = Concat();
            while (TryOp(out var op, "<=", ">=", "<>", "=", "<", ">"))
            {
                left = new Binary(op, left, Concat());
            }

            return left;
        }

        private Node Concat()
        {
            var left = Additive();
            while (TryOp(out _, "&"))
            {
                left = new Binary("&", left, Additive());
            }

            return left;
        }

        private Node Additive()
        {
            var left = Multiplicative();
            while (TryOp(out var op, "+", "-"))
            {
                left = new Binary(op, left, Multiplicative());
            }

            return left;
        }

        private Node Multiplicative()
        {
            var left = Power();
            while (TryOp(out var op, "*", "/"))
            {
                left = new Binary(op, left, Power());
            }

            return left;
        }

        private Node Power()
        {
            var left = Postfix();
            while (TryOp(out _, "^"))
            {
                left = new Binary("^", left, Postfix());
            }

            return left;
        }

        private Node Postfix()
        {
            var node = Prefix();
            while (TryOp(out _, "%"))
            {
                node = new Percent(node);
            }

            return node;
        }

        private Node Prefix()
        {
            if (TryOp(out var op, "-", "+"))
            {
                return new Unary(op == "-", Prefix());
            }

            return Primary();
        }

        private Node Primary()
        {
            Skip();
            if (_at >= text.Length)
            {
                throw new FormulaUnsupportedException("formula ends early");
            }

            var c = text[_at];
            if (c == '(')
            {
                _at++;
                var inner = Comparison();
                Expect(')');
                return inner;
            }

            if (c == '"')
            {
                return new Literal(XValue.Of(QuotedString()));
            }

            if (char.IsDigit(c) || (c == '.' && _at + 1 < text.Length && char.IsDigit(text[_at + 1])))
            {
                return new Literal(XValue.Of(NumberLiteral()));
            }

            if (c == '#')
            {
                throw new FormulaUnsupportedException("error literal");
            }

            // Имя листа в кавычках: 'Лист 1'!A1.
            if (c == '\'')
            {
                var sheet = QuotedSheet();
                Expect('!');
                return ReferenceOrRange(sheet);
            }

            var word = Word();
            if (word.Length == 0)
            {
                throw new FormulaUnsupportedException($"unexpected '{c}'");
            }

            Skip();
            if (_at < text.Length && text[_at] == '!')
            {
                _at++;
                return ReferenceOrRange(word);
            }

            if (_at < text.Length && text[_at] == '(')
            {
                _at++;
                return new Call(FunctionName(word), Arguments());
            }

            if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase))
            {
                return new Literal(XValue.Of(true));
            }

            if (word.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                return new Literal(XValue.Of(false));
            }

            return RangeTail(ParseCell(word, null));
        }

        /// <summary>Имя функции без приставок новых версий Excel (<c>_xlfn.</c>).</summary>
        private static string FunctionName(string word)
        {
            var name = word.ToUpperInvariant();
            return name.StartsWith("_XLFN.", StringComparison.Ordinal) ? name[6..] : name;
        }

        private List<Node> Arguments()
        {
            var args = new List<Node>();
            Skip();
            if (_at < text.Length && text[_at] == ')')
            {
                _at++;
                return args;
            }

            while (true)
            {
                args.Add(Comparison());
                Skip();
                if (_at < text.Length && (text[_at] == ',' || text[_at] == ';'))
                {
                    _at++;
                    continue;
                }

                Expect(')');
                return args;
            }
        }

        private Node ReferenceOrRange(string sheet)
        {
            var word = Word();
            return RangeTail(ParseCell(word, sheet));
        }

        private Node RangeTail(Reference from)
        {
            Skip();
            if (_at < text.Length && text[_at] == ':')
            {
                _at++;
                Skip();
                var to = ParseCell(Word(), from.Sheet);
                return new RangeNode(from, to);
            }

            return from;
        }

        private static Reference ParseCell(string word, string? sheet)
        {
            var plain = word.Replace("$", "", StringComparison.Ordinal);
            return CellAddress.TryParse(plain, out var column, out var row)
                ? new Reference(sheet, column, row)
                : throw new FormulaUnsupportedException("name " + word);
        }

        private string Word()
        {
            Skip();
            var start = _at;
            while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] is '_' or '.' or '$'))
            {
                _at++;
            }

            return text[start.._at];
        }

        private double NumberLiteral()
        {
            var start = _at;
            while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.'))
            {
                _at++;
            }

            if (_at < text.Length && text[_at] is 'e' or 'E')
            {
                _at++;
                if (_at < text.Length && text[_at] is '+' or '-')
                {
                    _at++;
                }

                while (_at < text.Length && char.IsDigit(text[_at]))
                {
                    _at++;
                }
            }

            return double.TryParse(text[start.._at], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                ? number
                : throw new FormulaUnsupportedException("number " + text[start.._at]);
        }

        private string QuotedString()
        {
            var value = new StringBuilder();
            _at++;
            while (_at < text.Length)
            {
                if (text[_at] == '"')
                {
                    if (_at + 1 < text.Length && text[_at + 1] == '"')
                    {
                        value.Append('"');
                        _at += 2;
                        continue;
                    }

                    _at++;
                    return value.ToString();
                }

                value.Append(text[_at++]);
            }

            throw new FormulaUnsupportedException("unterminated string");
        }

        private string QuotedSheet()
        {
            var value = new StringBuilder();
            _at++;
            while (_at < text.Length)
            {
                if (text[_at] == '\'')
                {
                    if (_at + 1 < text.Length && text[_at + 1] == '\'')
                    {
                        value.Append('\'');
                        _at += 2;
                        continue;
                    }

                    _at++;
                    return value.ToString();
                }

                value.Append(text[_at++]);
            }

            throw new FormulaUnsupportedException("unterminated sheet name");
        }

        private bool TryOp(out string op, params string[] options)
        {
            Skip();
            foreach (var option in options)
            {
                if (string.CompareOrdinal(text, _at, option, 0, option.Length) == 0)
                {
                    op = option;
                    _at += option.Length;
                    return true;
                }
            }

            op = "";
            return false;
        }

        private void Expect(char expected)
        {
            Skip();
            if (_at >= text.Length || text[_at] != expected)
            {
                throw new FormulaUnsupportedException($"expected '{expected}'");
            }

            _at++;
        }

        private void Skip()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }
    }
}
