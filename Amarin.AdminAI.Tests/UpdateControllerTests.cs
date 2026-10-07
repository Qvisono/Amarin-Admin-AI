using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Драйвер автомата: проверка, загрузка, подмена и перезапуск через подставные порты. Время и
/// очередь потока окна двигает тест, поэтому гонки воспроизводятся порядком вызовов, а не удачей.
/// </summary>
public sealed class UpdateControllerTests
{
    private static readonly ReleaseInfo V130 = UpdateTestKit.Release("1.30.0");
    private static readonly ReleaseInfo V131 = UpdateTestKit.Release("1.31.0");

    [Fact]
    public void Start_checks_at_once_and_downloads_what_it_found_in_the_background()
    {
        using var rig = new UpdateRig();

        rig.Controller.Start();
        var call = Assert.Single(rig.Source.Calls);
        Assert.False(call.Beta);
        Assert.True(rig.State.CheckRunning);

        rig.Source.Answer(UpdateTestKit.Found(V130));
        rig.Queue.Run();

        var download = Assert.Single(rig.Files.Downloads);
        Assert.False(download.AllowUnverified);
        Assert.Equal(V130.Release, rig.State.Download?.Version);

        // План — в состоянии: вопрос «Обновить» показывает его, не планируя заново.
        Assert.Equal(download.Plan, rig.State.Download?.Plan);
        Assert.Single(rig.App.Checks);
    }

    [Fact]
    public void Progress_reaches_the_card_and_a_finished_background_download_waits_for_the_exit()
    {
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V130);

        download.Progress.Report(0.5);
        rig.Queue.Run();
        Assert.Equal(0.5, rig.State.Download?.Share);

        download.Finish();
        rig.Queue.Run();

