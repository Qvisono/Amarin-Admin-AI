using System.Text;

namespace Amarin.Core;

/// <summary>
/// Разбор подмножества LaTeX, на котором пишут формулы модели: дроби, корни, индексы,
/// растянутые скобки, матрицы, <c>cases</c> и выключные выкладки в несколько строк.
/// </summary>
/// <remarks>
/// Разбор ничего не бракует. Незнакомая команда становится обычным словом, незакрытая скобка
/// закрывается на конце строки: пусть формула выглядит небрежно, но текст ответа не должен
/// пропадать из-за одной опечатки в разметке.
/// </remarks>
public static class LatexParser
{
    /// <summary>Потолок на число узлов: страховка от разросшейся или зацикленной разметки.</summary>
    private const int NodeBudget = 4000;

    public static MathNode Parse(string? latex) => new Parser(latex ?? "").ParseDocument();

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _index;
        private int _budget = NodeBudget;

        private bool Eof => _index >= _text.Length;

        private char Peek => _text[_index];

        public MathNode ParseDocument()
        {
            var rows = new List<MathNode>();
            while (true)
            {
                rows.Add(Row(ParseItems(stopAtCell: false)));
                if (!TryConsumeRowBreak())
                {
                    break;
                }
            }

            // Одна строка — не список строк: лишняя обёртка мешала бы разметке считать высоту.
            return rows.Count == 1 ? rows[0] : new MathLines(rows);
        }

        // ───────────────────────── последовательности ─────────────────────────

        private List<MathNode> ParseItems(bool stopAtCell)
        {
            var items = new List<MathNode>();

            while (!Eof && _budget > 0)
            {
                SkipSpace();
                if (Eof)
                {
                    break;
                }

                var c = Peek;
                if (c == '}' || (stopAtCell && c == '&'))
                {
                    break;
                }

                if (c == '\\' && PeekCommand() is { } upcoming)
                {
                    if (upcoming is "\\" or "right" or "end" or "cr")
                    {
                        break;
                    }

                    // {a \over b}: числителем становится всё, что уже разобрано.
                    if (upcoming is "over" or "atop")
                    {
                        ReadCommand();
                        var denominator = Row(ParseItems(stopAtCell));
                        return [new MathFraction(Row(items), denominator, upcoming == "over")];
                    }
                }

                var before = _index;
                var atom = ParseAtom();
                if (atom is null)
                {
                    // Атом ничего не съел — уходим, иначе цикл не кончится никогда.
                    if (_index == before)
                    {
                        _index++;
                    }

                    continue;
                }

                _budget--;
                items.Add(ParseScripts(atom));
            }

            return items;
        }

        private static MathNode Row(List<MathNode> items) =>
            items.Count == 1 ? items[0] : new MathRow(items);

        /// <summary>Индексы, степени и штрихи, навешанные на только что разобранный атом.</summary>
        private MathNode ParseScripts(MathNode atom)
        {
            MathNode? sub = null;
            MathNode? sup = null;
            var limits = TakesLimits(atom);

            while (!Eof)
            {
                SkipSpace();
                if (Eof)
                {
                    break;
                }

                if (Peek == '\'')
                {
                    var primes = new StringBuilder();
                    while (!Eof && Peek == '\'')
                    {
                        primes.Append('′');
                        _index++;
                    }

                    sup = sup is null
                        ? new MathSymbol(primes.ToString(), MathTokenKind.Upright)
                        : new MathRow([sup, new MathSymbol(primes.ToString(), MathTokenKind.Upright)]);
                    continue;
                }

                if (Peek == '\\' && PeekCommand() is "limits" or "nolimits")
                {
                    limits = ReadCommand() == "limits";
                    continue;
                }

                if (Peek is not ('^' or '_'))
                {
                    break;
                }

                var isSuper = Peek == '^';
                _index++;
                var script = ParseScriptAtom();
                if (isSuper)
                {
                    sup = script;
                }
                else
                {
                    sub = script;
                }
            }

            return sub is null && sup is null ? atom : new MathScripts(atom, sub, sup, limits);
        }

