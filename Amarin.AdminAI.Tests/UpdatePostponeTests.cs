using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Отменённая загрузка откладывает версию до следующего запуска. До 1.30.0 отмена не держалась:
/// следующая проверка заводила ту же загрузку заново, а закрытие программы докачивало и ставило
/// её; у фоновой загрузки не было и самой кнопки отмены.
/// </summary>
public sealed class UpdatePostponeTests
{
    private static readonly ReleaseInfo V130 = UpdateTestKit.Release("1.30.0");
    private static readonly ReleaseInfo V131 = UpdateTestKit.Release("1.31.0");
    private static readonly UpdateContext Context = UpdateTestKit.Context();

    [Fact]
    public void A_cancelled_background_download_is_not_started_again_by_the_next_check()
    {
        var cancelled = Next(Background(V130), new UpdateEvent.CancelRequested());
        Assert.Null(cancelled.State.Download);
        Assert.Equal(V130.Release, cancelled.State.Postponed);
        Assert.Contains(new UpdateEffect.CancelDownload(1), cancelled.Effects);

        var rechecked = Checked(cancelled.State, UpdateTestKit.Found(V130));
        Assert.Null(rechecked.State.Download);
        Assert.DoesNotContain(rechecked.Effects, effect => effect is UpdateEffect.StartDownload);

        // Выход тоже не докачивает её сам.
        var retried = Next(rechecked.State, new UpdateEvent.RetryDownload());
        Assert.Empty(retried.Effects);
        Assert.False(UpdateMachine.WantedAutomatically(rechecked.State, V130, Context));
    }

    [Fact]
    public void A_newer_release_than_the_postponed_one_downloads_as_usual()
    {
        var cancelled = Next(Background(V130), new UpdateEvent.CancelRequested()).State;

        var newer = Checked(cancelled, UpdateTestKit.Found(V131));

        Assert.Equal(V131.Release, newer.State.Download?.Version);
        Assert.Contains(newer.Effects, effect => effect is UpdateEffect.StartDownload);
    }

    [Fact]
    public void Pressing_update_after_cancelling_takes_the_version_back()
    {
        var cancelled = Next(Background(V130), new UpdateEvent.CancelRequested()).State;

        var confirmed = Next(cancelled, new UpdateEvent.UpdateConfirmed(V130, UpdateTestKit.Plan(V130), AllowUnverified: false));

        Assert.Null(confirmed.State.Postponed);
        Assert.Equal(UpdateDownloadOrigin.User, confirmed.State.Download?.Origin);
        Assert.Contains(confirmed.Effects, effect => effect is UpdateEffect.StartDownload);
    }

    [Fact]
    public void Switching_the_channel_forgets_what_was_postponed()
    {
        var cancelled = Next(Background(V130), new UpdateEvent.CancelRequested()).State;

        Assert.Null(Next(cancelled, new UpdateEvent.ChannelChanged()).State.Postponed);
    }

    [Fact]
    public void The_program_closes_without_waiting_for_a_postponed_version()
    {
        using var rig = new UpdateRig();
        rig.StartBackgroundDownload(V130);
        rig.Controller.Cancel();
        rig.Queue.Run();

        Assert.Equal(V130.Release, rig.State.Postponed);
        rig.Controller.Seed(state => state with { LastSuccessUtc = rig.Time.GetUtcNow().UtcDateTime });
        Assert.False(rig.Exit.PendingForExit);
        Assert.False(rig.Exit.ShouldDeferClose);
    }

    [Fact]
    public void The_view_offers_to_cancel_only_a_download_nobody_waits_for()
    {
        var background = UpdateView.From(Background(V130), "1.29.0");
        Assert.True(background.CanCancelDownload);
        Assert.Equal(UpdateAction.Update, background.Action);

        var wanted = UpdateView.From(Background(V130) with { Download = Background(V130).Download! with { InstallRequested = true } }, "1.29.0");
        Assert.False(wanted.CanCancelDownload);
        Assert.Equal(UpdateAction.Cancel, wanted.Action);
    }

    [Fact]
    public void After_cancelling_the_card_says_the_version_comes_back_after_a_restart()
    {
        var cancelled = Next(Background(V130), new UpdateEvent.CancelRequested()).State with { Notice = null };

        var view = UpdateView.From(cancelled, "1.29.0");

        Assert.Equal(Loc.Format("S.Updates.Postponed", V130.Release), view.Status);
        Assert.Equal(UpdateAction.Update, view.Action);
    }

    [Fact]
    public void The_title_bar_badge_follows_the_facts()
    {
        Assert.Equal(UpdateBadge.Hidden, Badge(UpdateState.Initial));
        Assert.Equal(UpdateBadge.Hidden, Badge(UpdateState.Initial with { LastCheckError = "нет сети" }));
        Assert.Equal(UpdateBadge.Available, Badge(UpdateState.Initial with { Latest = V130 }));
        Assert.Equal(UpdateBadge.Downloading, Badge(Background(V130)));
        Assert.Equal(UpdateBadge.Ready, Badge(UpdateState.Initial with { Latest = V130, Staged = UpdateTestKit.Staged(V130) }));
        Assert.Equal(UpdateBadge.Installing, Badge(UpdateState.Initial with { Installing = UpdateTestKit.Staged(V130) }));
        Assert.Equal(UpdateBadge.Failed, Badge(UpdateState.Initial with { Latest = V130, Failure = new(V130.Release, "сбой") }));
        Assert.Equal(UpdateBadge.Failed, Badge(UpdateState.Initial with { RestartError = "занят" }));
    }

    private static UpdateBadge Badge(UpdateState state) => UpdateView.From(state, "1.29.0").Badge;

    /// <summary>Проверка нашла выпуск, и фоновая загрузка номер один пошла.</summary>
    private static UpdateState Background(ReleaseInfo release) =>
        Checked(UpdateState.Initial, UpdateTestKit.Found(release)).State;

    private static UpdateTransition Next(UpdateState state, UpdateEvent update) => UpdateMachine.Next(state, update, Context);

    private static UpdateTransition Checked(UpdateState state, UpdateCheckResult result)
    {
        var running = Next(state, new UpdateEvent.CheckRequested(Manual: false)).State;
        return Next(running, new UpdateEvent.CheckFinished(running.CheckGeneration, result));
    }
}
