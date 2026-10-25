using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Amarin.Core;
using Amarin.UI;
using Xunit.Abstractions;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Заплатка обновления (1.37.0): новый exe собирается из установленного и кусков из заплатки, а
/// ставится только сошедшийся по сумме самого exe. Любая неудача заплатки — полная загрузка.
/// </summary>
public sealed class UpdatePatchTests(ITestOutputHelper output) : IDisposable
{
    private const string ExeUrl = "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v9.0.0/Amarin-Admin-AI-v9.0.0-win-x64.exe";
    private const string PatchUrl = "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v9.0.0/Amarin-Admin-AI-v9.0.0-from-v1.0.0-win-x64.patch";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-patch-" + Guid.NewGuid().ToString("N")[..8]);

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

    // ── формат ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_patch_rebuilds_the_target_and_carries_only_what_changed()
    {
        // Как у бандла: одинаковые файлы сдвинуты, между ними вставлен и выброшен новый кусок.
        var (source, target) = Versions();

        var patch = Make(source, target);

        Assert.Equal(target, Rebuild(source, patch));
        Assert.True(patch.Length < target.Length / 4, $"patch {patch.Length} of {target.Length}");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(100, 100)]
    [InlineData(UpdatePatch.BlockSize * 3, UpdatePatch.BlockSize - 1)]
    public void Small_and_empty_files_are_rebuilt_too(int sourceSize, int targetSize)
    {
        var source = Random(sourceSize, 1);
        var target = Random(targetSize, 2);

        Assert.Equal(target, Rebuild(source, Make(source, target)));
    }

    [Fact]
    public void The_same_file_becomes_a_tiny_patch()
    {
        var file = Random(UpdatePatch.BlockSize * 20 + 17, 3);

        var patch = Make(file, file);

        Assert.Equal(file, Rebuild(file, patch));
        Assert.True(patch.Length < 64);
    }

    [Fact]
    public void A_patch_for_another_file_is_refused()
    {
        var (source, target) = Versions();
        var patch = Make(source, target);

        Assert.Throws<InvalidDataException>(() => Rebuild(source[..^1], patch));
    }

    [Fact]
    public void A_damaged_patch_is_refused_rather_than_trusted()
    {
        var (source, target) = Versions();
        var patch = Make(source, target);

        Assert.Throws<InvalidDataException>(() => Rebuild(source, patch[..^10]));
        Assert.Throws<InvalidDataException>(() => Rebuild(source, [.. "NOTAPTCH"u8, .. patch[8..]]));

        // Копия за краем исходного файла. У заплатки к тому же файлу первое действие после
        // заголовка (24 байта) — копирование, за ним смещение.
        var outOfRange = Make(source, source);
        Assert.Equal(1, outOfRange[24]);
        BitConverter.GetBytes((long)source.Length).CopyTo(outOfRange, 25);
        Assert.Throws<InvalidDataException>(() => Rebuild(source, outOfRange));
    }

    [Fact]
    public void A_patch_making_a_file_larger_than_allowed_is_refused()
    {
        var (source, target) = Versions();
        var patch = Make(source, target);

        using var sourceStream = new MemoryStream(source);
        using var patchStream = new MemoryStream(patch);
        Assert.Throws<InvalidDataException>(() =>
            UpdatePatch.Apply(sourceStream, patchStream, Stream.Null, target.Length - 1, CancellationToken.None));
    }

    [Fact]
    public void The_release_command_makes_a_checked_patch_file()
    {
        Directory.CreateDirectory(_root);
        var (source, target) = Versions();
        var basePath = Write("base.exe", source);
        var targetPath = Write("target.exe", target);
        var patchPath = Path.Combine(_root, "out.patch");

        Assert.Null(UpdatePatch.MakeFile(basePath, targetPath, patchPath));

        Assert.Equal(target, Rebuild(source, File.ReadAllBytes(patchPath)));
        Assert.False(File.Exists(patchPath + ".part"));
    }

