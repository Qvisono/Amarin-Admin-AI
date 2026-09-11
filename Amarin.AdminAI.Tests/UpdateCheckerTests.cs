using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class UpdateCheckerTests
{
    [Theory]
    [InlineData("v1.14.2", "1.14.2")]
    [InlineData("1.14.2", "1.14.2")]
    [InlineData("V2.0", "2.0.0")]
    [InlineData("v1.15.0-beta.2", "1.15.0")]
    [InlineData("v3", "3.0.0")]
    public void Tags_turn_into_three_part_versions(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), UpdateChecker.ParseTag(tag));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData(null)]
    public void Unusable_tags_are_rejected(string? tag) =>
        Assert.Null(UpdateChecker.ParseTag(tag));

    [Fact]
    public void A_newer_release_is_reported_as_an_update()
    {
        var result = UpdateChecker.ReadRelease(
            """
            {
              "tag_name": "v1.15.0",
              "html_url": "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v1.15.0",
              "name": "Release 1.15.0",
              "published_at": "2026-01-05T10:00:00Z"
            }
            """,
            new Version(1, 14, 2));

        Assert.True(result.Ok);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 15, 0), result.Latest!.Version);
        Assert.EndsWith("v1.15.0", result.Latest.PageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_version_is_not_an_update()
    {
        var result = UpdateChecker.ReadRelease("""{"tag_name":"v1.14.2"}""", new Version(1, 14, 2));

        Assert.True(result.Ok);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public void An_older_release_is_not_an_update()
    {
        var result = UpdateChecker.ReadRelease("""{"tag_name":"v1.13.0"}""", new Version(1, 14, 2));

        Assert.True(result.Ok);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public void A_four_part_assembly_version_compares_by_its_first_three()
    {
        // Сборка версионируется как 1.14.2.0 — иначе она вечно «отстаёт» от тега v1.14.2.
        var result = UpdateChecker.ReadRelease("""{"tag_name":"v1.14.2"}""", new Version(1, 14, 2, 0));

        Assert.False(result.UpdateAvailable);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"message":"Not Found"}""")]
    public void A_broken_answer_becomes_an_error_not_an_exception(string json)
    {
        var result = UpdateChecker.ReadRelease(json, new Version(1, 0, 0));

        Assert.False(result.Ok);
        Assert.False(result.UpdateAvailable);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task A_client_carrying_someone_elses_key_is_refused()
    {
        // Клиент Venice подмешивает свой Bearer в каждый запрос: GitHub отвечал на него 401,
        // а ключ уезжал на посторонний сервер. Проверка обновлений ходит только своим клиентом.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "venice-key");

        var result = await UpdateChecker.CheckAsync(http, new Version(1, 14, 2));

        Assert.False(result.Ok);
        Assert.Null(result.Latest);
    }

    [Fact]
    public void Auto_check_is_on_by_default_and_remembers_nothing_yet()
    {
        var settings = new AppSettings();

        Assert.True(settings.AutoCheckUpdates);
        Assert.Null(settings.LastUpdateCheckUtc);
    }

    private static HttpResponseMessage Refusal(string? remaining, string? reset)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
        if (remaining is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", remaining);
        }

        if (reset is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ratelimit-reset", reset);
        }

        return response;
    }

    [Fact]
    public void An_exhausted_limit_is_read_out_of_the_headers()
    {
        // 403 от api.github.com — это почти всегда исчерпанный лимит анонимных запросов
        // (60 в час на адрес), и за общим NAT его выбирают чужие. «GitHub ответил 403»
        // человеку в этой ситуации не говорит ничего.
        var moment = DateTimeOffset.UtcNow.AddMinutes(37);
        using var response = Refusal("0", moment.ToUnixTimeSeconds().ToString());

        Assert.True(UpdateChecker.TryReadRateLimitReset(response.Headers, out var reset));
        Assert.Equal(moment.ToUnixTimeSeconds(), reset.ToUnixTimeSeconds());
        Assert.NotEqual(
            UpdateChecker.DescribeRefusal(response.Headers),
            Amarin.Core.Loc.Get("S.Updates.Forbidden"));
    }

    [Theory]
    [InlineData("7", "1893456000")]   // лимит ещё есть — значит, отказ не из-за него
    [InlineData("0", null)]           // ноль без времени: сказать, когда пробовать, нечего
    [InlineData(null, "1893456000")]
    [InlineData("0", "мусор")]
    public void A_forbidden_without_a_real_limit_does_not_invent_a_time(string? remaining, string? reset)
    {
        using var response = Refusal(remaining, reset);

        Assert.False(UpdateChecker.TryReadRateLimitReset(response.Headers, out _));
        Assert.Equal(Amarin.Core.Loc.Get("S.Updates.Forbidden"), UpdateChecker.DescribeRefusal(response.Headers));
    }

    [Fact]
    public void The_version_can_still_be_read_off_the_release_page_redirect()
    {
        // Запасной путь для упёршегося в лимит: /releases/latest перенаправляет на тег, и эта
        // страница лимитом API не считается.
        var result = UpdateChecker.ReadTaggedUrl(
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v1.17.0",
            new Version(1, 16, 2));

        Assert.NotNull(result);
        Assert.True(result!.Ok);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 17, 0), result.Latest!.Version);

        // Ссылок на файлы сборки на странице нет — значит, кнопка «Обновить» не появится,
        // а «Открыть релиз» появится. Пустой список здесь и есть этот признак.
        Assert.Empty(result.Latest.Assets);
        Assert.Null(result.Latest.WindowsBuild);
    }

    [Theory]
    [InlineData("https://github.com/Qvisono/Amarin-Admin-AI/releases")]
    [InlineData("https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/latest")]
    [InlineData("")]
    [InlineData(null)]
    public void An_address_without_a_tag_gives_nothing(string? url) =>
        Assert.Null(UpdateChecker.ReadTaggedUrl(url, new Version(1, 16, 2)));

    [Fact]
    public void The_same_version_on_the_release_page_is_not_an_update()
    {
        var result = UpdateChecker.ReadTaggedUrl(
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v1.16.2?a=b",
            new Version(1, 16, 2));

        Assert.NotNull(result);
        Assert.False(result!.UpdateAvailable);
    }
}
