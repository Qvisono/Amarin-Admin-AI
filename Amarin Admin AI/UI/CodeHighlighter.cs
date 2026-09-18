using ColorCode;
using ColorCode.Common;
using ColorCode.Parsing;

namespace Amarin.UI;

/// <summary>Класс токена; в цвет он превращается уже в WPF-слое через ключ палитры.</summary>
internal enum CodeTokenKind
{
    Plain,
    Keyword,
    Control,
    String,
    Comment,
    Number,
    Type,
    Variable,
    Function,
    Operator
}

internal readonly record struct CodeSpan(string Text, CodeTokenKind Kind);

/// <summary>
/// Разбор кода на цветные куски поверх ColorCode. Слой намеренно чистый — ни одного типа
/// из System.Windows, поэтому тесты идут без STA, а <c>ColorCode.Styling.Style</c>
/// не конфликтует с <c>System.Windows.Style</c>.
/// </summary>
internal static class CodeHighlighter
{
    private const int CacheCapacity = 64;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyList<CodeSpan>> Cache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    /// <summary>Знаем ли мы язык из инфостроки ограждения (```powershell и т.п.).</summary>
    public static bool IsSupported(string? info) => Resolve(info) is not null;

    /// <summary>
    /// Куски всегда покрывают <paramref name="code"/> целиком и по порядку, так что
    /// склейка их Text обязана вернуть исходную строку.
    /// </summary>
    /// <param name="cache">
    /// Запоминать ли результат. <c>false</c> — для блока, который прямо сейчас дописывает
    /// модель: ключ включает весь текст блока, у растущего он меняется на каждой перерисовке,
    /// и попаданий у него не бывает в принципе. Зато его промежуточные состояния вытесняли
    /// из кэша подсветку уже дописанных блоков — тех, ради которых кэш и заведён.
    /// </param>
    public static IReadOnlyList<CodeSpan> Highlight(string code, string? info, bool cache = true)
    {
        code ??= "";
        var language = Resolve(info);
        if (language is null || code.Length == 0)
        {
            return [new CodeSpan(code, CodeTokenKind.Plain)];
        }

        // Разделитель — \0: он не встречается ни в идентификаторе языка, ни в коде,
        // поэтому ключи разных языков не могут склеиться в один.
        var key = language.Id + "\0" + code;
        if (cache)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(key, out var cached))
                {
                    return cached;
                }
            }
        }

        IReadOnlyList<CodeSpan> spans;
        try
        {
            spans = new SpanFormatter().Run(code, language);
        }
        catch
        {
            // Правила ColorCode построены на регексах; чужой синтаксис не повод ронять чат.
            spans = [new CodeSpan(code, CodeTokenKind.Plain)];
        }

        if (!cache)
        {
            return spans;
        }

        lock (Gate)
        {
            // Гонка двух потоков за один и тот же блок: побеждает уже лежащий в кэше,
            // чтобы вызывающие получали один экземпляр списка.
            if (Cache.TryGetValue(key, out var raced))
            {
                return raced;
            }

            Cache[key] = spans;
            CacheOrder.Enqueue(key);
            while (CacheOrder.Count > CacheCapacity)
            {
                Cache.Remove(CacheOrder.Dequeue());
            }
        }

        return spans;
    }

    /// <summary>
    /// ColorCode сам знает большинство привычных алиасов (cs, ps1, js, py, xaml), здесь только
    /// то, чего у него нет. Языков без правил (bash, yaml, docker) тут намеренно нет — они
    /// уедут в Plain и покажутся просто моноширинным текстом.
    /// </summary>
    private static ILanguage? Resolve(string? info)
    {
        if (string.IsNullOrWhiteSpace(info))
        {
            return null;
        }

        // ```powershell {highlight=1} — язык это только первое слово инфостроки.
        var id = info.Trim().Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        id = id switch
        {
            "ps" or "dotnet" => LanguageId.PowerShell,
            "jsonc" => LanguageId.Json,
            "node" => LanguageId.JavaScript,
            "python3" => LanguageId.Python,
            "cxx" or "cplusplus" => LanguageId.Cpp,
            "tsql" or "mysql" or "psql" or "postgres" or "sqlite" => LanguageId.Sql,
            "csproj" or "props" or "targets" or "svg" or "xsd" or "wxs" => LanguageId.Xml,
            _ => id
        };

        return Languages.FindById(id);
    }

    private static CodeTokenKind Map(string scope) => scope switch
    {
        ScopeName.Comment or ScopeName.XmlComment or ScopeName.HtmlComment or ScopeName.XmlDocComment
            or ScopeName.XmlDocTag
            => CodeTokenKind.Comment,

        ScopeName.String or ScopeName.StringCSharpVerbatim or ScopeName.StringEscape
            or ScopeName.JsonString or ScopeName.XmlAttributeValue or ScopeName.HtmlAttributeValue
            or ScopeName.XmlCDataSection
            => CodeTokenKind.String,

        ScopeName.Number or ScopeName.JsonNumber
            => CodeTokenKind.Number,

        ScopeName.ControlKeyword
            => CodeTokenKind.Control,

        ScopeName.Keyword or ScopeName.PreprocessorKeyword or ScopeName.PseudoKeyword
            or ScopeName.JsonConst or ScopeName.BuiltinValue or ScopeName.HtmlEntity
            => CodeTokenKind.Keyword,

        ScopeName.Type or ScopeName.TypeVariable or ScopeName.ClassName or ScopeName.NameSpace
            or ScopeName.Constructor or ScopeName.Predefined or ScopeName.PowerShellType
            or ScopeName.XmlName or ScopeName.HtmlElementName or ScopeName.CssSelector
            => CodeTokenKind.Type,

        ScopeName.PowerShellVariable or ScopeName.JsonKey or ScopeName.XmlAttribute
            or ScopeName.HtmlAttributeName or ScopeName.PowerShellParameter
            or ScopeName.CssPropertyName or ScopeName.Attribute or ScopeName.PowerShellAttribute
            => CodeTokenKind.Variable,

        ScopeName.PowerShellCommand or ScopeName.BuiltinFunction or ScopeName.SqlSystemFunction
            or ScopeName.Intrinsic
            => CodeTokenKind.Function,

        ScopeName.Operator or ScopeName.PowerShellOperator or ScopeName.HtmlOperator
            or ScopeName.Delimiter or ScopeName.XmlDelimiter or ScopeName.HtmlTagDelimiter
            or ScopeName.XmlAttributeQuotes or ScopeName.Brackets or ScopeName.SpecialCharacter
            => CodeTokenKind.Operator,

        _ => CodeTokenKind.Plain
    };

    /// <summary>
    /// ColorCode отдаёт исходник кусками через абстрактный Write: на каждый кусок список
    /// областей с индексами <em>внутри этого куска</em>, при этом области вкладываются
    /// друг в друга и не обязаны покрывать кусок целиком.
    /// </summary>
    private sealed class SpanFormatter : CodeColorizerBase
    {
        private readonly List<CodeSpan> _spans = [];

        // null-парсер значит «возьми парсер по умолчанию»; StyleDictionary мы не используем
        // вовсе — цвет определяется классом токена уже в WPF-слое.
        public SpanFormatter()
            : base(null, null)
        {
        }

        public IReadOnlyList<CodeSpan> Run(string code, ILanguage language)
        {
            languageParser.Parse(code, language, Write);
            var result = _spans.ToArray();

            // Парсер обязан вернуть весь исходник; если правила языка что-то проглотили,
            // честнее показать некрашеный код, чем обрезанный.
            var total = 0;
            foreach (var span in result)
            {
                total += span.Text.Length;
            }

            return total == code.Length ? result : [new CodeSpan(code, CodeTokenKind.Plain)];
        }

        protected override void Write(string parsedSourceCode, IList<Scope> scopes)
        {
            if (parsedSourceCode.Length == 0)
            {
                return;
            }

            if (scopes is not { Count: > 0 })
            {
                Add(parsedSourceCode, CodeTokenKind.Plain);
                return;
            }

            // Раскладываем по символам: сначала родитель, потом дети поверх него.
            // Куски мелкие, так что O(n) на кусок дешевле возни с пересечением интервалов.
            var kinds = new CodeTokenKind[parsedSourceCode.Length];
            foreach (var scope in scopes)
            {
                Paint(kinds, scope);
            }

            var start = 0;
            for (var i = 1; i <= kinds.Length; i++)
            {
                if (i < kinds.Length && kinds[i] == kinds[start])
                {
                    continue;
                }

                Add(parsedSourceCode[start..i], kinds[start]);
                start = i;
            }
        }

        private static void Paint(CodeTokenKind[] kinds, Scope scope)
        {
            var kind = Map(scope.Name);
            var end = Math.Min(scope.Index + scope.Length, kinds.Length);
            for (var i = Math.Max(0, scope.Index); i < end; i++)
            {
                kinds[i] = kind;
            }

            if (scope.Children is not { Count: > 0 })
            {
                return;
            }

            foreach (var child in scope.Children)
            {
                Paint(kinds, child);
            }
        }

        private void Add(string text, CodeTokenKind kind)
        {
            if (text.Length == 0)
            {
                return;
            }

            // Соседние куски одного класса склеиваем — меньше Run-ов в документе.
            if (_spans.Count > 0 && _spans[^1].Kind == kind)
            {
                _spans[^1] = _spans[^1] with { Text = _spans[^1].Text + text };
                return;
            }

            _spans.Add(new CodeSpan(text, kind));
        }
    }
}