        private static bool TakesLimits(MathNode atom) => atom switch
        {
            MathSymbol symbol => symbol.Kind == MathTokenKind.BigOperator ||
                                 MathSymbols.TryGetLimitFunction(symbol.Text),
            _ => false
        };

        /// <summary>После <c>^</c> идёт либо группа, либо ровно один символ: <c>x^24</c> — это x²4.</summary>
        private MathNode ParseScriptAtom()
        {
            SkipSpace();
            if (Eof)
            {
                return MathRow.Empty;
            }

            if (Peek == '{')
            {
                return ParseGroup();
            }

            var before = _index;
            var atom = ParseAtom(singleCharacter: true);
            if (atom is null && _index == before)
            {
                _index++;
            }

            return atom ?? MathRow.Empty;
        }

        private MathNode ParseGroup()
        {
            _index++; // '{'
            var items = ParseItems(stopAtCell: false);
            if (!Eof && Peek == '}')
            {
                _index++;
            }

            return Row(items);
        }

        /// <summary>Обязательный аргумент команды: группа <c>{...}</c> или один следующий символ.</summary>
        private MathNode ParseArgument()
        {
            SkipSpace();
            if (Eof)
            {
                return MathRow.Empty;
            }

            return Peek == '{' ? ParseGroup() : ParseScriptAtom();
        }

        // ───────────────────────── атомы ─────────────────────────

        private MathNode? ParseAtom(bool singleCharacter = false)
        {
            SkipSpace();
            if (Eof)
            {
                return null;
            }

            var c = Peek;

            if (c == '{')
            {
                return ParseGroup();
            }

            if (c == '\\')
            {
                return ParseCommand();
            }

            if (char.IsDigit(c))
            {
                if (singleCharacter)
                {
                    _index++;
                    return new MathSymbol(c.ToString(), MathTokenKind.Number);
                }

                var start = _index;
                while (!Eof && (char.IsDigit(Peek) ||
                                (Peek == '.' && _index + 1 < _text.Length && char.IsDigit(_text[_index + 1]))))
                {
                    _index++;
                }

                return new MathSymbol(_text[start.._index], MathTokenKind.Number);
            }

            _index++;
            return new MathSymbol(CharacterText(c), CharacterKind(c));
        }

        private static string CharacterText(char c) => c switch
        {
            '-' => "−",
            '*' => "∗",
            '\'' => "′",
            _ => c.ToString()
        };

        private static MathTokenKind CharacterKind(char c) => c switch
        {
            '+' or '-' or '*' or '/' => MathTokenKind.Binary,
            '=' or '<' or '>' => MathTokenKind.Relation,
            ',' or ';' or ':' or '.' => MathTokenKind.Punctuation,
            '(' or ')' or '[' or ']' or '|' => MathTokenKind.Fence,
            _ when char.IsLetter(c) => MathTokenKind.Variable,
            _ => MathTokenKind.Upright
        };

