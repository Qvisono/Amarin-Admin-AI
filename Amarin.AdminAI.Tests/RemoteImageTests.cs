using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Pictures from the open web. Two things had to be true before any of this worked: the request
/// has to look like a browser (an anonymous GET is answered with 403 by most image CDNs), and a
/// link to a submission page has to resolve to the file the page shows.
/// </summary>
public sealed class RemoteImageTests
{
    /// <summary>A 1x1 transparent PNG.</summary>
    private static readonly byte[] PixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Theory]
    [InlineData("http://127.0.0.1/x.png")]
    [InlineData("http://localhost:8080/x.png")]
    [InlineData("http://10.0.0.5/x.png")]
    [InlineData("http://192.168.1.1/x.png")]
    [InlineData("http://172.20.3.4/x.png")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/x.png")]
    [InlineData("http://nas.local/x.png")]
    [InlineData("http://intranet/x.png")]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://example.com/x.png")]
    public void Addresses_inside_the_machine_or_the_lan_are_refused(string url)
    {
        // The URL is picked by a model, so an unfiltered fetch would be an SSRF primitive aimed
        // at whatever the user's own network happens to be running.
        Assert.False(RemoteImages.IsSafeTarget(url, out _), url + " should not be fetchable");
    }

    [Theory]
    [InlineData("https://d.furaffinity.net/art/someone/1234/1234.file.png")]
    [InlineData("http://example.com/photo.jpg")]
    [InlineData("https://8.8.8.8/photo.jpg")]
    public void Ordinary_public_addresses_are_allowed(string url) =>
        Assert.True(RemoteImages.IsSafeTarget(url, out _), url + " should be fetchable");

    [Fact]
    public void The_picture_a_page_advertises_is_found()
    {
        const string html = """
            <html><head>
              <meta name="twitter:image" content="/small.jpg">
              <meta property="og:image" content="https://d.example.net/art/full.png">
            </head><body>...</body></html>
            """;

        // og:image outranks twitter:image even though it comes second in the document.
        Assert.Equal(
            "https://d.example.net/art/full.png",
            RemoteImages.ResolveImageFromHtml(html, new Uri("https://example.net/view/1/")));
    }

    [Fact]
    public void A_relative_preview_is_resolved_against_the_page()
    {
        const string html = "<meta property='og:image' content='/art/full.png'>";

        Assert.Equal(
            "https://example.net/art/full.png",
            RemoteImages.ResolveImageFromHtml(html, new Uri("https://example.net/view/1/")));
    }

    [Fact]
    public void A_protocol_relative_preview_borrows_the_page_scheme()
    {
        const string html = "<meta property=\"og:image\" content=\"//cdn.example.net/full.png\">";

        Assert.Equal(
            "https://cdn.example.net/full.png",
            RemoteImages.ResolveImageFromHtml(html, new Uri("https://example.net/view/1/")));
    }

    [Fact]
    public void A_page_that_advertises_nothing_resolves_to_nothing() =>
        Assert.Null(RemoteImages.ResolveImageFromHtml(
            "<html><body><img src=\"/inline.png\"></body></html>",
            new Uri("https://example.net/view/1/")));

    // ===== The tool, against a real socket =====

    /// <summary>
    /// The stub lives on loopback, which the real filter refuses — so loopback is waved through
    /// and every other address still goes past the production rules.
    /// </summary>
    private static bool AllowStub(Uri uri) => uri.IsLoopback || RemoteImages.IsSafeTarget(uri);

    private static async Task<ToolResult> Fetch(string url, StubServer server)
    {
        var tool = new FetchImageTool(server.Client, AllowStub);
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { url }));
        return await tool.ExecuteAsync(arguments.RootElement);
    }

    [Fact]
    public async Task A_direct_image_link_comes_back_as_an_attachment()
    {
        using var server = new StubServer(context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.OutputStream.Write(PixelPng);
        });

        var result = await Fetch(server.Url + "cat.png", server);

        Assert.True(result.Success, result.Output);
        var image = Assert.Single(result.GetImages());
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal(Convert.ToBase64String(PixelPng), image.Base64);
    }

    [Fact]
    public async Task The_request_identifies_itself_as_a_browser()
    {
        using var server = new StubServer(context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.OutputStream.Write(PixelPng);
        });

        await Fetch(server.Url + "cat.png", server);

        var request = Assert.Single(server.Requests);
        Assert.Contains("Mozilla/", request.UserAgent ?? "");
        Assert.False(string.IsNullOrEmpty(request.Referer), "no Referer was sent");
    }

    [Fact]
    public async Task A_page_link_is_followed_to_the_picture_it_shows()
    {
        using var server = new StubServer(context =>
        {
            if (context.Request.Url!.AbsolutePath.EndsWith(".png", StringComparison.Ordinal))
            {
                context.Response.ContentType = "image/png";
                context.Response.OutputStream.Write(PixelPng);
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(
                "<html><head><meta property=\"og:image\" content=\"/art/full.png\"></head></html>"));
        });

        var result = await Fetch(server.Url + "view/1/", server);

        Assert.True(result.Success, result.Output);
        Assert.Single(result.GetImages());
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_page_pointing_its_preview_at_the_lan_is_not_followed()
    {
        // The classic SSRF shape: a public page that names an internal address as its picture.
        using var server = new StubServer(context =>
        {
            context.Response.ContentType = "text/html";
            context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(
                "<meta property=\"og:image\" content=\"http://169.254.169.254/latest/meta-data\">"));
        });

        var result = await Fetch(server.Url + "view/1/", server);

        Assert.False(result.Success);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task A_chain_of_pages_is_not_followed()
    {
        // One hop only, so a hostile page cannot walk the fetcher wherever it likes.
        using var server = new StubServer(context =>
        {
            context.Response.ContentType = "text/html";
            context.Response.OutputStream.Write(Encoding.UTF8.GetBytes(
                "<meta property=\"og:image\" content=\"/next/\">"));
        });

        var result = await Fetch(server.Url + "view/1/", server);

        Assert.False(result.Success);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_refusal_says_why_without_telling_the_model_to_give_up()
    {
        using var server = new StubServer(context => context.Response.StatusCode = 403);

        var result = await Fetch(server.Url + "cat.png", server);

        Assert.False(result.Success);
        Assert.Contains("403", result.Output);
        Assert.DoesNotContain("не повторяй", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_oversized_file_is_cut_off_rather_than_swallowed()
    {
        using var server = new StubServer(context =>
        {
            context.Response.ContentType = "image/png";
            context.Response.SendChunked = true;
            var chunk = new byte[64 * 1024];
            for (var sent = 0; sent < RemoteImages.MaxBytes + chunk.Length; sent += chunk.Length)
            {
                context.Response.OutputStream.Write(chunk);
            }
        });

        var result = await Fetch(server.Url + "huge.png", server);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Something_that_is_not_a_picture_is_reported_as_such()
    {
        using var server = new StubServer(context =>
        {
            context.Response.ContentType = "application/zip";
            context.Response.OutputStream.Write(new byte[16]);
        });

        var result = await Fetch(server.Url + "archive.zip", server);

        Assert.False(result.Success);
        Assert.Contains("картинки", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the server saw, copied out before the request object is disposed.</summary>
    private sealed record Seen(string Path, string? UserAgent, string? Referer);

    /// <summary>A one-off loopback HTTP server; the handler answers every request.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();

        public StubServer(Action<HttpListenerContext> handle)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            Client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    // Read eagerly: the request object is torn down with the response, and a
                    // test that looks at it afterwards gets ObjectDisposedException.
                    Requests.Add(new Seen(
                        context.Request.Url!.AbsolutePath,
                        context.Request.UserAgent,
                        context.Request.Headers["Referer"]));

                    try
                    {
                        handle(context);
                    }
                    catch (Exception)
                    {
                        // A cut-off response is a case under test, not a failure of the stub.
                    }

                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        // Already torn down by the client.
                    }
                }
            });
        }

        public string Url { get; }

        public HttpClient Client { get; }

        public System.Collections.Concurrent.ConcurrentBag<Seen> Requests { get; } = [];

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            Client.Dispose();
            _stop.Dispose();
        }
    }
}
