using System.Text.RegularExpressions;
using Amarin.UI;
using ColorCode;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Подсветка кода не виснет ни на одном языке ColorCode.
/// </summary>
/// <remarks>
/// Программа висела намертво посреди ответа с JSON: правило строки JSON перебирало варианты без
/// конца на строке, которую модель ещё не дописала (см. <see cref="CodeHighlighterFixes"/>).
/// Правила проверяются отдельными <see cref="Regex"/> с пределом времени: правило с перебором
/// роняет тест исключением, а не вешает весь прогон.
/// </remarks>
public sealed class CodeHighlighterSafetyTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    // Обёртка та же, в какую ColorCode ставит каждое правило при сборке языка.
    private static Regex Rule(string pattern) => new("(?-xis)(?m)(" + pattern + ")", RegexOptions.None, Budget);

    /// <summary>
    /// Незакрытые строки, комментарии и блоки — то, что подсветка видит, пока ответ пишется, — и
    /// готовые конструкции, за которыми правило ищет продолжение и не находит.
    /// </summary>
    private static string[] Hostile()
    {
        const int count = 48;
        var word = new string('x', count);
        static string Lines(string line) => string.Concat(Enumerable.Repeat(line, count));

        return
        [
            "\"" + word,
            "'" + word,
            "`" + word,
            "{\"a\": \"" + word,
            "[\"a\", \"" + word + "\"]",
            "{\"a\": [\"b\", \"" + word + "\"]}",
            "\"a\\\"" + word,
            "/*" + Lines("a\n"),
            "/**" + Lines(" * a\n"),
            "(*" + Lines("a\n"),
            "<!--" + Lines("a\n"),
            "{-" + Lines("a\n"),
            "```md\n" + Lines("a \n"),
            "```\n" + Lines(" \t\n"),
            "\"\"\"" + Lines("\"\" a\n"),
            "@\"" + Lines("a\n"),
            "@'" + Lines("a\n"),
            "<%@ " + Lines("a "),
            "<!DOCTYPE " + Lines("a "),
            "<a " + Lines("b=\"c\" "),
            "<" + Lines("a "),
            "[" + word,
            "$(" + Lines("a "),
            "#" + Lines(" "),
            Lines(" \t"),
            Lines("\n"),
            Lines("    a\n")
        ];
    }

    [Fact]
    public void No_rule_of_any_language_backtracks_without_end()
    {
        // Замены ставит статический конструктор: первое обращение к подсветке.
        Assert.True(CodeHighlighter.IsSupported("json"));

        var hostile = Hostile();
        var hanging = new List<string>();
        foreach (var language in Languages.All)
        {
            for (var r = 0; r < language.Rules.Count; r++)
            {
                var regex = Rule(language.Rules[r].Regex);
                for (var i = 0; i < hostile.Length; i++)
                {
                    try
                    {
                        _ = regex.Matches(hostile[i]).Count;
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        hanging.Add($"{language.Id} rule {r} on input {i}: {language.Rules[r].Regex}");
                    }
                }
            }
        }

        Assert.True(hanging.Count == 0, string.Join("\n\n", hanging));
    }

    [Fact]
    public void A_replaced_rule_reads_ordinary_code_exactly_as_before()
    {
        Assert.True(CodeHighlighter.IsSupported("json"));
        string[] samples =
        [
            "{\"name\": \"value with \\\"escape\\\"\", \"list\": [\"one\", \"two words\"], \"n\": 1.5, \"ok\": true}",
            "{\n  \"key\": \"line\",\n  \"nested\": {\"a\": [1, 2, \"three\"]}\n}",
            "int a = 1; /* one */ int b = 2; /** many\n * lines\n */ var c = \"s\";",
            "/* star ** inside */ x /***/ y /* a\r\n b */",
            "(* one *) let x = 1 (** two\n lines **)",
            "<root><!-- one --><a b=\"c\"/><!--\n two - lines\n--></root>",
            "{- one -} main = {- two\n lines -} pure ()",
            "Text\n\n```cs\nvar a = 1;\n  indented\n```\n\nMore\n```\ncode\n```",
            "<a href=\"x\" title='y' disabled>text</a>\n<b c=\"d e\"/><p:x q=\"1\" r='2'>",
            "<?xml version=\"1.0\"?>\n<root a=\"b\" c='d'><x y=\"z\"/></root>",
            "let s = \"\"\"triple \"quoted\" text\"\"\" in s",
            "val s = \"text \\n more\"\nval c = 'c'\nval v = @\"verbatim \"\"x\"\" end\""
        ];

        Assert.NotEmpty(CodeHighlighterFixes.Changed);
        foreach (var (language, original, fixedRule) in CodeHighlighterFixes.Changed)
        {
            var before = Rule(original);
            var after = Rule(fixedRule);
            foreach (var sample in samples)
            {
                Assert.True(
                    Describe(before.Matches(sample)) == Describe(after.Matches(sample)),
                    $"{language}: {fixedRule} reads differently from {original} on {sample}");
            }
        }

        static string Describe(MatchCollection matches) => string.Join(
            "|",
            matches.Select(match => string.Join(
                ",",
                match.Groups.Cast<Group>().Select(group => group.Success ? $"{group.Index}+{group.Length}" : "-"))));
    }

    [Fact]
    public void Every_replacement_still_finds_its_rule()
    {
        // Обновлённый ColorCode мог переписать правило: тогда замена молча перестала бы работать.
        Assert.True(CodeHighlighter.IsSupported("json"));
        foreach (var (unsafePart, safePart) in CodeHighlighterFixes.Replacements)
        {
            Assert.Contains(
                CodeHighlighterFixes.Changed,
                change => change.Original.Contains(unsafePart, StringComparison.Ordinal) &&
                          change.Fixed.Contains(safePart, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_old_json_string_rule_does_backtrack_and_the_test_would_see_it()
    {
        // Проверка проверки: без замены первый тест упал бы, а не прошёл молча.
        var (unsafePart, _) = CodeHighlighterFixes.Replacements[0];
        Assert.Throws<RegexMatchTimeoutException>(() =>
            new Regex(unsafePart, RegexOptions.None, TimeSpan.FromMilliseconds(200))
                .Matches("{\"a\": \"" + new string('x', 48)).Count);
    }

    [Theory]
    [InlineData("json", "{\n  \"name\": \"ai_refuse\",\n  \"description\": \"")]
    [InlineData("json", "[\"a\", \"")]
    [InlineData("csharp", "/** ")]
    [InlineData("xml", "<!-- ")]
    public async Task A_block_the_model_is_still_writing_is_highlighted_at_once(string language, string start)
    {
        var code = start + string.Concat(Enumerable.Repeat("abcdef ghijkl\n", 40));
        var highlight = Task.Run(() => CodeHighlighter.Highlight(code, language, cache: false));

        Assert.Same(highlight, await Task.WhenAny(highlight, Task.Delay(Budget)));
        Assert.Equal(code, string.Concat((await highlight).Select(span => span.Text)));
    }
}