        private MathNode? ParseCommand()
        {
            var name = ReadCommand();
            if (name.Length == 0)
            {
                return null;
            }

            switch (name)
            {
                case "frac" or "dfrac" or "tfrac" or "cfrac":
                    return new MathFraction(ParseArgument(), ParseArgument());

                case "binom" or "dbinom" or "tbinom" or "choose":
                    return new MathFenced("(", ")", new MathFraction(ParseArgument(), ParseArgument(), Line: false));

                case "sqrt":
                    return ParseRadical();

                case "text" or "textrm" or "textnormal" or "textsf" or "textit" or
                     "mathrm" or "mathsf" or "mathtt" or "operatorname" or "mbox":
                    return new MathSymbol(ReadRawGroup(), MathTokenKind.Upright);

                case "mathbf" or "boldsymbol" or "bf" or "pmb":
                    return new MathStyled(ParseArgument(), MathStyleKind.Bold);

                case "mathbb":
                    return new MathStyled(ParseArgument(), MathStyleKind.Blackboard);

                case "mathit" or "it":
                    return new MathStyled(ParseArgument(), MathStyleKind.Italic);

                case "mathcal" or "mathscr" or "mathfrak":
                    return new MathStyled(ParseArgument(), MathStyleKind.Calligraphic);

                case "hat" or "widehat":
                    return new MathAccent(ParseArgument(), MathAccentKind.Hat);

                case "bar":
                    return new MathAccent(ParseArgument(), MathAccentKind.Bar);

                case "overline":
                    return new MathAccent(ParseArgument(), MathAccentKind.Overline);

                case "underline" or "underbrace":
                    return new MathAccent(ParseArgument(), MathAccentKind.Underline);

                case "vec" or "overrightarrow":
                    return new MathAccent(ParseArgument(), MathAccentKind.Vector);

                case "tilde" or "widetilde":
                    return new MathAccent(ParseArgument(), MathAccentKind.Tilde);

                case "dot":
                    return new MathAccent(ParseArgument(), MathAccentKind.Dot);

                case "ddot":
                    return new MathAccent(ParseArgument(), MathAccentKind.DoubleDot);

                case "left":
                    return ParseFenced();

                case "begin":
                    return ParseEnvironment();

                // Ручной размер скобок разметке не нужен — она и так тянет их по содержимому.
                case "big" or "Big" or "bigg" or "Bigg" or
                     "bigl" or "Bigl" or "biggl" or "Biggl" or
                     "bigr" or "Bigr" or "biggr" or "Biggr" or
                     "bigm" or "Bigm" or "biggm" or "Biggm":
                    return ParseAtom();

                case "displaystyle" or "textstyle" or "scriptstyle" or "scriptscriptstyle" or
                     "limits" or "nolimits" or "notag" or "nonumber":
                    return null;

                case "," or "thinspace":
                    return new MathSpace(0.17);

                case ":" or ">" or "medspace":
                    return new MathSpace(0.22);

                case ";" or "thickspace":
                    return new MathSpace(0.28);

                case " " or "enspace":
                    return new MathSpace(0.5);

                case "!" or "negthinspace":
                    return new MathSpace(-0.17);

                case "quad":
                    return new MathSpace(1.0);

                case "qquad":
                    return new MathSpace(2.0);

                case "%" or "$" or "&" or "_" or "#" or "{" or "}":
                    return new MathSymbol(name, MathTokenKind.Upright);

                case "lVert" or "rVert":
                    return new MathSymbol("‖", MathTokenKind.Fence);

                case "lvert" or "rvert":
                    return new MathSymbol("|", MathTokenKind.Fence);
            }

            if (MathSymbols.TryGet(name, out var text, out var kind))
            {
                return new MathSymbol(text, kind);
            }

            // Неизвестная команда: показываем имя прямым шрифтом, чтобы автор увидел опечатку.
            return new MathSymbol(name, MathTokenKind.Upright);
        }

        private MathNode ParseRadical()
        {
            SkipSpace();
            MathNode? index = null;
            if (!Eof && Peek == '[')
            {
                _index++;
                var items = new List<MathNode>();
                while (!Eof && Peek != ']' && _budget > 0)
                {
                    var before = _index;
                    var atom = ParseAtom();
                    if (atom is null)
                    {
                        if (_index == before)
                        {
                            _index++;
                        }

                        continue;
                    }

                    _budget--;
                    items.Add(atom);
                }

                if (!Eof && Peek == ']')
                {
                    _index++;
                }

                index = Row(items);
            }

            return new MathRadical(ParseArgument(), index);
        }

        private MathNode ParseFenced()
        {
            var left = ReadDelimiter();
            var body = Row(ParseItems(stopAtCell: false));

            var right = "";
            if (!Eof && Peek == '\\' && PeekCommand() == "right")
            {
                ReadCommand();
                right = ReadDelimiter();
            }

            return new MathFenced(left, right, body);
        }

