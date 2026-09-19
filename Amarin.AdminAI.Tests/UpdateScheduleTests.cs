using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Расписание автообновления. До него проверка случалась ровно один раз за запуск и только если
/// с прошлой прошло шесть часов: программа, открытая сутками, о новой версии не узнавала вовсе,
/// а один обрыв связи откладывал следующую попытку на полный интервал.
/// </summary>
public sealed class UpdateScheduleTests
{
    private static readonly DateTime Now = new(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_interval_is_five_hours_and_the_checker_agrees()
    {
        // Срок назван в двух местах — здесь и в подписи под галкой. Разойдись они, программа
        // обещала бы человеку одно, а делала другое.
        Assert.Equal(TimeSpan.FromHours(5), UpdateSchedule.Interval);
        Assert.Equal(UpdateSchedule.Interval, UpdateChecker.AutoCheckInterval);
    }

    [Fact]
    public void A_failed_check_is_repeated_sooner_than_a_successful_one()
    {
        var afterFailure = UpdateSchedule.NextAfter(Now, ok: false);
        var afterSuccess = UpdateSchedule.NextAfter(Now, ok: true);

        Assert.True(afterFailure < afterSuccess);
        Assert.Equal(Now + UpdateSchedule.RetryAfterFailure, afterFailure);
        Assert.Equal(Now + UpdateSchedule.Interval, afterSuccess);
    }

    [Fact]
    public void The_heartbeat_is_much_shorter_than_the_interval()
    {
        // Такт сверяет часы сам, потому что DispatcherTimer не досчитывает время сна. Стань он
        // длиннее интервала — пропущенная за гибернацию проверка сдвинулась бы на него целиком.
        Assert.True(UpdateSchedule.Heartbeat < UpdateSchedule.Interval);
    }

    [Fact]
    public void A_check_that_never_ran_is_due_at_once()
    {
        Assert.True(UpdateSchedule.DueAt(Now, null));
    }

    [Theory]
    [InlineData(4, 59, false)]
    [InlineData(5, 0, true)]
    [InlineData(9, 0, true)]
    public void The_interval_decides_when_to_go_online(int hours, int minutes, bool due)
    {
        var last = Now - new TimeSpan(hours, minutes, 0);

        Assert.Equal(due, UpdateSchedule.DueAt(Now, last));
    }

    [Fact]
    public void A_fresh_check_is_named_relatively_and_an_old_one_by_its_date()
    {
        // Без подписи на конкретном языке: словарь Loc общий на процесс, и соседняя коллекция
        // тестов вправе переключить его на английский посреди этой проверки.
        var never = UpdateSchedule.DescribeLastCheck(null, Now);
        var justNow = UpdateSchedule.DescribeLastCheck(Now - TimeSpan.FromSeconds(20), Now);
        var minutes = UpdateSchedule.DescribeLastCheck(Now - TimeSpan.FromMinutes(40), Now);
        var hours = UpdateSchedule.DescribeLastCheck(Now - TimeSpan.FromHours(3), Now);
        var old = UpdateSchedule.DescribeLastCheck(Now - TimeSpan.FromDays(3), Now);

        Assert.NotEqual(never, justNow);
        Assert.Contains("40", minutes, StringComparison.Ordinal);
        Assert.Contains("3", hours, StringComparison.Ordinal);
        Assert.DoesNotContain("40", hours, StringComparison.Ordinal);

        // Старше суток — абсолютной датой: «73 ч назад» никто в уме не переводит.
        Assert.Contains(
            (Now - TimeSpan.FromDays(3)).ToLocalTime().Year.ToString(System.Globalization.CultureInfo.CurrentCulture),
            old,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Clock_moved_back_does_not_produce_a_negative_age()
    {
        // Перевод часов назад делает разницу отрицательной, и «-3 ч назад» было бы враньём.
        var ahead = UpdateSchedule.DescribeLastCheck(Now + TimeSpan.FromHours(2), Now);

        Assert.Equal(UpdateSchedule.DescribeLastCheck(Now, Now), ahead);
    }

    [Fact]
    public void Nothing_is_downloaded_while_the_toggle_is_off()
    {
        Assert.False(UpdateSchedule.ShouldAutoDownload(autoUpdate: false, Release(new Version(9, 0, 0)), staged: null));
    }

    [Fact]
    public void A_release_without_a_windows_build_is_not_downloaded()
    {
        // Так выглядит запасной путь через редирект страницы релизов: версия известна, списка
        // файлов нет вовсе, и качать оттуда нечего.
        var withoutAssets = new ReleaseInfo("v9.0.0", new Version(9, 0, 0), "https://example.invalid", null, null, []);

        Assert.False(UpdateSchedule.ShouldAutoDownload(autoUpdate: true, withoutAssets, staged: null));
    }

    [Fact]
    public void An_already_downloaded_version_is_not_downloaded_again()
    {
        var release = Release(new Version(9, 0, 0));

        Assert.False(UpdateSchedule.ShouldAutoDownload(autoUpdate: true, release, staged: new Version(9, 0, 0)));
        Assert.True(UpdateSchedule.ShouldAutoDownload(autoUpdate: true, release, staged: new Version(8, 9, 0)));
        Assert.True(UpdateSchedule.ShouldAutoDownload(autoUpdate: true, release, staged: null));
    }

    private static ReleaseInfo Release(Version version) =>
        new(
            "v" + version,
            version,
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v" + version,
            null,
            null,
            [
                new ReleaseAsset(
                    $"Amarin-Admin-AI-v{version}-win-x64.exe",
                    $"https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v{version}/Amarin-Admin-AI-v{version}-win-x64.exe",
                    1024,
                    null)
            ]);
}
