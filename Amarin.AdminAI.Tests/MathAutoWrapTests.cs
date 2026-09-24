using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Слэш между простыми операндами набирается дробью: модели пишут <c>3\pi/4</c> куда чаще,
/// чем <c>\frac</c>, а человек ждёт числитель над знаменателем.
/// </summary>
public sealed class SlashFractionTests
{
    [Fact]
    public void A_slash_between_simple_operands_becomes_a_fraction()
    {
        var fraction = Assert.IsType<MathFraction>(LatexParser.Parse(@"3\pi/4"));

        var numerator = Assert.IsType<MathRow>(fraction.Numerator);
        Assert.Equal(["3", "π"], numerator.Items.Select(item => Assert.IsType<MathSymbol>(item).Text));
        Assert.Equal("4", Assert.IsType<MathSymbol>(fraction.Denominator).Text);
    }

    [Fact]
    public void A_function_name_ends_the_numerator()
    {
        // cos π/3 — косинус от π/3, а не косинус, делённый на три.
        var row = Assert.IsType<MathRow>(LatexParser.Parse(@"\cos \pi/3"));

        Assert.Equal("cos", Assert.IsType<MathSymbol>(row.Items[0]).Text);
        var fraction = Assert.IsType<MathFraction>(row.Items[1]);
        Assert.Equal("π", Assert.IsType<MathSymbol>(fraction.Numerator).Text);
    }

    [Fact]
    public void A_function_applied_to_brackets_is_divided_whole()
    {
        var fraction = Assert.IsType<MathFraction>(LatexParser.Parse(@"\sin(x)/2"));

        var numerator = Assert.IsType<MathRow>(fraction.Numerator);
        Assert.Equal("sin", Assert.IsType<MathSymbol>(numerator.Items[0]).Text);
    }

    [Fact]
    public void Brackets_around_a_whole_operand_give_way_to_the_bar()
    {
        var fraction = Assert.IsType<MathFraction>(LatexParser.Parse("(a+b)/(c+d)"));

        Assert.Equal(3, Assert.IsType<MathRow>(fraction.Numerator).Items.Count);
        Assert.Equal(3, Assert.IsType<MathRow>(fraction.Denominator).Items.Count);
    }

    [Fact]
    public void A_minus_stays_in_front_of_the_fraction()
    {
        var row = Assert.IsType<MathRow>(LatexParser.Parse("-a/b"));

        Assert.Equal("−", Assert.IsType<MathSymbol>(row.Items[0]).Text);
        Assert.IsType<MathFraction>(row.Items[1]);
    }

    [Fact]
    public void The_denominator_is_one_unit()
    {
        // 1/2x — половина икс, а не единица, делённая на 2x.
        var row = Assert.IsType<MathRow>(LatexParser.Parse("1/2x"));

        Assert.IsType<MathFraction>(row.Items[0]);
        Assert.Equal("x", Assert.IsType<MathSymbol>(row.Items[1]).Text);
    }

    [Fact]
    public void Chained_slashes_fold_from_the_left()
    {
        var outer = Assert.IsType<MathFraction>(LatexParser.Parse("a/b/c"));

        Assert.IsType<MathFraction>(outer.Numerator);
        Assert.Equal("c", Assert.IsType<MathSymbol>(outer.Denominator).Text);
    }

    [Fact]
    public void A_slash_inside_an_exponent_stays_a_slash()
    {
        // Этажная дробь в степени нечитаемо мелкая — там слэш уместнее.
        var scripts = Assert.IsType<MathScripts>(LatexParser.Parse("e^{x/2}"));

        var exponent = Assert.IsType<MathRow>(scripts.Sup);
        Assert.Contains(exponent.Items, item => item is MathSymbol { Text: "/" });
    }

    [Theory]
    [InlineData("x/")]
    [InlineData("/2")]
    [InlineData("a + /b")]
    public void A_slash_without_operands_is_left_alone(string latex)
    {
        var parsed = LatexParser.Parse(latex);

        var items = parsed is MathRow row ? row.Items : [parsed];
        Assert.DoesNotContain(items, item => item is MathFraction);
    }
}

