using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// <see cref="DownloadValidator"/> keeps the allowlist in process-wide static state, so every test
/// touching it must run in this collection and restore the previous list.
/// </summary>
[CollectionDefinition("DownloadValidator", DisableParallelization = true)]
public sealed class DownloadValidatorCollection;

[Collection("DownloadValidator")]
public sealed class DomainListTests
{
    [Theory]
    [InlineData("github.com", "github.com")]
    [InlineData("  GitHub.COM  ", "github.com")]
    [InlineData("https://GitHub.com/user/repo?x=1", "github.com")]
    [InlineData("http://www.example.org:8080/path", "example.org")]
    [InlineData("user:pass@files.example.net/a/b", "files.example.net")]
    [InlineData("example.com.", "example.com")]
    [InlineData("dl.discordapp.net", "dl.discordapp.net")]
    public void Normalize_reduces_input_to_a_bare_host(string input, string expected) =>
        Assert.Equal(expected, DomainList.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://")]
    [InlineData("a..b.com")]
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("has space.com")]
    public void Normalize_rejects_unusable_input(string? input) =>
        Assert.Null(DomainList.Normalize(input));

    [Fact]
    public void TryAdd_normalizes_and_rejects_duplicates()
    {
        var list = new List<string>();

        Assert.True(DomainList.TryAdd(list, "https://www.GitHub.com/a", out var error));
        Assert.Equal("", error);
        Assert.Equal(["github.com"], list);

        Assert.False(DomainList.TryAdd(list, "github.com", out error));
        Assert.Contains("уже в списке", error, StringComparison.Ordinal);
        Assert.Single(list);

        Assert.False(DomainList.TryAdd(list, "   ", out error));
        Assert.NotEmpty(error);
        Assert.Single(list);
    }

    [Fact]
    public void TryGetHostFromToolArguments_reads_the_url_argument()
    {
        Assert.True(DomainList.TryGetHostFromToolArguments(
            """{"url":"https://files.example.org/setup.exe"}""", out var host));
        Assert.Equal("files.example.org", host);

        Assert.False(DomainList.TryGetHostFromToolArguments("""{"url":"not a url"}""", out _));
        Assert.False(DomainList.TryGetHostFromToolArguments("""{"path":"c:\\tmp"}""", out _));
        Assert.False(DomainList.TryGetHostFromToolArguments("not json", out _));
        Assert.False(DomainList.TryGetHostFromToolArguments(null, out _));
    }
}

[Collection("DownloadValidator")]
public sealed class DownloadValidatorTests : IDisposable
{
    private readonly string[] _saved = [.. DownloadValidator.AllowedDomains];

    public void Dispose() => DownloadValidator.ConfigureAllowedDomains(_saved);

    [Fact]
    public void IsDomainAllowed_matches_the_host_and_its_subdomains()
    {
        DownloadValidator.ConfigureAllowedDomains(["github.com"]);

        Assert.True(DownloadValidator.IsDomainAllowed("github.com"));
        Assert.True(DownloadValidator.IsDomainAllowed("GITHUB.COM"));
        Assert.True(DownloadValidator.IsDomainAllowed("raw.github.com"));
        Assert.False(DownloadValidator.IsDomainAllowed("notgithub.com"));
        Assert.False(DownloadValidator.IsDomainAllowed("github.com.evil.net"));
        Assert.False(DownloadValidator.IsDomainAllowed(""));
    }

    [Fact]
    public void Empty_list_blocks_everything()
    {
        DownloadValidator.ConfigureAllowedDomains([]);

        Assert.False(DownloadValidator.IsDomainAllowed("github.com"));
        Assert.False(DownloadValidator.IsDomainAllowed("microsoft.com"));
    }

    [Fact]
    public void ConfigureAllowedDomains_normalizes_and_deduplicates()
    {
        DownloadValidator.ConfigureAllowedDomains(
            ["https://www.GitHub.com/a", "github.com", "  ", "bad-.com", "example.org:443"]);

        Assert.Equal(["github.com", "example.org"], DownloadValidator.AllowedDomains);
    }

    [Fact]
    public void Null_restores_the_shipped_defaults()
    {
        DownloadValidator.ConfigureAllowedDomains([]);
        DownloadValidator.ConfigureAllowedDomains(null);

        Assert.Equal(new DownloadOptions().AllowedDomains, DownloadValidator.AllowedDomains);
    }
}

[Collection("DownloadValidator")]
public sealed class WebDownloadToolAllowlistTests : IDisposable
{
    private readonly string[] _saved = [.. DownloadValidator.AllowedDomains];

    public void Dispose() => DownloadValidator.ConfigureAllowedDomains(_saved);

    [Fact]
    public async Task Blocked_host_fails_before_any_network_call()
    {
        DownloadValidator.ConfigureAllowedDomains(["github.com"]);

        // A handler that would throw proves the tool never reached the network.
        using var http = new HttpClient(new ThrowingHandler());
        var tool = new WebDownloadTool(http, new DownloadOptions());

        var result = await tool.ExecuteAsync(
            JsonSchema.Parse("""{"url":"https://evil.example.net/setup.exe"}"""));

        Assert.False(result.Success);
        Assert.StartsWith(DomainList.BlockedMarker, result.Output, StringComparison.Ordinal);
        Assert.Contains("evil.example.net", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocked_marker_survives_the_chat_preview_truncation()
    {
        DownloadValidator.ConfigureAllowedDomains(["github.com"]);
        using var http = new HttpClient(new ThrowingHandler());
        var tool = new WebDownloadTool(http, new DownloadOptions());

        var result = await tool.ExecuteAsync(
            JsonSchema.Parse("""{"url":"https://evil.example.net/setup.exe"}"""));

        // The UI recognises a blocked download from ToolCallRecord.ResultPreview.
        var preview = ChatToolPreview.Summarize(result);
        Assert.StartsWith(DomainList.BlockedMarker, preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Allowed_subdomain_passes_the_allowlist_check()
    {
        DownloadValidator.ConfigureAllowedDomains(["example.net"]);
        using var http = new HttpClient(new ThrowingHandler());
        var tool = new WebDownloadTool(http, new DownloadOptions());

        var result = await tool.ExecuteAsync(
            JsonSchema.Parse("""{"url":"https://files.example.net/setup.exe"}"""));

        // It gets past the allowlist and dies at the network stub instead.
        Assert.False(result.Success);
        Assert.DoesNotContain(DomainList.BlockedMarker, result.Output, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("network must not be reached");
    }
}
