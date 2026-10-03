using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Обновление ставится только сверенным: сумма из SHA256SUMS, если GitHub её не прислал,
/// ещё одна сверка прямо перед подменой и правило подписи.
/// </summary>
public sealed class UpdateVerificationTests : IDisposable
{
    private const string BuildName = "Amarin-Admin-AI-v1.28.0-win-x64.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-verify-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdateVerificationTests() => Directory.CreateDirectory(_root);

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
    [InlineData("{0}  " + BuildName)]
    [InlineData("{0} *" + BuildName)]
    [InlineData("deadbeef  other.exe\n{0}  " + BuildName + "\n")]
    [InlineData("{0}\t" + BuildName + "\r\n")]
    public void The_checksum_is_read_from_a_sha256sum_file(string template)
    {
        var hash = new string('c', 64);

        Assert.Equal(hash, UpdateChecker.ReadSha256Sums(string.Format(template, hash), BuildName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a sum")]
    [InlineData("cccc  " + BuildName)]
    public void A_sums_file_without_our_build_gives_no_checksum(string text) =>
        Assert.Null(UpdateChecker.ReadSha256Sums(text, BuildName));

    [Fact]
    public async Task Without_a_digest_the_checksum_comes_from_the_release_sums_file()
    {
        var hash = new string('d', 64);
        var handler = new RoutingHandler(request => request.RequestUri!.AbsoluteUri.EndsWith("SHA256SUMS", StringComparison.Ordinal)
            ? $"{hash}  {BuildName}\n"
            : $$"""
               {
                 "tag_name": "v1.28.0",
                 "assets": [
                   { "name": "{{BuildName}}", "browser_download_url": "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.28.0/{{BuildName}}", "size": 10 },
                   { "name": "SHA256SUMS", "browser_download_url": "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.28.0/SHA256SUMS", "size": 90 }
                 ]
               }
               """);
        using var http = new HttpClient(handler);

        var result = await UpdateChecker.CheckAsync(http, new Version(1, 27, 0));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(hash, result.Latest!.WindowsBuild!.Sha256);
    }

    [Fact]
    public async Task A_build_without_a_checksum_is_not_even_downloaded_without_consent()
    {
        var payload = Encoding.UTF8.GetBytes("новая версия");
        var handler = new RoutingHandler(_ => Encoding.UTF8.GetString(payload));
        using var http = new HttpClient(handler);
        var plan = Plan(payload.Length, sha256: null);

        var refused = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);
        var consented = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None, allowUnverified: true);

        Assert.False(refused.Result.Ok);
        Assert.Equal(1, handler.Calls);
        Assert.True(consented.Result.Ok, consented.Result.Error);
    }

    [Fact]
    public void A_file_changed_after_download_is_not_swapped_in()
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "старая версия");
        var downloaded = Path.Combine(_root, "new.exe");
        var original = Encoding.UTF8.GetBytes("новая версия");
        File.WriteAllBytes(downloaded, original);
        var expected = Convert.ToHexString(SHA256.HashData(original));

        // Файл полежал во временной папке и успел поменяться.
        File.WriteAllText(downloaded, "подменённая версия");
        var swap = UpdateInstaller.Swap(downloaded, exe, expected);

        Assert.False(swap.Ok);
        Assert.Equal("старая версия", File.ReadAllText(exe));
    }

    [Fact]
    public void The_elevated_swap_checks_the_sum_it_was_handed()
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "старая версия");
        var work = Directory.CreateDirectory(Path.Combine(_root, UpdateInstaller.WorkFolderName)).FullName;
        var downloaded = Path.Combine(work, "new.exe");
        File.WriteAllText(downloaded, "подменённая версия");

        var swap = UpdateInstaller.ApplyElevated(downloaded, exe, new string('e', 64));

        Assert.False(swap.Ok);
        Assert.Equal("старая версия", File.ReadAllText(exe));
    }

    [Fact]
    public void The_sum_is_passed_to_the_elevated_process()
    {
        var hash = new string('f', 64);

        var args = StartupArgs.Parse(["--apply-update", "C:\\x\\new.exe", "--sha256", hash]);
        var bogus = StartupArgs.Parse(["--apply-update", "C:\\x\\new.exe", "--sha256", "nothex"]);

        Assert.Equal(hash, args.ApplyUpdateSha256);
        Assert.Null(bogus.ApplyUpdateSha256);
    }

    [Theory]
    [InlineData(false, false, null, false, false, null, true)]   // не подписан — решает сумма
    [InlineData(true, true, "CN=Amarin", true, true, "CN=Amarin", true)]
    [InlineData(true, true, "CN=Amarin", false, false, null, false)]
    [InlineData(true, true, "CN=Amarin", true, false, "CN=Amarin", false)]
    [InlineData(true, true, "CN=Amarin", true, true, "CN=Someone Else", false)]
    public void A_signed_program_is_only_replaced_by_the_same_publisher(
        bool currentSigned, bool currentValid, string? currentPublisher,
        bool newSigned, bool newValid, string? newPublisher,
        bool allowed)
    {
        var refusal = AuthenticodeCheck.Refusal(
            new SignatureInfo(currentSigned, currentValid, currentPublisher),
            new SignatureInfo(newSigned, newValid, newPublisher));

        Assert.Equal(allowed, refusal is null);
    }

    [Fact]
    public void A_signed_program_is_not_swapped_for_an_unsigned_build_even_with_the_right_sum()
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "подписанная версия");
        var downloaded = Path.Combine(_root, "new.exe");
        var payload = Encoding.UTF8.GetBytes("неподписанная версия");
        File.WriteAllBytes(downloaded, payload);

        var swap = UpdateInstaller.Swap(
            downloaded,
            exe,
            Convert.ToHexString(SHA256.HashData(payload)),
            inspect: path => path == exe ? new SignatureInfo(true, true, "CN=Amarin") : SignatureInfo.None);

        Assert.False(swap.Ok);
        Assert.Equal("подписанная версия", File.ReadAllText(exe));
    }

    private UpdatePlan Plan(long size, string? sha256)
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "старая версия");
        var release = new ReleaseInfo(
            "v1.28.0",
            new Version(1, 28, 0),
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v1.28.0",
            null,
            null,
            [new ReleaseAsset(BuildName, "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v1.28.0/" + BuildName, size, sha256)]);
        Assert.True(UpdateInstaller.TryPlan(release, exe, out var plan, out var error), error);
        return plan;
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(request), Encoding.UTF8),
                RequestMessage = request
            });
        }
    }
}
