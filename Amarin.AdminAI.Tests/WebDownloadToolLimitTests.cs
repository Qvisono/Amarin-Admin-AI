using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Размер проверяется по заголовку и по потоку; загрузки пишутся только во временную папку.</summary>
[Collection("DownloadValidator")]
public sealed class WebDownloadToolLimitTests : IDisposable
{
    private const int MiB = 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-download-limit-" + Guid.NewGuid().ToString("N"));
    private readonly List<HttpClient> _clients = [];

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task An_old_profile_keeps_the_limit_from_configuration()
    {
        var tool = Tool(new byte[9], reportLength: true, () => new AppSettings(), configuredBytes: 8);

        var result = await Download(tool);

        Assert.False(result.Success);
        Assert.Contains("8", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(Destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_profile_can_raise_the_limit_and_the_exact_boundary_is_allowed(bool reportLength)
    {
        var payload = new byte[MiB];
        var tool = Tool(payload, reportLength, () => new AppSettings { DownloadMaxSizeMb = 1 }, configuredBytes: 8);
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var result = await Download(tool, JsonSchema.Parse($$"""{"expected_sha256":"{{hash}}"}"""));

        Assert.True(result.Success, result.Output);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Destination));
        Assert.Equal(MiB, Assert.Single(result.GetFiles()).SizeBytes);
        Assert.Contains(hash, result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exceeding_the_profile_limit_leaves_no_partial_file(bool reportLength)
    {
        var tool = Tool(new byte[MiB + 1], reportLength, () => new AppSettings { DownloadMaxSizeMb = 1 });

        var result = await Download(tool);

        Assert.False(result.Success);
        Assert.Equal(Loc.Format("S.Tool.Download.SizeLimit", MiB), result.Output);
        Assert.False(File.Exists(Destination));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Turning_the_limit_off_allows_a_file_larger_than_both_saved_limits(bool reportLength)
    {
        var tool = Tool(new byte[MiB + 1], reportLength,
            () => new AppSettings { DownloadSizeLimitEnabled = false, DownloadMaxSizeMb = 1 }, configuredBytes: 8);

        var result = await Download(tool);

        Assert.True(result.Success, result.Output);
        Assert.Equal(MiB + 1, new FileInfo(Destination).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_per_call_limit_still_applies_when_the_profile_limit_is_off(bool reportLength)
    {
        var tool = Tool(new byte[MiB + 1], reportLength,
            () => new AppSettings { DownloadSizeLimitEnabled = false });

        var result = await Download(tool, JsonSchema.Parse("""{"max_size_mb":1}"""));

        Assert.False(result.Success);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task A_per_call_limit_cannot_raise_the_profile_limit()
    {
        var tool = Tool(new byte[MiB + 1], reportLength: true,
            () => new AppSettings { DownloadMaxSizeMb = 1 });

        var result = await Download(tool, JsonSchema.Parse("""{"max_size_mb":2}"""));

        Assert.False(result.Success);
        Assert.False(File.Exists(Destination));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("2147483648")]
    [InlineData("\"1\"")]
    [InlineData("null")]
    public async Task An_invalid_per_call_limit_is_refused_before_the_request(string value)
    {
        var handler = new PayloadHandler(() => throw new InvalidOperationException("HTTP must not run"));
        var http = new HttpClient(handler);
        _clients.Add(http);
        var tool = new WebDownloadTool(http, new DownloadOptions());

        var result = await Download(tool, JsonSchema.Parse($$"""{"max_size_mb":{{value}}}"""));

        Assert.False(result.Success);
        Assert.Equal(Loc.Get("S.Tool.Download.InvalidSize"), result.Output);
        Assert.Equal(0, handler.Requests);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task The_same_tool_reads_the_new_settings_object_between_downloads()
    {
        var settings = new AppSettings { DownloadMaxSizeMb = 1 };
        var tool = Tool(new byte[MiB + 1], reportLength: true, () => settings);
        Assert.False((await Download(tool)).Success);

        // Смена профиля заменяет объект настроек, но не уже собранные инструменты рецептов.
        settings = new AppSettings { DownloadMaxSizeMb = 2 };
        Assert.True((await Download(tool)).Success);
    }

    [Fact]
    public async Task The_agent_tool_registry_passes_the_live_settings_reader_to_downloads()
    {
        var downloadHttp = new HttpClient(new PayloadHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new PayloadContent(new byte[9], reportLength: true, afterRead: null, declaredBytes: null)
        }));
        _clients.Add(downloadHttp);
        using var providerHttp = new HttpClient(new HttpClientHandler());
        var options = new AgentOptions();
        var settings = new AppSettings();
        var registry = AgentTools.Create(new VeniceClient(providerHttp, options), downloadHttp,
            new DownloadOptions { MaxSizeBytes = 8 }, readSettings: () => settings);
        var tool = Assert.IsType<WebDownloadTool>(registry.All.Single(item => item.Name == "download_file"));

        Assert.False((await Download(tool)).Success);
        settings.DownloadSizeLimitEnabled = false;
        Assert.True((await Download(tool)).Success);
    }

    [Fact]
    public async Task A_tool_that_reads_the_store_follows_the_active_profile()
    {
        var firstRoot = Path.Combine(_root, "first");
        var secondRoot = Path.Combine(_root, "second");
        new AppSettingsStore(firstRoot).Save(new AppSettings { DownloadMaxSizeMb = 1 });
        new AppSettingsStore(secondRoot).Save(new AppSettings { DownloadMaxSizeMb = 2 });
        using var services = UiServices.Build(firstRoot, "test-key", new HttpClientHandler());
        var tool = Tool(new byte[MiB + 1], reportLength: true, () => services.SettingsStore.Load());

        Assert.False((await Download(tool)).Success);
        services.UseProfile(secondRoot);
        Assert.True((await Download(tool)).Success);
    }

    [Fact]
    public async Task A_started_download_keeps_its_limit_until_the_call_ends()
    {
        var settings = new AppSettings { DownloadMaxSizeMb = 1 };
        var tool = Tool(new byte[MiB + 1], reportLength: false, () => settings,
            afterRead: () => settings.DownloadSizeLimitEnabled = false);

        var result = await Download(tool);

        Assert.False(result.Success);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task Cancelling_a_streaming_download_removes_its_partial_file()
    {
        using var cancel = new CancellationTokenSource();
        var tool = Tool(new byte[MiB + 1], reportLength: false,
            () => new AppSettings { DownloadSizeLimitEnabled = false }, afterRead: cancel.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.DownloadAsync(new Uri("https://example.org/file.bin"), Destination, JsonSchema.Parse("{}"), cancel.Token));

        Assert.False(File.Exists(Destination));
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(int.MaxValue)]
    public async Task Limits_larger_than_two_gibibytes_do_not_overflow(int maxMb)
    {
        var maxBytes = maxMb * (long)MiB;
        var tool = Tool([], reportLength: true, () => new AppSettings { DownloadMaxSizeMb = maxMb },
            declaredBytes: maxBytes + 1);

        var result = await Download(tool);

        Assert.False(result.Success);
        Assert.Equal(Loc.Format("S.Tool.Download.SizeLimit", maxBytes), result.Output);
        Assert.False(File.Exists(Destination));
    }

    private string Destination => Path.Combine(_root, "file.bin");

    private Task<ToolResult> Download(WebDownloadTool tool, JsonElement? arguments = null) =>
        tool.DownloadAsync(new Uri("https://example.org/file.bin"), Destination, arguments ?? JsonSchema.Parse("{}"));

    private WebDownloadTool Tool(byte[] payload, bool reportLength, Func<AppSettings> settings,
        long configuredBytes = 100L * MiB, Action? afterRead = null, long? declaredBytes = null)
    {
        var http = new HttpClient(new PayloadHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new PayloadContent(payload, reportLength, afterRead, declaredBytes)
        }));
        _clients.Add(http);
        return new WebDownloadTool(http, new DownloadOptions { MaxSizeBytes = configuredBytes }, settings);
    }

    private sealed class PayloadHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(response());
        }
    }

    private sealed class PayloadContent(byte[] payload, bool reportLength, Action? afterRead, long? declaredBytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = declaredBytes ?? payload.LongLength;
            return reportLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(payload).AsTask();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ReadProbeStream(payload, afterRead));
    }

    private sealed class ReadProbeStream(byte[] payload, Action? afterRead) : MemoryStream(payload, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Read(buffer.Span);
            afterRead?.Invoke();
            return ValueTask.FromResult(read);
        }
    }
}
