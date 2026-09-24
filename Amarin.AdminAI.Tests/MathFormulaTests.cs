using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

public sealed class LatexParserTests
{
    [Fact]
    public void Fraction_takes_two_arguments()
    {
        var node = LatexParser.Parse(@"\frac{a+b}{c}");

        var fraction = Assert.IsType<MathFraction>(node);
        Assert.IsType<MathRow>(fraction.Numerator);
        Assert.Equal("c", Assert.IsType<MathSymbol>(fraction.Denominator).Text);
    }

    [Fact]
    public void Root_index_is_optional()
    {
        Assert.Null(Assert.IsType<MathRadical>(LatexParser.Parse(@"\sqrt{x}")).Index);

        var cubic = Assert.IsType<MathRadical>(LatexParser.Parse(@"\sqrt[3]{x}"));
        Assert.Equal("3", Assert.IsType<MathSymbol>(cubic.Index!).Text);
    }

    [Fact]
    public void Scripts_attach_to_the_atom_before_them()
    {
        var scripts = Assert.IsType<MathScripts>(LatexParser.Parse("x_1^{2n}"));

        Assert.Equal("x", Assert.IsType<MathSymbol>(scripts.Base).Text);
        Assert.Equal("1", Assert.IsType<MathSymbol>(scripts.Sub!).Text);
        Assert.IsType<MathRow>(scripts.Sup!);
    }

    [Fact]
    public void Big_operators_and_lim_ask_for_limits_above_and_below()
    {
        Assert.True(Assert.IsType<MathScripts>(LatexParser.Parse(@"\sum_{i=1}^{n}")).Limits);
        Assert.True(Assert.IsType<MathScripts>(LatexParser.Parse(@"\lim_{x \to 0}")).Limits);

        // Обычная буква пределов не берёт — иначе индекс уехал бы под неё.
        Assert.False(Assert.IsType<MathScripts>(LatexParser.Parse("a_i")).Limits);
    }

    [Fact]
    public void Left_and_right_produce_a_stretchable_pair()
    {
        var fenced = Assert.IsType<MathFenced>(LatexParser.Parse(@"\left( x \right)"));

        Assert.Equal("(", fenced.Left);
        Assert.Equal(")", fenced.Right);
    }

    [Fact]
    public void A_dot_after_right_means_no_delimiter()
    {
        var fenced = Assert.IsType<MathFenced>(LatexParser.Parse(@"\left\{ x \right."));

        Assert.Equal("{", fenced.Left);
        Assert.Equal("", fenced.Right);
    }

    [Fact]
    public void Matrix_environments_carry_their_own_brackets()
    {
        var matrix = Assert.IsType<MathMatrix>(LatexParser.Parse(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}"));

        Assert.Equal("(", matrix.Left);
        Assert.Equal(")", matrix.Right);
        Assert.Equal(2, matrix.Rows.Count);
        Assert.Equal(2, matrix.Rows[0].Count);
    }

    [Fact]
    public void Cases_is_left_aligned_and_open_on_the_right()
    {
        var cases = Assert.IsType<MathMatrix>(
            LatexParser.Parse(@"\begin{cases} x & x > 0 \\ -x & x \leq 0 \end{cases}"));

        Assert.Equal("{", cases.Left);
        Assert.Equal("", cases.Right);
        Assert.True(cases.LeftAligned);
    }

    [Fact]
    public void Rows_split_on_a_double_backslash()
    {
        var lines = Assert.IsType<MathLines>(LatexParser.Parse(@"a = 1 \\ b = 2"));

        Assert.Equal(2, lines.Rows.Count);
    }

    [Fact]
    public void Unknown_commands_survive_as_plain_words()
    {
        var node = LatexParser.Parse(@"\nosuchcommand");

        var symbol = Assert.IsType<MathSymbol>(node);
        Assert.Equal("nosuchcommand", symbol.Text);
        Assert.Equal(MathTokenKind.Upright, symbol.Kind);
    }

    [Theory]
    [InlineData(@"\frac{")]
    [InlineData(@"\left(")]
    [InlineData("{{{{{")]
    [InlineData(@"\begin{pmatrix} a &")]
    [InlineData(@"^_^_")]
    [InlineData("")]
    public void Broken_markup_still_returns_something(string latex)
    {
        // Разбор не бросает и не зацикливается: сломанная формула — не повод потерять ответ.
        Assert.NotNull(LatexParser.Parse(latex));
    }
}

public sealed class MathDetectionTests
{
    [Theory]
    [InlineData(@"\frac{1}{2}")]
    [InlineData("x^2")]
    [InlineData("a_i")]
    [InlineData("E = mc")]
    [InlineData("x")]
    [InlineData("a + b")]
    [InlineData("|z|")]
    [InlineData("f(x)")]
    [InlineData("(x, y)")]
    [InlineData("z'")]
    [InlineData("arg(z)")]
    public void Real_formulas_are_recognised(string content) =>
        Assert.True(MathDetection.LooksLikeMath(content));

