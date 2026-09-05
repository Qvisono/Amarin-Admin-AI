using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Разбор markdown и подсветка кода — чистые функции, поэтому STA-поток им не нужен.
/// Всё, что требует FlowDocument, живёт в <see cref="ChatMarkdownRenderTests"/>.
/// </summary>
public sealed class ChatMarkdownTests
{
    [Theory]
    [InlineData("обычный вопрос", false)]
    [InlineData("вопрос с **жирным** и `кодом`", false)]
    [InlineData("первый абзац\n\nвторой абзац", false)]
    [InlineData("- один\n- два", true)]
    [InlineData("# Заголовок", true)]
    [InlineData("> цитата", true)]
    [InlineData("```\ncode\n```", true)]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", true)]
    [InlineData("---", true)]
    [InlineData("", false)]
    public void Block_constructs_are_detected(string text, bool expected)
    {
        Assert.Equal(expected, ChatMarkdown.HasBlockConstructs(text));
    }

    [Fact]
    public void Flatten_drops_inline_markers_for_bubble_measurement()
    {
        Assert.Equal("жирный и код", ChatMarkdown.FlattenInline("**жирный** и `код`"));
        Assert.Equal("ссылка", ChatMarkdown.FlattenInline("[ссылка](https://example.com)"));
        Assert.Equal("зачёркнуто", ChatMarkdown.FlattenInline("~~зачёркнуто~~"));
    }

    [Fact]
    public void Flatten_returns_the_source_when_there_is_block_markup()
    {
        // Такое сообщение всё равно поедет в широкий пузырь — мерить его смысла нет.
        const string source = "- один\n- два";
        Assert.Equal(source, ChatMarkdown.FlattenInline(source));
    }

    [Fact]
    public void Preview_lines_still_use_paragraphs_and_bullets()
    {
        // Расширения pipeline не должны ломать превью в списке чатов.
        Assert.Equal(["Привет", "• один", "• два"], ChatMarkdown.PreviewLines("Привет\n\n- один\n- два"));
    }

    [Fact]
    public void Dollar_signs_stay_literal()
    {
        // Ровно поэтому pipeline собран вручную: UseAdvancedExtensions() включает Mathematics,
        // и переменные PowerShell превратились бы в формулу.
        Assert.Equal(
            "$env:Path и $svc.Status",
            ChatMarkdown.FlattenInline("$env:Path и $svc.Status"));
    }

    [Theory]
    [InlineData("powershell")]
    [InlineData("ps1")]
    [InlineData("csharp")]
    [InlineData("cs")]
    [InlineData("json")]
    [InlineData("xml")]
    [InlineData("python")]
    [InlineData("sql")]
    [InlineData("js")]
    public void Known_languages_are_supported(string info)
    {
        Assert.True(CodeHighlighter.IsSupported(info), info);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("shell")]
    [InlineData("yaml")]
    [InlineData("dockerfile")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_languages_fall_back_to_one_plain_span(string? info)
    {
        Assert.False(CodeHighlighter.IsSupported(info));

        const string code = "apt-get install -y curl";
        var spans = CodeHighlighter.Highlight(code, info);
        Assert.Equal([new CodeSpan(code, CodeTokenKind.Plain)], spans);
    }

    [Theory]
    [InlineData("powershell", "# сводка\n$svc = Get-Service -Name 'Spooler'\nif ($svc.Status -eq 'Running') { 42 }")]
    [InlineData("csharp", "// hi\npublic sealed class Foo { int N = 42; string S = \"x\"; }")]
    [InlineData("json", "{ \"a\": 1, \"b\": [true, \"s\"] }")]
    [InlineData("xml", "<Root attr=\"v\"><!-- c --><Child/></Root>")]
    [InlineData("sql", "SELECT COUNT(*) FROM dbo.Users WHERE Name = 'x'")]
    public void Highlighting_never_loses_or_reorders_the_source(string info, string code)
    {
        var spans = CodeHighlighter.Highlight(code, info);
        Assert.Equal(code, string.Concat(spans.Select(span => span.Text)));
        Assert.DoesNotContain(spans, span => span.Text.Length == 0);
    }

    [Fact]
    public void Powershell_gets_real_tokens_not_one_plain_blob()
    {
        var spans = CodeHighlighter.Highlight(
            "# сводка\n$svc = Get-Service -Name 'Spooler'", "powershell");

        Assert.Contains(spans, span => span.Kind == CodeTokenKind.Comment && span.Text.Contains("сводка"));
        Assert.Contains(spans, span => span.Kind == CodeTokenKind.Variable && span.Text.Contains("$svc"));
        Assert.Contains(spans, span => span.Kind == CodeTokenKind.Function && span.Text.Contains("Get-Service"));
        Assert.Contains(spans, span => span.Kind == CodeTokenKind.String && span.Text.Contains("Spooler"));
    }

    [Fact]
    public void Csharp_keywords_strings_and_numbers_are_separated()
    {
        var spans = CodeHighlighter.Highlight("var x = \"привет\"; int n = 42;", "csharp");

        Assert.Contains(spans, span => span.Kind == CodeTokenKind.Keyword);
        Assert.Contains(spans, span => span.Kind == CodeTokenKind.String && span.Text.Contains("привет"));
        Assert.Contains(spans, span => span.Kind == CodeTokenKind.Number && span.Text.Contains("42"));
    }

    [Fact]
    public void Repeated_highlighting_hits_the_cache()
    {
        // Живой стриминг перерисовывает один и тот же блок десятки раз; регекс-разбор
        // ColorCode повторяться не должен.
        const string code = "$a = 1\n$b = 2\nWrite-Host ($a + $b)";
        var first = CodeHighlighter.Highlight(code, "powershell");
        var second = CodeHighlighter.Highlight(code, "powershell");

        Assert.Same(first, second);
    }

    [Fact]
    public void Language_info_string_may_carry_attributes()
    {
        // ```powershell {highlight=2} — язык это только первое слово.
        Assert.True(CodeHighlighter.IsSupported("powershell {highlight=2}"));
        Assert.True(CodeHighlighter.IsSupported("JSON"));
    }
}