/// <summary>
/// Формулы, оставленные моделью без долларов, заворачиваются в них до разбора разметки.
/// </summary>
/// <remarks>
/// Ошибиться в сторону «формулы» дороже, чем пропустить её, — поэтому здесь столько же
/// проверок на то, что обычный текст остался текстом.
/// </remarks>
public sealed class MathAutoWrapTests
{
    [Theory]
    [InlineData(
        @"z = 2(\cos \frac{\pi}{3} + i\sin \frac{\pi}{3})",
        @"$z = 2(\cos \frac{\pi}{3} + i\sin \frac{\pi}{3})$")]
    [InlineData(@"Итого = \frac{3\pi}{4}, это ответ.", @"Итого = $\frac{3\pi}{4}$, это ответ.")]
    [InlineData(@"где \alpha — угол", @"где $\alpha$ — угол")]
    [InlineData(@"(где \alpha)", @"(где $\alpha$)")]
    [InlineData(@"код `a = \frac` и \frac{1}{2} тут", @"код `a = \frac` и $\frac{1}{2}$ тут")]
    public void Bare_latex_gets_its_dollars(string line, string expected) =>
        Assert.Equal(expected, MathDelimiterNormalizer.ToDollars(line));

    [Theory]
    [InlineData("z = 2(cos π/3 + isin π/3)", @"$z = 2 ( \cos π / 3 + i \sin π / 3)$")]
    [InlineData(
        "Тригонометрическая форма: z = 2(cos π / 3 + i sin π / 3).",
        @"Тригонометрическая форма: $z = 2 ( \cos π / 3 + i \sin π / 3)$.")]
    [InlineData("- **z = 2(cos π/3 + i sin π/3)**", @"- **$z = 2 ( \cos π / 3 + i \sin π / 3)$**")]
    [InlineData("S = πr²", "$S = π r^{2}$")]
    [InlineData("√(x² + y²)", @"$\sqrt{x^{2} + y^{2}}$")]
    [InlineData("и z = 2(cos π/3)", @"и $z = 2 ( \cos π / 3)$")]
    [InlineData("Угол равен 3π/4 радиан.", "Угол равен $3 π / 4$ радиан.")]
    [InlineData("если x = 5, то", "если $x = 5$, то")]
    [InlineData("| Угол | 3π/4 |", "| Угол | $3 π / 4$ |")]
    public void A_plain_text_formula_becomes_latex(string line, string expected) =>
        Assert.Equal(expected, MathDelimiterNormalizer.ToDollars(line));

    [Theory]
    [InlineData("12/05/2024")]
    [InlineData("и/или")]
    [InlineData("TCP/IP")]
    [InlineData("x86/x64")]
    [InlineData("Windows 10/11")]
    [InlineData("a/b")]
    [InlineData("1/2 стакана")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"Путь C:\pi\file")]
    [InlineData(@"Используйте \n для переноса")]
    [InlineData("$env:PATH = 1")]
    [InlineData("HP LaserJet 1010")]
    [InlineData("Ctrl + C")]
    [InlineData("запусти sfc /scannow")]
    [InlineData("Get-Process | where CPU > 50")]
    [InlineData("Версия v1.2 = новая")]
    [InlineData("https://example.com/a/b")]
    [InlineData("# Заголовок y = x^2")]
    [InlineData("`x = π/3`")]
    public void Ordinary_text_stays_text(string line) =>
        Assert.Same(line, MathDelimiterNormalizer.ToDollars(line));

    [Fact]
    public void A_fenced_block_is_not_searched_for_formulas()
    {
        var converted = MathDelimiterNormalizer.ToDollars("```\nx = π/3\n```\nx = π/3");

        Assert.Equal("```\nx = π/3\n```\n$x = π / 3$", converted);
    }

    [Fact]
    public void The_middle_of_a_display_formula_is_not_wrapped_twice()
    {
        // Средняя строка $$…$$ долларов на себе не несёт — без учёта блока она получила бы
        // свои собственные и сломала бы формулу.
        const string text = "$$\nz = \\frac{a}{b}\n$$";

        Assert.Same(text, MathDelimiterNormalizer.ToDollars(text));
        Assert.Equal("$$\n\\rho = \\frac{m}{V}\n$$", MathDelimiterNormalizer.ToDollars("\\[\n\\rho = \\frac{m}{V}\n\\]"));
    }

    [Fact]
    public void A_leading_empty_line_keeps_its_line_break()
    {
        Assert.Equal("\n$x = π / 3$", MathDelimiterNormalizer.ToDollars("\nx = π/3"));
    }

    [Fact]
    public void The_chat_is_told_how_to_write_formulas()
    {
        Assert.Contains(@"\frac{numerator}{denominator}", ChatEngine.FormulaRules, StringComparison.Ordinal);
        Assert.StartsWith("FORMULAS", ChatEngine.FormulaRules, StringComparison.Ordinal);
    }
}