    [Theory]
    [InlineData("env:VENICE_API_KEY")]
    [InlineData("env:PATH=$env:TEMP")]
    [InlineData("5-")]
    [InlineData("100")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HOME")]
    [InlineData("(TODO)")]
    [InlineData("(см. ниже)")]
    [InlineData("5, 10")]
    public void Prose_between_dollars_is_left_alone(string content) =>
        Assert.False(MathDetection.LooksLikeMath(content));
}

/// <summary>
/// Модели пишут формулы двумя способами: долларами и парами \( \) с \[ \]. Второй вид
/// разметка сама не понимает, поэтому переводится в первый до разбора.
/// </summary>
public sealed class MathDelimiterNormalizerTests
{
    [Fact]
    public void Inline_parentheses_become_single_dollars() =>
        Assert.Equal(
            "Масса $m$ и объём $V$.",
            MathDelimiterNormalizer.ToDollars(@"Масса \(m\) и объём \(V\)."));

    [Fact]
    public void Display_brackets_become_double_dollars() =>
        Assert.Equal(
            "$$\n\\rho = \\frac{m}{V}\n$$",
            MathDelimiterNormalizer.ToDollars("\\[\n\\rho = \\frac{m}{V}\n\\]"));

    [Fact]
    public void Text_without_such_pairs_is_returned_untouched()
    {
        const string text = @"Обычный текст с $ценой$ и \alpha без скобок.";

        Assert.Same(text, MathDelimiterNormalizer.ToDollars(text));
    }

    [Fact]
    public void A_fenced_code_block_is_left_alone()
    {
        const string text = "Пример:\n\n```powershell\nGet-Item \\(x\\)\n```\n\nи \\(y\\) в тексте.";

        var converted = MathDelimiterNormalizer.ToDollars(text);

        Assert.Contains(@"Get-Item \(x\)", converted, StringComparison.Ordinal);
        Assert.Contains("и $y$ в тексте.", converted, StringComparison.Ordinal);
    }

    [Fact]
    public void Inline_code_is_left_alone()
    {
        var converted = MathDelimiterNormalizer.ToDollars(@"Регулярка `\(\d+\)` и формула \(n\).");

        Assert.Contains(@"`\(\d+\)`", converted, StringComparison.Ordinal);
        Assert.Contains("формула $n$.", converted, StringComparison.Ordinal);
    }

    [Fact]
    public void An_escaped_backslash_is_not_a_delimiter()
    {
        // Двойная косая внутри формулы — перенос строки, и её вторая косая не открывает скобку.
        var converted = MathDelimiterNormalizer.ToDollars(@"\[a \\ b\]");

        Assert.Equal(@"$$a \\ b$$", converted);
    }
}

[Collection(WpfCollection.Name)]
public sealed class MathRendererTests
{
    private readonly WpfFixture _wpf;

