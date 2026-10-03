using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Плашка обновлений как функция от состояния. Пока каждое событие красило плашку само, она
/// расходилась с правдой: «Доступна версия» над кнопкой «Отменить», «Есть обновление» сразу
/// после «Не удалось», «Последняя версия» после неудачной проверки.
/// </summary>
public sealed class UpdateViewTests
{
    private const string Current = "1.29.0";
    private static readonly ReleaseInfo V130 = UpdateTestKit.Release("1.30.0");

    [Fact]
    public void Without_news_the_card_names_the_installed_version()
    {
        var never = View(UpdateState.Initial);
        Assert.Equal(UpdatePhase.UpToDate, never.Phase);
        Assert.Equal(Loc.Format("S.Updates.Installed", Current), never.Status);
        Assert.Equal(UpdateAction.None, never.Action);
        Assert.False(never.ShowOpenRelease);
        Assert.True(never.CanCheck);
        Assert.Null(never.Progress);
        Assert.Equal("v" + Current, never.SidebarText);
        Assert.False(never.SidebarAccent);

        // «Последняя версия» — только когда GitHub в этом запуске правда ответил.
        var checkedNow = View(UpdateState.Initial with { LastSuccessUtc = DateTime.UtcNow });
        Assert.Equal(Loc.Format("S.Updates.UpToDate", Current), checkedNow.Status);
    }

    [Fact]
    public void A_found_release_offers_the_update_and_names_both_versions()
    {
        var view = View(UpdateState.Initial with { Latest = V130 });

        Assert.Equal(UpdatePhase.Found, view.Phase);
        Assert.Equal(Loc.Format("S.Updates.Available", V130.Release, Current), view.Status);
        Assert.True(view.StatusAccent);
        Assert.Equal(UpdateAction.Update, view.Action);
        Assert.Equal(Loc.Get("S.Updates.Update"), view.ActionLabel);
        Assert.True(view.ShowOpenRelease);
        Assert.Equal(Loc.Format("S.Updates.SidebarNewer", Current, V130.Release), view.SidebarText);
        Assert.True(view.SidebarAccent);
    }

    [Fact]
    public void A_found_release_without_a_build_says_where_to_get_it_instead_of_offering_a_button()
    {
        var bare = UpdateTestKit.Release(build: false);

        var view = View(UpdateState.Initial with { Latest = bare });

        Assert.Equal(Loc.Format("S.Updates.AvailableNoBuild", bare.Release, Current), view.Status);
        Assert.Equal(UpdateAction.None, view.Action);
        Assert.True(view.ShowOpenRelease);
    }

    [Fact]
    public void A_background_download_names_the_version_and_its_button_joins_it()
    {
        var view = View(Downloading(install: false));

        Assert.Equal(UpdatePhase.Downloading, view.Phase);
        Assert.Equal(Loc.Format("S.Updates.DownloadingVersion", V130.Release, "40"), view.Status);
        Assert.Equal(UpdateAction.Update, view.Action);
        Assert.Equal(Loc.Get("S.Updates.Update"), view.ActionLabel);
        Assert.Equal(0.4, view.Progress);
        Assert.True(view.CanCheck);
        Assert.Equal(Loc.Format("S.Updates.SidebarNewer", Current, V130.Release), view.SidebarText);
    }

    [Fact]
    public void A_download_the_person_waits_for_says_it_will_install_and_its_button_cancels()
    {
        var view = View(Downloading(install: true));

        Assert.Equal(Loc.Format("S.Updates.DownloadingToInstall", V130.Release, "40"), view.Status);
        Assert.Equal(UpdateAction.Cancel, view.Action);
        Assert.Equal(Loc.Get("S.Common.Cancel"), view.ActionLabel);
        Assert.False(view.CanCheck);
    }

    [Fact]
    public void A_downloaded_build_offers_install_and_warns_about_the_admin_question()
    {
        var plain = View(UpdateState.Initial with { Latest = V130, Staged = UpdateTestKit.Staged(V130) });
        Assert.Equal(UpdatePhase.Downloaded, plain.Phase);
        Assert.Equal(Loc.Format("S.Updates.Ready", V130.Release), plain.Status);
        Assert.Equal(UpdateAction.Install, plain.Action);
        Assert.Equal(Loc.Get("S.Updates.Install"), plain.ActionLabel);
        Assert.Equal(Loc.Format("S.Updates.SidebarStaged", Current, V130.Release), plain.SidebarText);

        var admin = View(UpdateState.Initial with { Latest = V130, Staged = UpdateTestKit.Staged(V130, elevated: true) });
        Assert.Equal(Loc.Format("S.Updates.ReadyAdmin", V130.Release), admin.Status);
    }

