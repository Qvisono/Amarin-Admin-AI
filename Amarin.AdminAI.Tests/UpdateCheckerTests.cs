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
}