    public MathRendererTests(WpfFixture wpf) => _wpf = wpf;

    [Theory]
    [InlineData(@"\frac{-b \pm \sqrt{b^2 - 4ac}}{2a}")]
    [InlineData(@"\sum_{i=1}^{n} i^2 = \frac{n(n+1)(2n+1)}{6}")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}")]
    [InlineData(@"f(x) = \begin{cases} x^2 & x \geq 0 \\ -x & x < 0 \end{cases}")]
    [InlineData(@"\int_0^\infty e^{-x^2}\,dx")]
    [InlineData(@"\left( \frac{1}{1 - \frac{1}{n}} \right)^n")]
    public void Formulas_get_a_real_size(string latex)
    {
        var (width, ascent, descent) = _wpf.Ui.Invoke(() =>
        {
            var host = new Window();
            var visual = MathRenderer.BuildVisual(host, latex, 16, display: true);
            return (visual.Width, visual.Ascent, visual.Descent);
        });

        Assert.True(width > 10, $"ширина {width}");
        Assert.True(ascent > 10, $"подъём {ascent}");
        Assert.True(descent >= 0, $"свес {descent}");
    }

    [Fact]
    public void A_fraction_is_taller_than_its_parts()
    {
        var (plain, fraction) = _wpf.Ui.Invoke(() =>
        {
            var host = new Window();
            var single = MathRenderer.BuildVisual(host, "a", 16, display: true);
            var over = MathRenderer.BuildVisual(host, @"\frac{a}{b}", 16, display: true);
            return (single.Ascent + single.Descent, over.Ascent + over.Descent);
        });

        Assert.True(fraction > plain * 1.5, $"дробь {fraction}, буква {plain}");
    }

    [Theory]
    [InlineData(@"z = x + iy")]
    [InlineData(@"e^{i3\pi/4}")]
    [InlineData(@"\cos\varphi + i\sin\varphi")]
    public void The_imaginary_unit_is_set_upright(string latex)
    {
        // Курсивную i в Cambria Math в «+ i sin» и в показателе степени просто не видно.
        var styles = _wpf.Ui.Invoke(() =>
            Glyphs(MathRenderer.BuildVisual(new Window(), latex, 16, display: false).Element)
                .Where(block => block.Text == "i")
                .Select(block => block.FontStyle)
                .ToList());

        Assert.NotEmpty(styles);
        Assert.All(styles, style => Assert.Equal(FontStyles.Normal, style));
    }

    [Theory]
    [InlineData(@"\sum_{i=1}^{n} i^2")]
    [InlineData(@"a_i + b_i")]
    [InlineData(@"i = 1, 2, 3")]
    public void An_index_i_stays_italic(string latex)
    {
        var styles = _wpf.Ui.Invoke(() =>
            Glyphs(MathRenderer.BuildVisual(new Window(), latex, 16, display: true).Element)
                .Where(block => block.Text == "i")
                .Select(block => block.FontStyle)
                .ToList());

        Assert.NotEmpty(styles);
        Assert.All(styles, style => Assert.Equal(FontStyles.Italic, style));
    }

    [Fact]
    public void A_function_name_does_not_stick_to_the_letter_before_it()
    {
        // Без промежутка «i sin» читалось как одно слово «isin».
        var gap = _wpf.Ui.Invoke(() =>
        {
            var visual = MathRenderer.BuildVisual(new Window(), @"i\sin x", 16, display: false);
            var canvas = (Canvas)visual.Element;
            var i = canvas.Children.OfType<TextBlock>().Single(block => block.Text == "i");
            var sin = canvas.Children.OfType<TextBlock>().Single(block => block.Text == "sin");
            return Canvas.GetLeft(sin) - (Canvas.GetLeft(i) + i.DesiredSize.Width);
        });

        Assert.True(gap >= 16 * 0.15, $"промежуток {gap}");
    }