    [Fact]
    public void A_failure_names_the_version_and_offers_another_try()
    {
        var failed = View(UpdateState.Initial with { Latest = V130, Failure = new(V130.Release, "обрыв") });
        Assert.Equal(UpdatePhase.Failed, failed.Phase);
        Assert.Equal(Loc.Format("S.Updates.FailedVersion", V130.Release, "обрыв"), failed.Status);
        Assert.Equal(UpdateAction.Update, failed.Action);
        Assert.True(failed.ShowOpenRelease);

        // Неудачный откат: версии нет, и пробовать ещё раз кнопкой обновления нечего.
        var rollback = View(UpdateState.Initial with { Failure = new(null, "занято") });
        Assert.Equal(Loc.Format("S.Updates.Failed", "занято"), rollback.Status);
        Assert.Equal(UpdateAction.None, rollback.Action);
    }

    [Fact]
    public void A_failed_check_says_why_and_leaves_the_release_page_at_hand()
    {
        var view = View(UpdateState.Initial with { LastCheckError = "GitHub ответил 503." });

        Assert.Equal(UpdatePhase.Failed, view.Phase);
        Assert.Equal("GitHub ответил 503.", view.Status);
        Assert.True(view.ShowOpenRelease);
        Assert.True(view.CanCheck);
    }

    [Fact]
    public void A_manual_check_over_a_found_release_says_checking_but_keeps_the_button()
    {
        var view = View(UpdateState.Initial with { Latest = V130, CheckRunning = true, CheckManual = true });

        Assert.Equal(UpdatePhase.Checking, view.Phase);
        Assert.Equal(Loc.Get("S.Updates.Checking"), view.Status);
        Assert.Equal(UpdateAction.Update, view.Action);
        Assert.False(view.CanCheck);
        Assert.False(view.ShowOpenRelease);
    }

    [Fact]
    public void Installing_names_the_version_until_the_restart()
    {
        var staged = UpdateTestKit.Staged(V130);

        foreach (var state in new[]
                 {
                     UpdateState.Initial with { Latest = V130, Installing = staged },
                     UpdateState.Initial with { Latest = V130, Installing = staged, SwapDone = true }
                 })
        {
            var view = View(state);
            Assert.Equal(UpdatePhase.Installing, view.Phase);
            Assert.Equal(Loc.Format("S.Updates.InstallingVersion", V130.Release), view.Status);
            Assert.Equal(UpdateAction.None, view.Action);
            Assert.False(view.CanCheck);
        }
    }

    [Fact]
    public void A_failed_restart_says_the_new_version_is_already_in_place()
    {
        var view = View(UpdateState.Initial with { Latest = V130, SwapDone = true, RestartError = "нет доступа" });

        Assert.Equal(UpdatePhase.Failed, view.Phase);
        Assert.Equal(Loc.Format("S.Updates.RestartFailed", "нет доступа"), view.Status);
        Assert.Equal(UpdateAction.None, view.Action);
        Assert.False(view.CanCheck);
    }

    [Fact]
    public void A_finished_rollback_says_so_until_the_restart()
    {
        var view = View(UpdateState.Initial with { SwapDone = true, Notice = new(UpdateNoticeKind.RollingBack) });

        Assert.Equal(UpdatePhase.Installing, view.Phase);
        Assert.Equal(Loc.Get("S.Updates.RollingBack"), view.Status);
        Assert.Equal(UpdateAction.None, view.Action);
    }

    [Fact]
    public void Notices_speak_over_the_usual_line()
    {
        var found = UpdateState.Initial with { Latest = V130 };

        Assert.Equal(
            Loc.Get("S.Updates.DownloadCancelled"),
            View(found with { Notice = new(UpdateNoticeKind.DownloadCancelled) }).Status);
        Assert.Equal(
            Loc.Get("S.Updates.WaitForTurns"),
            View(found with { Notice = new(UpdateNoticeKind.WaitForTurns) }).Status);
        Assert.Equal(
            Loc.Format("S.Updates.BrowserFailed", "нет браузера"),
            View(found with { Notice = new(UpdateNoticeKind.BrowserFailed, "нет браузера") }).Status);

        var ready = View(found with { Staged = UpdateTestKit.Staged(V130), Notice = new(UpdateNoticeKind.ReadyAfterTurns) });
        Assert.Equal(Loc.Format("S.Updates.ReadyAfterTurns", V130.Release), ready.Status);
        Assert.True(ready.StatusAccent);
        Assert.Equal(UpdateAction.Install, ready.Action);

        // Строка поверх, а кнопка — по фактам: отменённая загрузка возвращает «Обновить».
        Assert.Equal(UpdateAction.Update, View(found with { Notice = new(UpdateNoticeKind.DownloadCancelled) }).Action);
    }

    private static UpdateView View(UpdateState state) => UpdateView.From(state, Current);

    private static UpdateState Downloading(bool install) =>
        UpdateState.Initial with
        {
            Latest = V130,
            Download = new UpdateDownload(
                V130,
                UpdateDownloadOrigin.Background,
                install,
                AllowUnverified: false,
                Share: 0.4,
                Generation: 1,
                UpdateTestKit.Plan(V130))
        };
}
