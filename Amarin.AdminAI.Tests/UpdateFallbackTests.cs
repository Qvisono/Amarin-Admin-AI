using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Обновление, когда API GitHub не отвечает: лимит анонимных запросов (шестьдесят в час на адрес)
/// за VPN и мобильным NAT выбирают чужие запросы. До 1.28.5 запасной путь находил версию без
/// файлов — кнопки «Обновить» не было, и автообновление не качало ничего.
/// </summary>
public sealed class UpdateFallbackTests
{
    private const string Repo = "https://github.com/Qvisono/Amarin-Admin-AI";
    private const string BuildName = "Amarin-Admin-AI-v1.28.5-win-x64.exe";
    private static readonly string Hash = new('e', 64);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_refused_api_still_gives_a_build_that_installs_and_downloads_by_itself(HttpStatusCode refusal)
    {
        var handler = new GitHub(api: _ => Answer(refusal, "{}"));
        using var http = new HttpClient(handler);

        var result = await UpdateChecker.CheckAsync(http, new ReleaseVersion(new Version(1, 28, 0), ""), beta: false);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("1.28.5", result.Latest!.Release.ToString());
        var build = result.Latest.WindowsBuild;
        Assert.NotNull(build);
        Assert.Equal(BuildName, build.Name);
        Assert.Equal(Repo + "/releases/download/v1.28.5/" + BuildName, build.Url);
        Assert.Equal(Hash, build.Sha256);
        Assert.True(UpdateInstaller.IsTrustedUrl(build.Url));
        Assert.True(UpdateSchedule.ShouldAutoDownload(autoUpdate: true, result.Latest, staged: null));
    }

    [Fact]
    public async Task An_unreachable_api_falls_back_to_the_release_page_too()
    {
        // Раньше запасной путь включался только на 403 и 429, а обрыв связи с api.github.com при
        // живом github.com оставлял человека вовсе без обновления.
        var handler = new GitHub(api: _ => throw new HttpRequestException("api.github.com is unreachable"));
        using var http = new HttpClient(handler);

        var result = await UpdateChecker.CheckAsync(http, new ReleaseVersion(new Version(1, 28, 0), ""), beta: false);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Hash, result.Latest!.WindowsBuild!.Sha256);
    }

    [Fact]
    public async Task Without_a_sums_file_the_version_is_still_reported_but_has_no_build()
    {
        var handler = new GitHub(api: _ => Answer(HttpStatusCode.Forbidden, "{}"), sums: () => Answer(HttpStatusCode.NotFound, "Not Found"));
        using var http = new HttpClient(handler);

        var result = await UpdateChecker.CheckAsync(http, new ReleaseVersion(new Version(1, 28, 0), ""), beta: false);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.UpdateAvailable);
        Assert.Null(result.Latest!.WindowsBuild);
    }

    [Fact]
    public async Task The_same_version_on_the_release_page_asks_for_no_sums_file()
    {
        var handler = new GitHub(api: _ => Answer(HttpStatusCode.Forbidden, "{}"));
        using var http = new HttpClient(handler);

        var result = await UpdateChecker.CheckAsync(http, new ReleaseVersion(new Version(1, 28, 5), ""), beta: false);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.UpdateAvailable);
        Assert.Equal(0, handler.SumsRequests);
    }

    [Fact]
    public async Task When_github_is_unreachable_altogether_the_api_error_is_what_the_person_reads()
    {
        var handler = new GitHub(
            api: _ => Answer(HttpStatusCode.ServiceUnavailable, "{}"),
            page: () => throw new HttpRequestException("github.com is unreachable"));
        using var http = new HttpClient(handler);

        var result = await UpdateChecker.CheckAsync(http, new ReleaseVersion(new Version(1, 28, 0), ""), beta: false);

        Assert.False(result.Ok);
        Assert.Equal(Loc.Format("S.Updates.Status", 503), result.Error);
    }

    [Theory]
    [InlineData("{0}  " + BuildName + "\n")]
    [InlineData("{0}  notes.txt\n{0}  Amarin-Admin-AI-v1.28.5-win-arm64.exe\n{0}  " + BuildName + "\n")]
    public void The_build_is_named_by_the_sums_file_itself(string template)
    {
        var build = UpdateChecker.ReadBuildFromSums(string.Format(template, Hash), "v1.28.5");

        Assert.NotNull(build);
        Assert.Equal(BuildName, build.Name);
        Assert.Equal(Hash, build.Sha256);
        Assert.Equal(0, build.Size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eeee  " + BuildName)]
    [InlineData("{0}  notes.txt")]
    [InlineData("{0}  ../evil/" + BuildName)]
    public void A_sums_file_without_a_windows_build_gives_none(string template) =>
        Assert.Null(UpdateChecker.ReadBuildFromSums(string.Format(template, Hash), "v1.28.5"));

    [Fact]
    public void A_richer_answer_about_the_same_release_wins_and_a_new_version_always_does()
    {
        var full = Release("v1.28.5", new ReleaseAsset(BuildName, Repo + "/releases/download/v1.28.5/" + BuildName, 10, Hash));
        var bare = Release("v1.28.5");
        var newer = Release("v1.28.6");

        Assert.Same(full, UpdateChecker.Richer(full, bare));
        Assert.Same(full, UpdateChecker.Richer(bare, full));
        Assert.Same(newer, UpdateChecker.Richer(full, newer));
        Assert.Same(bare, UpdateChecker.Richer(null, bare));
    }

    private static ReleaseInfo Release(string tag, params ReleaseAsset[] assets) =>
        new(tag, UpdateChecker.ParseTag(tag)!, Repo + "/releases/tag/" + tag, null, null, assets);

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8) };

    /// <summary>
    /// GitHub в миниатюре: API, страница последнего выпуска (уже «после редиректа» на тег) и файл
    /// сумм по прямой ссылке.
    /// </summary>
    private sealed class GitHub(
        Func<HttpRequestMessage, HttpResponseMessage> api,
        Func<HttpResponseMessage>? page = null,
        Func<HttpResponseMessage>? sums = null) : HttpMessageHandler
    {
        public int SumsRequests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            HttpResponseMessage response;
            if (url.StartsWith("https://api.github.com/", StringComparison.Ordinal))
            {
                response = api(request);
            }
            else if (url == UpdateChecker.ReleasesPageUrl)
            {
                response = page?.Invoke() ?? Answer(HttpStatusCode.OK, "<html></html>");

                // Настоящий клиент идёт по редиректу сам и оставляет в ответе конечный адрес.
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, Repo + "/releases/tag/v1.28.5");
                return Task.FromResult(response);
            }
            else if (url == Repo + "/releases/download/v1.28.5/SHA256SUMS")
            {
                Interlocked.Increment(ref SumsRequests);
                response = sums?.Invoke() ?? Answer(HttpStatusCode.OK, $"{Hash}  {BuildName}\n");
            }
            else
            {
                response = Answer(HttpStatusCode.NotFound, "");
            }

            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
