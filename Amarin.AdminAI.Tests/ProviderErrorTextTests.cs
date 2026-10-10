using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отказ провайдера показывается словами, а не страницей разметки.
/// </summary>
/// <remarks>
/// Защита сайта OpenRouter отвечает адресу из страны, которую сервис не обслуживает, целой
/// HTML-страницей «Access Denied». До 1.34.0 она уходила в плашку выбора модели как есть.
/// </remarks>
public sealed class ProviderErrorTextTests
{
    [Fact]
    public void An_html_page_is_told_by_its_title()
    {
        const string page = """
            <!DOCTYPE html><html><head><title>Access Denied</title></head>
            <body><h1>Access Denied</h1><p>Reference #18.5f2a</p></body></html>
            """;

        Assert.Equal("Access Denied", ProviderErrorText.Detail(page));
    }

    [Fact]
    public void An_html_page_without_a_title_loses_its_markup_and_scripts()
    {
        const string page = """
            <html><body><script>var x = "<b>";</script><h1>502 Bad   Gateway</h1>
            <p>nginx &amp; friends</p></body></html>
            """;

        Assert.Equal("502 Bad Gateway nginx & friends", ProviderErrorText.Detail(page));
    }

    [Fact]
    public void A_long_text_is_cut()
    {
        var text = ProviderErrorText.Detail(new string('x', 1000));

        Assert.Equal(ProviderErrorText.DetailLimit + 1, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_body_says_so()
    {
        Assert.Equal("No details", ProviderErrorText.Detail("  "));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void A_refused_access_is_named_with_its_likely_reasons(int status)
    {
        var error = new VeniceApiException("OpenRouter API error: Access Denied") { StatusCode = status };

        Assert.Equal(
            Loc.Format("S.Models.LoadDenied", "OpenRouter", status),
            ProviderErrorText.ForCatalog(error, "OpenRouter"));
    }

    [Fact]
    public void A_server_failure_is_not_blamed_on_the_key()
    {
        var error = new VeniceApiException("Venice API error (502): Bad Gateway") { StatusCode = 502 };

        Assert.Equal(
            Loc.Format("S.Models.LoadServer", "Venice", 502),
            ProviderErrorText.ForCatalog(error, "Venice"));
    }

    [Fact]
    public void Anything_else_keeps_a_short_detail()
    {
        var error = new VeniceApiException("OpenRouter API error (404): <html><title>Not Found</title></html>")
        {
            StatusCode = 404
        };

        var text = ProviderErrorText.ForCatalog(error, "OpenRouter");

        Assert.StartsWith(Loc.Format("S.Models.LoadFailed", "OpenRouter", ""), text, StringComparison.Ordinal);
        Assert.Contains("404", text, StringComparison.Ordinal);
    }
}