    [Fact]
    public void The_release_command_is_parsed_and_routed_before_the_single_instance_lock()
    {
        var startup = StartupArgs.Parse(["--make-update-patch", @"D:\a\_temp\base.exe", @"release\x.patch"]);

        Assert.Equal(@"D:\a\_temp\base.exe", startup.MakePatchBase);
        Assert.Equal(@"release\x.patch", startup.MakePatchOutput);
        Assert.Equal(StartupRoute.MakeUpdatePatch, StartupRouter.Decide(startup, () => throw new InvalidOperationException("lock")));

        var incomplete = StartupArgs.Parse(["--make-update-patch", "base.exe"]);
        Assert.Null(incomplete.MakePatchBase);
        Assert.Equal(StartupRoute.Run, StartupRouter.Decide(incomplete, () => true));
    }

    // ── выбор заплатки ──────────────────────────────────────────────────────────

    [Fact]
    public void The_patch_is_picked_by_the_installed_version_only()
    {
        var release = Release(
            Asset("Amarin-Admin-AI-v9.0.0-from-v1.0.0-beta.2-win-x64.patch", 10),
            Asset("Amarin-Admin-AI-v9.0.0-from-v1.0.0-win-x64.patch", 20),
            Asset("Amarin-Admin-AI-v9.0.0-from-v1.0.1-win-x64.PATCH", 30),
            new ReleaseAsset("Amarin-Admin-AI-v9.0.0-from-v2.0.0-win-x64.patch", "https://example.com/x.patch", 40, null));

        Assert.Equal(20, release.PatchFrom(ReleaseVersion.Parse("1.0.0")!.Value)?.Size);
        Assert.Equal(10, release.PatchFrom(ReleaseVersion.Parse("1.0.0-beta.2")!.Value)?.Size);
        Assert.Equal(30, release.PatchFrom(ReleaseVersion.Parse("1.0.1")!.Value)?.Size);
        Assert.Null(release.PatchFrom(ReleaseVersion.Parse("1.0.0-beta.1")!.Value));
        Assert.Null(release.PatchFrom(ReleaseVersion.Parse("2.0.0")!.Value));
    }

    [Fact]
    public void The_plan_takes_the_patch_for_the_file_on_disk_and_only_with_a_checksum()
    {
        // Версия берётся из ресурса версии самого файла — подойдёт любая сборка с ним.
        Directory.CreateDirectory(_root);
        var exe = Path.Combine(_root, "app.exe");
        File.Copy(typeof(UpdatePatch).Assembly.Location, exe);
        var installed = UpdateInstaller.FileVersionOf(exe);
        Assert.NotNull(installed);

        var patchName = $"Amarin-Admin-AI-v9.0.0-from-v{installed}-win-x64.patch";
        var verified = Release(Asset("Amarin-Admin-AI-v9.0.0-win-x64.exe", 100_000, new string('a', 64)), Asset(patchName, 1234));
        var unverified = Release(Asset("Amarin-Admin-AI-v9.0.0-win-x64.exe", 100_000), Asset(patchName, 1234));

        Assert.True(UpdateInstaller.TryPlan(verified, exe, out var plan, out var error), error);
        Assert.Equal(patchName, plan.Patch?.Name);
        Assert.Equal(1234, plan.DownloadSize);

        Assert.True(UpdateInstaller.TryPlan(unverified, exe, out var bare, out error), error);
        Assert.Null(bare.Patch);
        Assert.Equal(100_000, bare.DownloadSize);
    }