    private static IEnumerable<TextBlock> Glyphs(DependencyObject root)
    {
        if (root is TextBlock block)
        {
            yield return block;
        }

        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                foreach (var nested in Glyphs(child))
                {
                    yield return nested;
                }
            }
        }
        else if (root is Decorator { Child: { } child })
        {
            foreach (var nested in Glyphs(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void Garbage_does_not_throw()
    {
        var built = _wpf.Ui.Invoke(() =>
        {
            var host = new Window();
            foreach (var latex in new[] { @"\frac{", "{{{{", @"\left(", @"\sqrt[", "^^^" })
            {
                MathRenderer.BuildVisual(host, latex, 16, display: false);
            }

            return true;
        });

        Assert.True(built);
    }
}

[Collection(WpfCollection.Name)]
public sealed class ChatMarkdownMathTests
{
    private readonly WpfFixture _wpf;

    public ChatMarkdownMathTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>
    /// Разбор разметки и всё чтение получившегося документа идут на потоке UI: элементы
    /// FlowDocument принадлежат ему и с чужого потока даже не читаются.
    /// </summary>
    private Rendered Render(string markdown) => _wpf.Ui.Invoke(() =>
    {
        var host = new Window();
        var box = new RichTextBox();
        ChatMarkdown.Write(box, host, markdown, 14, 21);

        var blocks = box.Document.Blocks.ToList();
        var paragraphs = blocks.OfType<Paragraph>().ToList();
        var paragraph = paragraphs.FirstOrDefault();

        return new Rendered(
            blocks.OfType<BlockUIContainer>().Count(),
            paragraphs.Count,
            paragraph is not null && paragraph.Inlines.Any(inline => inline is InlineUIContainer),
            paragraph is null || double.IsNaN(paragraph.LineHeight),
            paragraph is null ? "" : new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text);
    });

    private sealed record Rendered(
        int MathBlocks,
        int Paragraphs,
        bool HasInlineFormula,
        bool LineHeightIsAuto,
        string Text);

    [Fact]
    public void Display_math_becomes_its_own_block() =>
        Assert.Equal(1, Render(@"$$x = \frac{1}{2}$$").MathBlocks);

    [Fact]
    public void Multiline_display_math_becomes_its_own_block() =>
        Assert.Equal(1, Render("$$\n\\frac{1}{2}\n$$").MathBlocks);

    [Fact]
    public void Inline_math_lands_inside_the_paragraph()
    {
        var rendered = Render(@"Корень $\sqrt{2}$ иррационален.");

        Assert.Equal(1, rendered.Paragraphs);
        Assert.True(rendered.HasInlineFormula);

        // Формула выше строки, поэтому у такого абзаца интерлиньяж отпускается на содержимое.
        Assert.True(rendered.LineHeightIsAuto);
    }

    [Fact]
    public void A_shell_variable_is_not_a_formula()
    {
        var rendered = Render("Проверьте $env:VENICE_API_KEY и $env:PATH в оболочке.");

        Assert.False(rendered.HasInlineFormula);
        Assert.Contains("$env:VENICE_API_KEY", rendered.Text);
    }

    [Fact]
    public void Latex_style_display_math_becomes_its_own_block() =>
        Assert.Equal(1, Render("\\[\n\\rho = \\frac{m}{V}\n\\]").MathBlocks);

    [Fact]
    public void Latex_style_inline_math_renders_inside_the_line() =>
        Assert.True(Render(@"Плотность \(\rho\) считается так.").HasInlineFormula);

    [Fact]
    public void A_math_fence_becomes_a_block() =>
        Assert.Equal(1, Render("```math\nE = mc^2\n```").MathBlocks);

    [Fact]
    public void A_price_range_is_not_a_formula()
    {
        var rendered = Render("Цена $5-$10 за штуку.");

        Assert.False(rendered.HasInlineFormula);
        Assert.Contains("$5-$10", rendered.Text);
    }
}
