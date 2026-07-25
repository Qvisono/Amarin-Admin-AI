using Amarin.UI;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Amarin.AdminAI.Tests;

public sealed class MarkdownConsoleRendererTests
{
    private static (MarkdownConsoleRenderer Renderer, TestConsole Console) Create()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Ansi = true;
        console.Profile.Width = 120;
        var renderer = new MarkdownConsoleRenderer(console);
        return (renderer, console);
    }

    private static string RenderToString(string markdown)
    {
        var (renderer, console) = Create();
        renderer.Render(markdown);
        return console.Output;
    }

    private static string MarkupOf(string markdown) =>
        MarkdownConsoleRenderer.ToSpectreMarkup(markdown);

    [Fact]
    public void Unpaired_asterisk_is_literal_not_italic()
    {
        var markup = MarkupOf("price is 5*6=30");
        Assert.DoesNotContain("[italic]", markup);
        Assert.Contains("5*6=30", markup);

        var output = RenderToString("price is 5*6=30");
        Assert.Contains("5*6=30", output);
        // Should not throw; plain asterisk remains visible.
        Assert.DoesNotContain("\u001b[3m", output); // italic SGR when ANSI on — unpaired should not italicize
    }

    [Fact]
    public void Formatting_inside_fenced_code_block_is_not_applied()
    {
        var md = """
            ```csharp
            var x = **bold** and *italic* and `code`;
            ```
            """;

        var (renderer, console) = Create();
        var renderable = renderer.ToRenderable(md);
        console.Write(renderable);
        var output = console.Output;

        // Literal markers must remain; no Spectre bold/italic applied to code body.
        Assert.Contains("**bold**", output);
        Assert.Contains("*italic*", output);
        Assert.Contains("`code`", output);
    }

    [Fact]
    public void Bracket_characters_are_escaped_for_spectre_markup()
    {
        // Classic crash case: array[0] → Spectre treats [0] as markup tag.
        var markup = MarkupOf("array[0] and [link-like]");
        Assert.Contains("array[[0]]", markup);
        Assert.Contains("[[link-like]]", markup);

        // Render must not throw.
        var output = RenderToString("use array[0] carefully");
        Assert.Contains("array[0]", output);
    }

    [Fact]
    public void Unclosed_fenced_code_at_end_renders_as_code_without_throwing()
    {
        var md = """
            Before

            ```python
            def foo():
                return **not_bold**
            """;

        var output = RenderToString(md);
        Assert.Contains("def foo():", output);
        Assert.Contains("**not_bold**", output);
        Assert.Contains("Before", output);
    }

    [Fact]
    public void Nested_lists_indent_two_spaces_per_level()
    {
        var md = """
            - outer
              - inner
                - deep
            """;

        var markup = MarkupOf(md);
        Assert.Contains("• outer", markup);
        Assert.Contains("  • inner", markup);
        Assert.Contains("    • deep", markup);

        var output = RenderToString(md);
        Assert.Contains("outer", output);
        Assert.Contains("inner", output);
        Assert.Contains("deep", output);
    }

    [Fact]
    public void Lists_inside_blockquotes_render()
    {
        var md = """
            > - quoted item
            > - second
            """;

        var output = RenderToString(md);
        Assert.Contains("quoted item", output);
        Assert.Contains("second", output);
        Assert.Contains("│", output);
    }

    [Fact]
    public void Bold_and_italic_render()
    {
        var markup = MarkupOf("**bold** and *italic*");
        Assert.Contains("[bold]", markup);
        Assert.Contains("[italic]", markup);
        Assert.Contains("bold", markup);
        Assert.Contains("italic", markup);
    }

    [Fact]
    public void Inline_code_is_highlighted_and_escaped()
    {
        var markup = MarkupOf("use `array[0]` here");
        Assert.Contains("array[[0]]", markup);
        Assert.Contains("on grey", markup);
    }

    [Fact]
    public void Headings_use_bold_and_level_color()
    {
        var markup = MarkupOf("# Title\n## Sub");
        Assert.Contains("[bold", markup);
        Assert.Contains("Title", markup);
        Assert.Contains("Sub", markup);
    }

    [Fact]
    public void Horizontal_rule_renders()
    {
        var output = RenderToString("above\n\n---\n\nbelow");
        Assert.Contains("above", output);
        Assert.Contains("below", output);
        // Spectre Rule draws a line of box-drawing / dashes
        Assert.True(
            output.Contains('─') || output.Contains('-') || output.Contains('═'),
            $"Expected a rule character in output:\n{output}");
    }

    [Fact]
    public void Links_show_as_text_url()
    {
        var markup = MarkupOf("see [docs](https://example.com/path)");
        Assert.Contains("docs", markup);
        Assert.Contains("(https://example.com/path)", markup);

        var output = RenderToString("see [docs](https://example.com/path)");
        Assert.Contains("docs", output);
        Assert.Contains("https://example.com/path", output);
    }

    [Fact]
    public void Table_renders_via_spectre_table()
    {
        var md = """
            | a | b |
            |---|---|
            | 1 | 2 |
            """;

        var output = RenderToString(md);
        Assert.Contains("a", output);
        Assert.Contains("b", output);
        Assert.Contains("1", output);
        Assert.Contains("2", output);
    }

    [Fact]
    public void Ordered_list_preserves_numbering()
    {
        var markup = MarkupOf("1. first\n2. second\n3. third");
        Assert.Contains("1.", markup);
        Assert.Contains("2.", markup);
        Assert.Contains("3.", markup);
        Assert.Contains("first", markup);
    }

    [Fact]
    public void Streaming_renders_complete_lines_and_buffers_code_fences()
    {
        var (renderer, console) = Create();

        renderer.AppendChunk("# He");
        Assert.True(string.IsNullOrWhiteSpace(console.Output) || !console.Output.Contains("Hello"));

        renderer.AppendChunk("llo\n");
        Assert.Contains("Hello", console.Output);

        renderer.AppendChunk("```js\nconst x = 1;\n");
        // Code fence still open — body may not be finalized as panel yet
        var mid = console.Output;

        renderer.AppendChunk("const y = 2;\n```\n");
        Assert.Contains("const x = 1;", console.Output);
        Assert.Contains("const y = 2;", console.Output);
        Assert.True(console.Output.Length >= mid.Length);
    }

    [Fact]
    public void Streaming_unclosed_fence_flushed_on_complete()
    {
        var (renderer, console) = Create();
        renderer.AppendChunk("```\npartial code");
        renderer.Complete();
        Assert.Contains("partial code", console.Output);
    }

    [Fact]
    public void Streaming_chunk_with_brackets_does_not_throw()
    {
        var (renderer, console) = Create();
        renderer.AppendChunk("value array[0]\n");
        renderer.Complete();
        Assert.Contains("array[0]", console.Output);
    }

    [Fact]
    public void Render_empty_and_null_safe()
    {
        var (renderer, console) = Create();
        renderer.Render("");
        renderer.Render("   ");
        Assert.NotNull(console.Output);
    }

    [Fact]
    public void Ansi_disabled_fallback_still_emits_readable_text()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Width = 100;
        var renderer = new MarkdownConsoleRenderer(console);

        renderer.Render("**bold** text with `array[0]` and [link](http://x.test)");
        var output = console.Output;

        Assert.Contains("bold", output);
        Assert.Contains("array[0]", output);
        Assert.Contains("link", output);
        Assert.Contains("http://x.test", output);
    }

    [Fact]
    public void Fenced_code_panel_includes_language_header()
    {
        var md = """
            ```python
            print(1)
            ```
            """;
        var output = RenderToString(md);
        Assert.Contains("python", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("print(1)", output);
    }

    [Fact]
    public void Nested_list_inside_blockquote()
    {
        var md = """
            > - top
            >   - nested under quote
            """;

        var output = RenderToString(md);
        Assert.Contains("top", output);
        Assert.Contains("nested under quote", output);
    }
}