    // ── загрузка ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_update_with_a_patch_downloads_only_the_patch()
    {
        var (source, target) = Versions();
        var server = new Server(target, Make(source, target));
        var plan = PlanFor(source, target, server.Patch.Length);
        using var http = new HttpClient(server);

        var (result, file) = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(target, await File.ReadAllBytesAsync(file!));
        Assert.Equal([PatchUrl], server.Requested);
        Assert.Equal([Path.GetFileName(file!)], Directory.GetFiles(plan.WorkDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_broken_patch_falls_back_to_the_whole_file()
    {
        var (source, target) = Versions();
        var patch = Make(source, target);
        patch[^5] ^= 0xFF;
        var server = new Server(target, patch);
        var plan = PlanFor(source, target, patch.Length);
        using var http = new HttpClient(server);

        var (result, file) = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(target, await File.ReadAllBytesAsync(file!));
        Assert.Equal([PatchUrl, ExeUrl], server.Requested);
        Assert.Single(Directory.GetFiles(plan.WorkDirectory));
    }

    [Fact]
    public async Task A_patch_to_another_build_falls_back_to_the_whole_file()
    {
        // Exe на диске не тот, к которому делалась заплатка: собран не в CI или подписан иначе.
        var (source, target) = Versions();
        var other = (byte[])source.Clone();
        other[source.Length / 2] ^= 0xFF;
        var server = new Server(target, Make(source, target));
        var plan = PlanFor(other, target, server.Patch.Length);
        using var http = new HttpClient(server);

        var (result, file) = await UpdateInstaller.DownloadAsync(plan, http, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(target, await File.ReadAllBytesAsync(file!));
        Assert.Equal([PatchUrl, ExeUrl], server.Requested);
    }

    [Fact]
    public async Task A_missing_patch_falls_back_to_the_whole_file()
    {
        var (source, target) = Versions();
        var server = new Server(target, patch: null);
        var plan = PlanFor(source, target, 1000);
        using var http = new HttpClient(server);

        var (result, file) = await UpdateInstaller.DownloadAsync(
            plan, http, null, CancellationToken.None, policy: DownloadPolicy.Default with { MaxRestarts = 0 });

        Assert.True(result.Ok, result.Error);
        Assert.Equal(target, await File.ReadAllBytesAsync(file!));
        Assert.Contains(ExeUrl, server.Requested);
    }

    [Fact]
    public async Task Cancelling_during_the_patch_cancels_the_update_and_leaves_nothing()
    {
        var (source, target) = Versions();
        using var cancel = new CancellationTokenSource();
        var server = new Server(target, Make(source, target)) { OnPatch = cancel.Cancel };
        var plan = PlanFor(source, target, server.Patch.Length);
        using var http = new HttpClient(server);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateInstaller.DownloadAsync(plan, http, null, cancel.Token));

        Assert.DoesNotContain(ExeUrl, server.Requested);
        Assert.Empty(Directory.GetFiles(plan.WorkDirectory));
    }

    // ── winget ──────────────────────────────────────────────────────────────────

    [Fact]
    public void An_exe_started_through_a_link_is_updated_where_it_really_lies()
    {
        // winget запускает портативный пакет ссылкой из папки Links; подмена по пути ссылки
        // переставила бы ссылку. Ссылку без прав администратора или режима разработчика Windows не
        // создаёт — тогда проверяется только, что обычный путь не меняется.
        Directory.CreateDirectory(_root);
        var exe = Write("app.exe", [1, 2, 3]);
        Assert.Equal(exe, UpdateInstaller.RealPath(exe));
        Assert.Null(UpdateInstaller.RealPath(null));

        var link = Path.Combine(_root, "links", "amarin-admin-ai.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            File.CreateSymbolicLink(link, exe);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(exe, UpdateInstaller.RealPath(link));
    }

    // ── живой замер ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Замер на настоящих выпусках: <c>AMARIN_PATCH_LIVE_BASE</c> и <c>AMARIN_PATCH_LIVE_TARGET</c> —
    /// пути к двум exe. Пишет размер заплатки и время; обычный прогон сразу выходит.
    /// </summary>
    [Fact]
    public void Live_patch_between_two_real_builds()
    {
        if (Environment.GetEnvironmentVariable("AMARIN_PATCH_LIVE_BASE") is not { Length: > 0 } basePath ||
            Environment.GetEnvironmentVariable("AMARIN_PATCH_LIVE_TARGET") is not { Length: > 0 } targetPath)
        {
            return;
        }

        Directory.CreateDirectory(_root);
        var patchPath = Path.Combine(_root, "live.patch");
        var clock = Stopwatch.StartNew();
        Assert.Null(UpdatePatch.MakeFile(basePath, targetPath, patchPath));
        var made = clock.Elapsed;

        clock.Restart();
        using (var source = File.OpenRead(basePath))
        using (var patch = File.OpenRead(patchPath))
        {
            var hash = UpdatePatch.Apply(source, patch, Stream.Null, long.MaxValue, CancellationToken.None);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(targetPath))).ToLowerInvariant(), hash);
        }

        output.WriteLine(
            $"patch {new FileInfo(patchPath).Length / 1048576.0:F1} MB of {new FileInfo(targetPath).Length / 1048576.0:F1} MB; " +
            $"made and checked in {made.TotalSeconds:F1} s, applied in {clock.Elapsed.TotalSeconds:F1} s");
    }

    // ── вспомогательное ─────────────────────────────────────────────────────────

    /// <summary>Две «версии»: общие куски сдвинуты, один выброшен, один новый.</summary>
    private static (byte[] Source, byte[] Target) Versions()
    {
        var a = Random(300_000, 10);
        var b = Random(200_000, 11);
        var c = Random(150_000, 12);
        var gone = Random(40_000, 13);
        var fresh = Random(30_000, 14);
        return ([.. a, .. gone, .. b, .. c], [.. Random(123, 15), .. a, .. b, .. fresh, .. c]);
    }

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Make(byte[] source, byte[] target)
    {
        using var stream = new MemoryStream();
        UpdatePatch.Create(source, target, stream);
        return stream.ToArray();
    }

    private static byte[] Rebuild(byte[] source, byte[] patch)
    {
        using var sourceStream = new MemoryStream(source);
        using var patchStream = new MemoryStream(patch);
        using var output = new MemoryStream();
        var hash = UpdatePatch.Apply(sourceStream, patchStream, output, long.MaxValue, CancellationToken.None);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(output.ToArray())).ToLowerInvariant(), hash);
        return output.ToArray();
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private UpdatePlan PlanFor(byte[] installed, byte[] target, long patchSize)
    {
        Directory.CreateDirectory(_root);
        var exe = Write("app.exe", installed);
        var asset = new ReleaseAsset(
            "Amarin-Admin-AI-v9.0.0-win-x64.exe",
            ExeUrl,
            target.Length,
            Convert.ToHexString(SHA256.HashData(target)).ToLowerInvariant());
        var patch = new ReleaseAsset("Amarin-Admin-AI-v9.0.0-from-v1.0.0-win-x64.patch", PatchUrl, patchSize, null);
        return new UpdatePlan(asset, exe, Path.Combine(_root, UpdateInstaller.WorkFolderName), Patch: patch);
    }

    private static ReleaseInfo Release(params ReleaseAsset[] assets) =>
        new("v9.0.0", new Version(9, 0, 0), "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v9.0.0", null, null, assets);

    private static ReleaseAsset Asset(string name, long size, string? sha256 = null) =>
        new(name, "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v9.0.0/" + name, size, sha256);

    /// <summary>Релиз GitHub: exe по своей ссылке, заплатка по своей (null — 404).</summary>
    private sealed class Server(byte[] exe, byte[]? patch) : HttpMessageHandler
    {
        private readonly List<string> _requested = [];

        public byte[] Patch => patch ?? [];

        public Action? OnPatch { get; init; }

        public IReadOnlyList<string> Requested
        {
            get
            {
                lock (_requested)
                {
                    return [.. _requested];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_requested)
            {
                _requested.Add(url);
            }

            if (url == PatchUrl)
            {
                OnPatch?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(patch is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(patch) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(exe) });
        }
    }
}