        Assert.Null(rig.State.Download);
        Assert.Equal(V130.Release, rig.State.Staged?.Version);
        Assert.Empty(rig.Files.Swaps);
        Assert.Empty(rig.App.Restarts);
    }

    [Fact]
    public void Update_during_the_background_download_joins_it_then_installs_and_restarts()
    {
        // Гонка 1 от кнопки до перезапуска: загрузка не обрывается и не начинается заново,
        // а по её окончании программа сохраняет чат, подменяет файл и перезапускается.
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V130);

        var offer = Assert.IsType<UpdateOffer>(rig.Controller.Prepare());
        Assert.Same(V130, offer.Release);
        Assert.Equal(1, rig.Files.Plans);

        rig.Controller.Confirm(offer);
        Assert.False(download.Token.IsCancellationRequested);
        Assert.Equal(UpdateAction.Cancel, UpdateView.From(rig.State, "1.29.0").Action);

        download.Finish();
        rig.Queue.RunUntil(() => rig.App.Restarts.Count == 1);

        Assert.Single(rig.Files.Downloads);
        Assert.Equal(V130.Release, Assert.Single(rig.Files.Swaps).Version);
        Assert.Equal(1, rig.App.Saves);
        Assert.Equal("/apps/Amarin Admin AI.exe", rig.App.Restarts[0]);
        Assert.True(rig.State.SwapDone);
    }

    [Fact]
    public void Cancel_stops_the_download_the_person_waits_for()
    {
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V130);
        rig.Controller.Confirm(Assert.IsType<UpdateOffer>(rig.Controller.Prepare()));

        rig.Controller.Cancel();
        rig.Queue.Run();

        Assert.True(download.Token.IsCancellationRequested);
        Assert.Null(rig.State.Download);
        Assert.Equal(UpdateNoticeKind.DownloadCancelled, rig.State.Notice?.Kind);
        Assert.Empty(rig.Files.Swaps);
    }

    [Fact]
    public void A_download_the_downloader_gave_up_on_is_a_failure_with_a_reason()
    {
        // Гонка 7. Повисшее соединение загрузчик меняет сам и докачивает (ResumableDownload), а
        // исчерпав попытки — отвечает отказом. Это отказ на плашке, а не тихая отмена: до 1.32.0
        // срок клиента выдавался за отмену, и загрузка пропадала молча.
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V130);

        download.Fail(Loc.Get("S.Updates.Stalled"));
        rig.Queue.Run();

        Assert.Null(rig.State.Download);
        Assert.Equal(new UpdateFailure(V130.Release, Loc.Get("S.Updates.Stalled")), rig.State.Failure);
    }

    [Fact]
    public void A_long_download_is_not_cut_short_by_the_controller()
    {
        // Сторож теперь у загрузчика и меряет байты; контроллер долгую загрузку не обрывает.
        using var rig = new UpdateRig();
        var download = rig.StartBackgroundDownload(V130);

        download.Progress.Report(0.1);
        rig.Queue.Run();
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        rig.Queue.Run();

        Assert.False(download.Token.IsCancellationRequested);
    }

    [Fact]
    public void A_check_that_hangs_ends_with_a_timeout_and_is_retried_sooner()
    {
        using var rig = new UpdateRig();
        rig.Controller.Start();

        rig.Time.Advance(UpdateController.CheckTimeout);
        rig.Queue.Run();

        Assert.False(rig.State.CheckRunning);
        Assert.Equal(Loc.Get("S.Updates.Timeout"), rig.State.LastCheckError);
        Assert.Equal(rig.Time.GetUtcNow().UtcDateTime + UpdateSchedule.RetryAfterFailure, rig.State.NextAutoCheckUtc);
    }

    [Fact]
    public void The_heartbeat_checks_again_when_the_interval_has_passed()
    {
        using var rig = new UpdateRig();
        rig.Controller.Start();
        rig.Source.Answer(new UpdateCheckResult { Latest = UpdateTestKit.Release("1.29.0") });
        rig.Queue.Run();

        rig.Time.Advance(UpdateSchedule.Interval - UpdateSchedule.Heartbeat);
        rig.Queue.Run();
        Assert.Single(rig.Source.Calls);

        rig.Time.Advance(UpdateSchedule.Heartbeat);
        rig.Queue.Run();
        Assert.Equal(2, rig.Source.Calls.Count);
    }

    [Fact]
    public void Prepare_asks_to_wait_while_answers_run()
    {
        using var rig = new UpdateRig();
        rig.Controller.Seed(state => state with { Latest = V130 });
        rig.App.TurnsRunning = true;

        Assert.Null(rig.Controller.Prepare());
        Assert.Equal(UpdateNoticeKind.WaitForTurns, rig.State.Notice?.Kind);
    }

    [Fact]
    public void Prepare_reports_a_plan_that_cannot_be_made()
    {
        using var rig = new UpdateRig();
        rig.Controller.Seed(state => state with { Latest = V130 });
        rig.Files.PlanError = "нет папки";

        Assert.Null(rig.Controller.Prepare());
        Assert.Equal(new UpdateFailure(V130.Release, "нет папки"), rig.State.Failure);
        Assert.Equal(UpdatePhase.Failed, rig.State.Phase);
    }

    [Fact]
    public void A_build_without_a_checksum_is_downloaded_only_with_consent()
    {
        using var rig = new UpdateRig();
        var bare = UpdateTestKit.Release(checksum: false);
        rig.Controller.Start();
        rig.Source.Answer(UpdateTestKit.Found(bare));
        rig.Queue.Run();
        Assert.Empty(rig.Files.Downloads);

        var offer = Assert.IsType<UpdateOffer>(rig.Controller.Prepare());
        Assert.False(offer.Plan.Verified);
        rig.Controller.Confirm(offer, allowUnverified: true);

        Assert.True(Assert.Single(rig.Files.Downloads).AllowUnverified);
        rig.Files.Downloads[0].Finish();
        rig.Queue.RunUntil(() => rig.Files.Swaps.Count == 1);
        Assert.True(rig.Files.Swaps[0].AllowUnverified);
    }

    [Fact]
    public void A_build_for_a_protected_folder_is_swapped_through_the_admin_path()
    {
        using var rig = new UpdateRig();
        rig.Files.Elevated = true;
        rig.Controller.Seed(state => state with { Latest = V130, Staged = UpdateTestKit.Staged(V130, elevated: true) });

        rig.Controller.Confirm(Assert.IsType<UpdateOffer>(rig.Controller.Prepare()));
        rig.Queue.RunUntil(() => rig.App.Restarts.Count == 1);

        Assert.Single(rig.Files.ElevatedSwaps);
        Assert.Empty(rig.Files.Swaps);
    }

    [Fact]
    public void A_refused_admin_question_keeps_the_build_and_the_button()
    {
        using var rig = new UpdateRig();
        rig.Files.SwapResult = UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationRefused"));
        var staged = UpdateTestKit.Staged(V130, elevated: true);
        rig.Controller.Seed(state => state with { Latest = V130, Staged = staged });

        rig.Controller.Confirm(Assert.IsType<UpdateOffer>(rig.Controller.Prepare()));
        rig.Queue.RunUntil(() => rig.State.Installing is null);

        Assert.Same(staged, rig.State.Staged);
        Assert.Equal(UpdateAction.Update, UpdateView.From(rig.State, "1.29.0").Action);
        Assert.Empty(rig.App.Restarts);
    }

    [Fact]
    public void A_failed_restart_is_reported_and_the_exit_does_not_install_again()
    {
        // Гонка 6 через драйвер.
        using var rig = new UpdateRig();
        rig.App.RestartError = "нет доступа";
        rig.Controller.Seed(state => state with { Latest = V130, Staged = UpdateTestKit.Staged(V130) });

        rig.Controller.Confirm(Assert.IsType<UpdateOffer>(rig.Controller.Prepare()));
        rig.Queue.RunUntil(() => rig.State.RestartError is not null);

        Assert.True(rig.State.SwapDone);
        Assert.Single(rig.Files.Swaps);
        Assert.False(rig.Exit.ShouldDeferClose);

        _ = rig.Exit.BeginAsync();
        rig.Queue.Run();
        Assert.Equal(0, rig.Host.Hidden);
        Assert.Single(rig.Files.Swaps);
        Assert.Equal(1, rig.Host.Shutdowns);
    }

    [Fact]
    public void Changing_the_channel_cancels_the_download_and_asks_the_other_channel()
    {
        using var rig = new UpdateRig();
        rig.Controller.Start();
        rig.Controller.Seed(state => state with { Latest = V130 });
        rig.Controller.Confirm(Assert.IsType<UpdateOffer>(rig.Controller.Prepare()));
        var download = Assert.Single(rig.Files.Downloads);

        rig.App.Beta = true;
        rig.Controller.ChangeChannel();

        Assert.True(download.Token.IsCancellationRequested);
        Assert.Equal(2, rig.Source.Calls.Count);
        Assert.True(rig.Source.Calls[1].Beta);

        // Ответ проверки по прежнему каналу опоздал и не применяется.
        rig.Source.Answer(UpdateTestKit.Found(V130), index: 0);
        rig.Queue.Run();
        Assert.Null(rig.State.Latest);
        Assert.True(rig.State.CheckRunning);

        rig.Source.Answer(UpdateTestKit.Found(V131), index: 1);
        rig.Queue.Run();
        Assert.Same(V131, rig.State.Latest);
    }

    [Fact]
    public void An_event_raised_by_an_effect_waits_its_turn_and_the_card_is_told_once()
    {
        // План фоновой загрузки подаётся событием изнутри её же запуска; вложенный переход
        // увидел бы состояние раньше, чем его оставил внешний.
        using var rig = new UpdateRig();
        rig.Controller.Start();
        var changes = 0;
        rig.Controller.Changed += () => changes++;

        rig.Source.Answer(UpdateTestKit.Found(V130));
        rig.Queue.Run();

        Assert.Equal(1, changes);
        Assert.NotNull(rig.State.Download?.Plan);
    }

    [Fact]
    public void Waiting_for_a_state_ends_when_it_comes_or_when_time_runs_out()
    {
        // Без await: продолжение встало бы в ручную очередь, которую в этот момент никто не
        // разбирает.
        using var rig = new UpdateRig();

        Assert.True(rig.Controller.WaitUntilAsync(state => !state.CheckRunning, CancellationToken.None).IsCompletedSuccessfully);

        rig.Controller.Start();
        var settled = rig.Controller.WaitUntilAsync(state => !state.CheckRunning, CancellationToken.None);
        Assert.False(settled.IsCompleted);
        rig.Source.Answer(UpdateCheckResult.Failed("нет сети"));
        rig.Queue.RunUntil(() => settled.IsCompleted);
        Assert.True(settled.IsCompletedSuccessfully);

        using var limit = new CancellationTokenSource();
        var never = rig.Controller.WaitUntilAsync(state => state.SwapDone, limit.Token);
        limit.Cancel();
        rig.Queue.RunUntil(() => never.IsCompleted);
        Assert.True(never.IsCanceled);
    }
}
