using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Лимит токенов ключа: как его набирают и как он показывается.
/// </summary>
/// <remarks>
/// Пакеты у перепродавцов — миллионы токенов, и набирают их как придётся: с пробелами, с «M»,
/// по-русски «млн». Все записи одного числа должны читаться одинаково, а пустое поле — снимать
/// лимит, а не быть ошибкой.
/// </remarks>
public sealed class TokenCountTests
{
    [Theory]
    [InlineData("10000000", 10_000_000)]
    [InlineData("10 000 000", 10_000_000)]
    [InlineData("10,000,000", 10_000_000)]
    [InlineData("10M", 10_000_000)]
    [InlineData("10 млн", 10_000_000)]
    [InlineData("1,5 млн", 1_500_000)]
    [InlineData("2.5m", 2_500_000)]
    [InlineData("500k", 500_000)]
    [InlineData("500 тыс.", 500_000)]
    public void Every_spelling_of_a_package_reads_as_one_number(string typed, long expected)
    {
        Assert.True(TokenCount.TryParseLimit(typed, out var limit));
        Assert.Equal(expected, limit);
    }

    [Fact]
    public void An_empty_field_clears_the_limit()
    {
        Assert.True(TokenCount.TryParseLimit("  ", out var limit));
        Assert.Null(limit);
    }

    [Theory]
    [InlineData("много")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("1,2345 k")]
    public void Nonsense_is_refused(string typed) =>
        Assert.False(TokenCount.TryParseLimit(typed, out _));

    [Fact]
    public void Millions_are_shown_as_millions()
    {
        Assert.Equal(Loc.Format("S.Tokens.Millions", "10"), TokenCount.Format(10_000_000));
        Assert.Equal(Loc.Format("S.Tokens.Millions", "1,24"), TokenCount.Format(1_240_000));
        Assert.Equal("950 000", TokenCount.Format(950_000));
    }
}