        private MathNode ParseEnvironment()
        {
            var environment = ReadRawGroup().Trim().TrimEnd('*');
            if (environment is "array" or "tabular")
            {
                SkipSpace();
                if (!Eof && Peek == '{')
                {
                    ReadRawGroup();
                }
            }

            var rows = new List<IReadOnlyList<MathNode>>();
            var cells = new List<MathNode>();

            while (!Eof && _budget > 0)
            {
                cells.Add(Row(ParseItems(stopAtCell: true)));

                SkipSpace();
                if (Eof)
                {
                    break;
                }

                if (Peek == '&')
                {
                    _index++;
                    continue;
                }

                if (TryConsumeRowBreak())
                {
                    rows.Add(cells);
                    cells = [];
                    continue;
                }

                if (Peek == '\\' && PeekCommand() == "end")
                {
                    ReadCommand();
                    ReadRawGroup();
                    break;
                }

                // Ни разделителя, ни конца — дальше разбирать нечего.
                if (Peek == '}')
                {
                    break;
                }

                _index++;
            }

            if (cells.Count > 0 && (cells.Count > 1 || cells[0] is not MathRow { Items.Count: 0 }))
            {
                rows.Add(cells);
            }

            var (left, right) = environment switch
            {
                "pmatrix" => ("(", ")"),
                "bmatrix" => ("[", "]"),
                "Bmatrix" => ("{", "}"),
                "vmatrix" => ("|", "|"),
                "Vmatrix" => ("‖", "‖"),
                "cases" => ("{", ""),
                _ => ("", "")
            };

            return new MathMatrix(rows, left, right, LeftAligned: environment is "cases" or "aligned" or "align" or "split");
        }

        // ───────────────────────── лексика ─────────────────────────

        private void SkipSpace()
        {
            while (!Eof && (Peek == ' ' || Peek == '\t' || Peek == '\n' || Peek == '\r' || Peek == '~'))
            {
                _index++;
            }
        }

        /// <summary>Имя следующей команды без её поглощения; <c>null</c>, если впереди не команда.</summary>
        private string? PeekCommand()
        {
            var saved = _index;
            var name = ReadCommand();
            _index = saved;
            return name.Length == 0 ? null : name;
        }

        /// <summary>
        /// Имя команды. У <c>\alpha</c> это буквы, у <c>\,</c> и <c>\\</c> — один следующий символ.
        /// </summary>
        private string ReadCommand()
        {
            if (Eof || Peek != '\\')
            {
                return "";
            }

            _index++;
            if (Eof)
            {
                return "";
            }

            if (!char.IsLetter(Peek))
            {
                var single = Peek.ToString();
                _index++;
                return single;
            }

            var start = _index;
            while (!Eof && char.IsLetter(Peek))
            {
                _index++;
            }

            return _text[start.._index];
        }

        private bool TryConsumeRowBreak()
        {
            SkipSpace();
            if (Eof || Peek != '\\' || PeekCommand() is not ("\\" or "cr"))
            {
                return false;
            }

            ReadCommand();

            // \\[6pt] — необязательный вертикальный отступ, для нас ничего не значащий.
            SkipSpace();
            if (!Eof && Peek == '[')
            {
                while (!Eof && Peek != ']')
                {
                    _index++;
                }

                if (!Eof)
                {
                    _index++;
                }
            }

            return true;
        }

        /// <summary>Содержимое <c>{...}</c> как есть — для <c>\text</c> и имён окружений.</summary>
        private string ReadRawGroup()
        {
            SkipSpace();
            if (Eof || Peek != '{')
            {
                // Без скобок аргументом считается один символ: \text x.
                if (Eof)
                {
                    return "";
                }

                var single = Peek.ToString();
                _index++;
                return single;
            }

            _index++;
            var depth = 1;
            var builder = new StringBuilder();
            while (!Eof)
            {
                var c = Peek;
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        _index++;
                        break;
                    }
                }
                else if (c == '\\' && _index + 1 < _text.Length && !char.IsLetter(_text[_index + 1]))
                {
                    // \{ и \% внутри текста — это сами скобка и процент.
                    builder.Append(_text[_index + 1]);
                    _index += 2;
                    continue;
                }

                builder.Append(c);
                _index++;
            }

            return builder.ToString();
        }

        private string ReadDelimiter()
        {
            SkipSpace();
            if (Eof)
            {
                return "";
            }

            if (Peek == '\\')
            {
                var name = ReadCommand();
                if (name == ".")
                {
                    return "";
                }

                return MathSymbols.TryGet(name, out var text, out _) ? text : name;
            }

            var c = Peek;
            _index++;
            return c switch
            {
                '.' => "",
                '<' => "⟨",
                '>' => "⟩",
                _ => c.ToString()
            };
        }
    }
}
