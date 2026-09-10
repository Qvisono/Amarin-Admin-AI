using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Установка обновления поверх себя: программа скачивает исполняемый файл и заменяет им свой,
/// поэтому проверки адреса, размера и контрольной суммы здесь не украшение, а условие работы.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "amarin-update-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.15.0/app.exe")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/x")]
    public void Release_addresses_on_github_are_trusted(string url) =>
        Assert.True(UpdateInstaller.IsTrustedUrl(url));

    [Theory]
    [InlineData("http://github.com/x/y/releases/download/v1/app.exe")]
    [InlineData("https://github.evil.com/x/app.exe")]
    [InlineData("https://example.com/app.exe")]
    [InlineData("file:///C:/app.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? url) =>
        Assert.False(UpdateInstaller.IsTrustedUrl(url));

    [Fact]
    public void A_release_without_a_windows_build_cannot_be_installed()
    {
        var release = Release([new ReleaseAsset("notes.txt", "https://github.com/a/b/releases/download/v1/notes.txt", 10, null)]);

        Assert.False(UpdateInstaller.TryPlan(release, ExeIn("app.exe"), out _, out var error));
        Assert.Contains("сборки для Windows", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_asset_from_a_foreign_host_cannot_be_installed()
    {
        var release = Release([new ReleaseAsset("app-win-x64.exe", "https://example.com/app.exe", 10, null)]);

        Assert.False(UpdateInstaller.TryPlan(release, ExeIn("app.exe"), out _, out var error));
        Assert.Contains("не на GitHub", error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_points_at_the_folder_the_program_runs_from()
    {
        var exe = ExeIn("app.exe");

        Assert.True(UpdateInstaller.TryPlan(Release(), exe, out var plan, out _));

        Assert.Equal(_root, plan.Folder);
        Assert.Equal(Path.Combine(_root, UpdateInstaller.WorkFolderName), plan.WorkDirectory);
        Assert.Equal("Amarin-Admin-AI-v1.15.0-win-x64.exe", plan.Asset.Name);
    }

    [Fact]
    public async Task A_good_download_lands_in_the_work_folder()
    {
        var payload = Encoding.UTF8.GetBytes("свежая сборка");
        var plan = PlanFor(payload, Hash(payload));
        using var http = new HttpClient(new BytesHandler(payload));

        var (result, file) = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file!));
        Assert.Empty(Directory.GetFiles(plan.WorkDirectory, "*.part"));
    }

    [Fact]
    public async Task A_wrong_checksum_throws_the_file_away()
    {
        var payload = Encoding.UTF8.GetBytes("подменённая сборка");
        var plan = PlanFor(payload, Hash(Encoding.UTF8.GetBytes("совсем другое")));
        using var http = new HttpClient(new BytesHandler(payload));

        var (result, file) = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(file);
        Assert.Contains("сумма", result.Error!, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(plan.WorkDirectory));
    }

    [Fact]
    public async Task A_wrong_size_throws_the_file_away()
    {
        var payload = Encoding.UTF8.GetBytes("короткая сборка");
        var plan = PlanFor(payload, Hash(payload), declaredSize: payload.Length + 100);
        using var http = new HttpClient(new BytesHandler(payload));

        var (result, file) = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Null(file);
        Assert.Contains("Размер", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_reaches_the_end()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        var plan = PlanFor(payload, Hash(payload));
        using var http = new HttpClient(new BytesHandler(payload));

        var reported = new List<double>();
        var (result, _) = await UpdateInstaller.DownloadAsync(
            plan,
            http,
            new Progress<double>(reported.Add),
            CancellationToken.None);

        // Progress<T> отдаёт значения через контекст синхронизации; в тесте это пул потоков.
        await Task.Delay(50);

        Assert.True(result.Ok, result.Error);
        Assert.Contains(reported, share => share >= 1);
    }

    [Fact]
    public void Swapping_moves_the_old_program_aside()
    {
        var exe = ExeIn("app.exe", "старая версия");
        var incoming = Path.Combine(_root, "new.exe");
        File.WriteAllText(incoming, "новая версия");

        var result = UpdateInstaller.Swap(incoming, exe);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("новая версия", File.ReadAllText(exe));
        Assert.Equal("старая версия", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(incoming));
    }

    [Fact]
    public void A_failed_swap_puts_the_old_program_back()
    {
        var exe = ExeIn("app.exe", "старая версия");

        // Файла с обновлением нет — подмена обязана откатиться, а не оставить пустое место.
        var result = UpdateInstaller.Swap(Path.Combine(_root, "missing.exe"), exe);

        Assert.False(result.Ok);
        Assert.True(File.Exists(exe));
        Assert.Equal("старая версия", File.ReadAllText(exe));
    }

    [Fact]
    public void Cleanup_removes_the_leftovers_of_the_previous_update()
    {
        var exe = ExeIn("app.exe");
        File.WriteAllText(exe + ".old", "прошлая версия");
        var work = Path.Combine(_root, UpdateInstaller.WorkFolderName);
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "leftover.exe"), "мусор");

        UpdateInstaller.CleanupLeftovers(exe);

        Assert.False(File.Exists(exe + ".old"));
        Assert.False(Directory.Exists(work));
        Assert.True(File.Exists(exe));
    }

    // ── вспомогательное ──────────────────────────────────────────────────────────

    private string ExeIn(string name, string content = "программа")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ReleaseInfo Release(IReadOnlyList<ReleaseAsset>? assets = null) =>
        new(
            "v1.15.0",
            new Version(1, 15, 0),
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v1.15.0",
            "Release 1.15.0",
            null,
            assets ??
            [
                new ReleaseAsset(
                    "Amarin-Admin-AI-v1.15.0-win-x64.exe",
                    "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.15.0/Amarin-Admin-AI-v1.15.0-win-x64.exe",
                    1024,
                    null)
            ]);

    private UpdatePlan PlanFor(byte[] payload, string sha256, long? declaredSize = null)
    {
        var asset = new ReleaseAsset(
            "Amarin-Admin-AI-v1.15.0-win-x64.exe",
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.15.0/app.exe",
            declaredSize ?? payload.Length,
            sha256);

        Assert.True(UpdateInstaller.TryPlan(Release([asset]), ExeIn("app.exe"), out var plan, out var error), error);
        return plan;
    }

    private static string Hash(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class BytesHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
    }
}

public sealed class ReleaseAssetParsingTests
{
    [Fact]
    public void Assets_and_their_checksums_are_read()
    {
        var result = UpdateChecker.ReadRelease(
            """
            {
              "tag_name": "v1.15.0",
              "assets": [
                { "name": "notes.md", "browser_download_url": "https://github.com/a/b/notes.md", "size": 10 },
                {
                  "name": "Amarin-Admin-AI-v1.15.0-win-x64.exe",
                  "browser_download_url": "https://github.com/a/b/app.exe",
                  "size": 77808879,
                  "digest": "sha256:4ff5dd8070e5253dae3844344495fa3196726275232f6878d0244f994baca48f"
                }
              ]
            }
            """,
            new Version(1, 14, 2));

        var build = result.Latest!.WindowsBuild;

        Assert.NotNull(build);
        Assert.Equal(77808879, build!.Size);
        Assert.Equal("4ff5dd8070e5253dae3844344495fa3196726275232f6878d0244f994baca48f", build.Sha256);
    }

    [Fact]
    public void A_release_without_assets_has_no_windows_build()
    {
        var result = UpdateChecker.ReadRelease("""{"tag_name":"v1.15.0"}""", new Version(1, 14, 2));

        Assert.Empty(result.Latest!.Assets);
        Assert.Null(result.Latest.WindowsBuild);
    }

    [Fact]
    public void A_digest_in_an_unknown_shape_is_ignored()
    {
        var result = UpdateChecker.ReadRelease(
            """
            {
              "tag_name": "v1.15.0",
              "assets": [
                { "name": "app.exe", "browser_download_url": "https://github.com/a/b/app.exe", "size": 1, "digest": "md5:abc" }
              ]
            }
            """,
            new Version(1, 0, 0));

        Assert.Null(result.Latest!.WindowsBuild!.Sha256);
    }
}
