using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Обновления (H4): бета-канал, заметки к релизу, возврат к прошлой версии, «Что нового».
/// </summary>
public sealed class ReleaseChannelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-rollback-" + Guid.NewGuid().ToString("N"));

    public ReleaseChannelTests() => Directory.CreateDirectory(_root);

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
    [InlineData("v1.29.0-beta.1", "1.29.0", -1)]
    [InlineData("1.29.0-beta.2", "1.29.0-beta.1", 1)]
    [InlineData("1.29.0-beta.10", "1.29.0-beta.2", 1)]
    [InlineData("1.29.0-alpha", "1.29.0-beta", -1)]
    [InlineData("1.29.0-beta", "1.29.0-beta.1", -1)]
    [InlineData("1.29.0-1", "1.29.0-beta", -1)]
    [InlineData("1.28.0+6b2c8d6", "v1.28.0", 0)]
    [InlineData("1.28.1-beta.1", "1.28.0", 1)]
    [InlineData("1.28", "1.28.0", 0)]
    public void Versions_compare_by_semver(string left, string right, int expected)
    {
        var a = ReleaseVersion.Parse(left)!.Value;
        var b = ReleaseVersion.Parse(right)!.Value;

        Assert.Equal(expected, Math.Sign(a.CompareTo(b)));
        Assert.Equal(expected == 0, a == b);
    }

    [Fact]
    public void Build_metadata_is_dropped_and_the_label_kept()
    {
        var parsed = ReleaseVersion.Parse("1.29.0-beta.1+abcdef")!.Value;

        Assert.Equal("1.29.0-beta.1", parsed.ToString());
        Assert.True(parsed.IsPrerelease);
        Assert.Null(ReleaseVersion.Parse("beta"));
    }

    [Fact]
    public void The_running_version_has_no_commit_hash_and_matches_the_assembly()
    {
        // SDK приписывает к InformationalVersion хеш коммита; показывать его в заголовке окна и
        // сравнивать с тегом релиза — значит никогда не совпасть.
        var version = RuntimeContext.AppVersion;

        Assert.DoesNotContain("+", version, StringComparison.Ordinal);
        Assert.Equal(
            typeof(RuntimeContext).Assembly.GetName().Version!.ToString(3),
            RuntimeContext.AppRelease.Core.ToString(3));
    }

    [Fact]
    public void A_beta_user_is_offered_the_final_build_of_the_same_number()
    {
        // Ради этого сравнение и переведено на полную версию: по одним числам 1.29.0-beta.1 и
        // 1.29.0 равны, и человек на бете финальную не получил бы никогда.
        const string json = """{"tag_name":"v1.29.0","html_url":"https://github.com/x","assets":[]}""";

        var result = UpdateChecker.ReadRelease(json, ReleaseVersion.Parse("1.29.0-beta.1")!.Value);

        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public void Release_notes_and_the_prerelease_flag_are_read()
    {
        const string json = """
            {"tag_name":"v1.29.0-beta.1","prerelease":true,"body":"## Fixes\r\n\r\n- One\r\n","assets":[]}
            """;

        var latest = UpdateChecker.ReadRelease(json, new Version(1, 28, 0)).Latest!;

        Assert.True(latest.Prerelease);
        Assert.Equal("## Fixes\n\n- One", latest.Notes);
        Assert.Equal("1.29.0-beta.1", latest.Release.ToString());
    }

    [Fact]
    public void Empty_notes_are_none_and_long_ones_are_cut_at_a_line()
    {
        Assert.Null(UpdateChecker.TrimNotes("  \r\n "));

        var line = new string('x', 99) + "\n";
        var trimmed = UpdateChecker.TrimNotes(string.Concat(Enumerable.Repeat(line, 400)))!;

        Assert.True(trimmed.Length <= UpdateChecker.NotesLimit + 3);
        Assert.EndsWith("x\n\n…", trimmed, StringComparison.Ordinal);
    }

    private const string ReleaseList = """
        [
          {"tag_name":"v1.27.2","draft":false,"prerelease":false,"assets":[]},
          {"tag_name":"v1.30.0","draft":true,"prerelease":false,"assets":[]},
          {"tag_name":"v1.29.0-beta.2","draft":false,"prerelease":true,"assets":[]},
          {"tag_name":"v1.28.0","draft":false,"prerelease":false,"assets":[]},
          {"tag_name":"not-a-version","draft":false,"prerelease":false,"assets":[]}
        ]
        """;

    [Fact]
    public void The_beta_channel_picks_the_newest_by_version_skipping_drafts()
    {
        // 1.27.2 создан позже и стоит первым, 1.30.0 — черновик: ни тот, ни другой не выбор.
        var result = UpdateChecker.ReadReleases(ReleaseList, new Version(1, 28, 0), includePrerelease: true);

        Assert.True(result.Ok);
        Assert.Equal("1.29.0-beta.2", result.Latest!.Release.ToString());
        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public void Without_prereleases_the_list_gives_the_newest_final_build()
    {
        var result = UpdateChecker.ReadReleases(ReleaseList, new Version(1, 28, 0), includePrerelease: false);

        Assert.Equal("1.28.0", result.Latest!.Release.ToString());
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public void A_list_that_is_not_an_array_is_a_readable_failure()
    {
        Assert.False(UpdateChecker.ReadReleases("{}", new Version(1, 0, 0), true).Ok);
        Assert.False(UpdateChecker.ReadReleases("[", new Version(1, 0, 0), true).Ok);
    }

    [Fact]
    public void A_declined_version_is_not_downloaded_again_but_a_newer_one_is()
    {
        var release = new ReleaseInfo(
            "v1.29.0", new Version(1, 29, 0), "https://github.com/x", null, null,
            [new ReleaseAsset("Amarin-Admin-AI-v1.29.0-win-x64.exe", "https://github.com/x.exe", 10, new string('a', 64))]);

        Assert.False(UpdateSchedule.ShouldAutoDownload(true, release, staged: null, declined: new Version(1, 29, 0)));
        Assert.True(UpdateSchedule.ShouldAutoDownload(true, release, staged: null, declined: new Version(1, 28, 5)));
        Assert.True(UpdateSchedule.ShouldAutoDownload(true, release, staged: null));
    }

    [Fact]
    public void A_staged_beta_does_not_count_as_the_final_build()
    {
        var final = new ReleaseInfo(
            "v1.29.0", new Version(1, 29, 0), "https://github.com/x", null, null,
            [new ReleaseAsset("a-win-x64.exe", "https://github.com/x.exe", 10, new string('a', 64))]);

        Assert.True(UpdateSchedule.ShouldAutoDownload(true, final, staged: ReleaseVersion.Parse("1.29.0-beta.3")));
    }

    [Fact]
    public void After_a_successful_start_the_old_exe_is_kept_as_the_previous_version()
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "new");
        File.WriteAllText(exe + ".old", "old");

        UpdateInstaller.CleanupLeftovers(exe);

        Assert.False(File.Exists(exe + ".old"));
        Assert.Equal("old", File.ReadAllText(exe + UpdateInstaller.PreviousSuffix));
        Assert.Equal(exe + UpdateInstaller.PreviousSuffix, UpdateInstaller.PreviousVersionPath(exe));
    }

    [Fact]
    public void Rolling_back_swaps_the_previous_version_in_and_keeps_the_current_one()
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "new");
        File.WriteAllText(exe + UpdateInstaller.PreviousSuffix, "old");

        var result = UpdateInstaller.RollBack(exe);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("old", File.ReadAllText(exe));

        // Текущая уезжает в .old, а следующий запуск сделает её «прошлой» — вернуться обратно
        // можно тем же путём.
        UpdateInstaller.CleanupLeftovers(exe);
        Assert.Equal("new", File.ReadAllText(exe + UpdateInstaller.PreviousSuffix));
    }

    [Fact]
    public void A_previous_version_left_as_old_is_not_wiped_by_the_swap_itself()
    {
        // Защищённая папка: обычный процесс не смог переименовать .old, и прошлая версия лежит
        // под этим именем. Swap первым делом чистит .old — без переезда он стёр бы источник.
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "new");
        File.WriteAllText(exe + ".old", "old");

        var result = UpdateInstaller.RollBack(exe);

        Assert.True(result.Ok, result.Error);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal("new", File.ReadAllText(exe + ".old"));
        Assert.False(File.Exists(exe + UpdateInstaller.PreviousSuffix));
    }

    [Fact]
    public void Without_a_previous_version_the_rollback_refuses_readably()
    {
        var exe = Path.Combine(_root, "app.exe");
        File.WriteAllText(exe, "new");

        var result = UpdateInstaller.RollBack(exe);

        Assert.False(result.Ok);
        Assert.Equal(Loc.Get("S.Updates.NoPrevious"), result.Error);
        Assert.Equal("new", File.ReadAllText(exe));
    }

    [Theory]
    [InlineData("1.27.1", false)]
    [InlineData("1.28.0", true)]
    [InlineData("1.29.0-beta.1", true)]
    [InlineData(null, false)]
    public void Only_versions_that_know_declined_updates_keep_auto_update_on(string? version, bool expected) =>
        Assert.Equal(expected, UpdateInstaller.KnowsDeclinedUpdate(ReleaseVersion.Parse(version)));

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData("1.27.1", false, true)]
    [InlineData("1.28.0", true, false)]
    [InlineData("1.29.0", true, false)]
    [InlineData("1.28.0-beta.1", true, true)]
    public void Whats_new_is_shown_once_per_newer_version(string? lastSeen, bool usedBefore, bool expected) =>
        Assert.Equal(expected, WhatsNew.ShouldShow(lastSeen, ReleaseVersion.Parse("1.28.0")!.Value, usedBefore));

    [Fact]
    public void The_build_carries_the_notes_of_its_own_version()
    {
        var notes = WhatsNew.Embedded();

        Assert.NotNull(notes);
        Assert.StartsWith("# " + RuntimeContext.AppRelease.Core.ToString(3), notes, StringComparison.Ordinal);
    }

    [Fact]
    public void The_elevated_rollback_is_routed_before_the_single_instance_lock()
    {
        var startup = StartupArgs.Parse(["--rollback-update"]);

        Assert.True(startup.RollbackUpdate);
        Assert.Equal(StartupRoute.ApplyUpdate, StartupRouter.Decide(startup, () => throw new InvalidOperationException("lock asked")));
    }

    [Fact]
    public void Suffixed_versions_are_released_as_prereleases()
    {
        var text = File.ReadAllText(FindUp(".github/workflows/release.yml"));

        Assert.Contains("--prerelease", text, StringComparison.Ordinal);
        Assert.Contains("--notes-file $env:NOTES @pre", text, StringComparison.Ordinal);
    }

    private static string FindUp(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
