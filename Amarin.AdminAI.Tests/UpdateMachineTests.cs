using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Переходы автомата обновлений — без сети, диска и окна. До 1.30.0 эти решения жили полями
/// главного окна и проверялись только оконными тестами, которые на CI не идут; гонки между
/// проверкой, фоновой загрузкой и кнопкой «Обновить» не проверялись вовсе.
/// </summary>
public sealed class UpdateMachineTests
{
    private static readonly ReleaseInfo V130 = UpdateTestKit.Release("1.30.0");
    private static readonly ReleaseInfo V131 = UpdateTestKit.Release("1.31.0");
    private static readonly UpdateContext Context = UpdateTestKit.Context();

    [Fact]
    public void A_heartbeat_checks_at_once_and_then_waits_for_the_interval()
    {
        var started = Next(UpdateState.Initial, new UpdateEvent.Heartbeat());
        Assert.True(started.State.CheckRunning);
        Assert.False(started.State.CheckManual);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.StartCheck(1) }, started.Effects);

        var done = Next(started.State, new UpdateEvent.CheckFinished(1, Nothing()));
        Assert.False(done.State.CheckRunning);
        Assert.Equal(Context.NowUtc, done.State.LastSuccessUtc);
        Assert.Equal(Context.NowUtc + UpdateSchedule.Interval, done.State.NextAutoCheckUtc);
        Assert.Contains(new UpdateEffect.RememberCheck(), done.Effects);
        Assert.Equal(UpdatePhase.UpToDate, done.State.Phase);

        var early = Next(done.State, new UpdateEvent.Heartbeat(), UpdateTestKit.Context(now: Context.NowUtc + UpdateSchedule.Interval - TimeSpan.FromMinutes(1)));
        Assert.Same(done.State, early.State);
        Assert.Empty(early.Effects);

        var due = Next(done.State, new UpdateEvent.Heartbeat(), UpdateTestKit.Context(now: Context.NowUtc + UpdateSchedule.Interval));
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.StartCheck(2) }, due.Effects);
    }

    [Fact]
    public void A_failed_check_is_repeated_sooner_and_says_why()
    {
        var failed = Checked(UpdateState.Initial, UpdateCheckResult.Failed("нет сети"));

        Assert.Equal(Context.NowUtc + UpdateSchedule.RetryAfterFailure, failed.State.NextAutoCheckUtc);
        Assert.Null(failed.State.LastSuccessUtc);
        Assert.Equal("нет сети", failed.State.LastCheckError);
        Assert.Equal(UpdatePhase.Failed, failed.State.Phase);
    }

    [Fact]
    public void The_heartbeat_stays_quiet_when_checking_would_be_wrong()
    {
        // Галка снята, выход начат, файл уже подменён, проверка и так идёт.
        Assert.Empty(Next(UpdateState.Initial, new UpdateEvent.Heartbeat(), UpdateTestKit.Context(auto: false)).Effects);
        Assert.Empty(Next(UpdateState.Initial with { Exiting = true }, new UpdateEvent.Heartbeat()).Effects);
        Assert.Empty(Next(UpdateState.Initial with { SwapDone = true }, new UpdateEvent.Heartbeat()).Effects);
        Assert.Empty(Next(UpdateState.Initial with { CheckRunning = true, CheckGeneration = 1 }, new UpdateEvent.Heartbeat()).Effects);
    }

    [Fact]
    public void A_manual_check_says_checking_over_a_found_release_and_is_never_doubled()
    {
        var found = UpdateState.Initial with { Latest = V130, Notice = new(UpdateNoticeKind.DownloadCancelled) };

        var manual = Next(found, new UpdateEvent.CheckRequested(Manual: true));
        Assert.Equal(UpdatePhase.Checking, manual.State.Phase);
        Assert.Null(manual.State.Notice);

        var again = Next(manual.State, new UpdateEvent.CheckRequested(Manual: true));
        Assert.Same(manual.State, again.State);
        Assert.Empty(again.Effects);

        // Фоновая проверка поверх найденного не прячет кнопку «Обновить» за «Проверяем».
        var background = Next(found with { Notice = null }, new UpdateEvent.CheckRequested(Manual: false));
        Assert.Equal(UpdatePhase.Found, background.State.Phase);
    }

    [Fact]
    public void A_verified_release_is_downloaded_in_the_background_as_soon_as_it_is_found()
    {
        var found = Checked(UpdateState.Initial, UpdateTestKit.Found(V130));

        Assert.Same(V130, found.State.Latest);
        var download = Assert.IsType<UpdateDownload>(found.State.Download);
        Assert.Equal(UpdateDownloadOrigin.Background, download.Origin);
        Assert.False(download.InstallRequested);
        Assert.False(download.Wanted);
        Assert.Contains(new UpdateEffect.StartDownload(download.Generation, V130, null, false), found.Effects);
        Assert.Equal(UpdatePhase.Downloading, found.State.Phase);
    }

    [Fact]
    public void What_the_person_has_not_allowed_is_found_but_not_downloaded()
    {
        // Галка снята; сборка без суммы; версия, от которой человек вернулся к прошлой.
        AssertNoDownload(Checked(UpdateState.Initial, UpdateTestKit.Found(V130), UpdateTestKit.Context(auto: false)));
        AssertNoDownload(Checked(UpdateState.Initial, UpdateTestKit.Found(UpdateTestKit.Release(checksum: false))));
        AssertNoDownload(Checked(UpdateState.Initial, UpdateTestKit.Found(V130), UpdateTestKit.Context(declined: V130.Release)));

        // Уже скачанное второй раз не качается.
        AssertNoDownload(Checked(UpdateState.Initial with { Staged = UpdateTestKit.Staged(V130) }, UpdateTestKit.Found(V130)));

        static void AssertNoDownload(UpdateTransition transition)
        {
            Assert.NotNull(transition.State.Latest);
            Assert.Null(transition.State.Download);
            Assert.DoesNotContain(transition.Effects, effect => effect is UpdateEffect.StartDownload);
        }
    }

    [Fact]
    public void A_poorer_answer_about_the_same_release_keeps_the_richer_one()
    {
        // Запасной путь без API знает о выпуске меньше; его ответ не должен прятать кнопку.
        var poorer = UpdateTestKit.Release(build: false);
        var known = UpdateState.Initial with { Latest = V130, Staged = UpdateTestKit.Staged(V130) };

        var after = Checked(known, UpdateTestKit.Found(poorer));

        Assert.Same(V130, after.State.Latest);
    }

    [Fact]
    public void A_failed_recheck_keeps_the_found_release()
    {
        var found = UpdateState.Initial with { Latest = V130 };

        var after = Checked(found, UpdateCheckResult.Failed("нет сети"));

        Assert.Same(V130, after.State.Latest);
        Assert.Equal(UpdatePhase.Found, after.State.Phase);
    }

    [Fact]
    public void An_answer_from_a_replaced_check_is_dropped()
    {
        // Смена канала заменила проверку новой: ответ прежней — про другой канал.
        var replaced = UpdateState.Initial with { CheckRunning = true, CheckGeneration = 2 };

        var late = Next(replaced, new UpdateEvent.CheckFinished(1, UpdateTestKit.Found(V130)));

        Assert.Same(replaced, late.State);
        Assert.Empty(late.Effects);
    }

    [Fact]
    public void Update_during_a_background_download_joins_it_instead_of_cancelling_it()
    {
        // Гонка 1. До 1.30.0 кнопка над фоновой загрузкой была подписана «Отмена» и обрывала
        // её: полоса пропадала, и обновиться можно было только нажав ещё раз, качая заново.
        var background = Downloading(V130);

        var joined = Next(background, Confirmed(V130));

        var download = Assert.IsType<UpdateDownload>(joined.State.Download);
        Assert.True(download.InstallRequested);
        Assert.True(download.Wanted);
        Assert.Equal(1, download.Generation);
        Assert.Equal(0.4, download.Share);
        Assert.Empty(joined.Effects);
    }

    [Fact]
    public void Apply_after_a_background_download_began_behind_the_question_joins_it()
    {
        // Гонка 2. Вопрос открыт по найденной версии; пока человек читает заметки, проверка
        // начинает фоновую загрузку того же выпуска. Раньше «Применить» тогда молча не делал
        // ничего: загрузка уже шла, и кнопку просто проглатывали.
        var asked = UpdateState.Initial with { Latest = V130 };
        var plan = UpdateTestKit.Plan(V130);

        var behind = Checked(asked, UpdateTestKit.Found(V130));
        var applied = Next(behind.State, new UpdateEvent.UpdateConfirmed(V130, plan, AllowUnverified: false));

        Assert.True(applied.State.Download?.InstallRequested);
        Assert.Single(behind.Effects, effect => effect is UpdateEffect.StartDownload);
        Assert.DoesNotContain(applied.Effects, effect => effect is UpdateEffect.StartDownload or UpdateEffect.CancelDownload);
    }

    [Fact]
    public void Confirming_what_is_already_downloaded_installs_it_without_downloading_again()
    {
        var staged = UpdateTestKit.Staged(V130);

        var install = Next(UpdateState.Initial with { Latest = V130, Staged = staged }, Confirmed(V130));

        Assert.Same(staged, install.State.Installing);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.Install(staged) }, install.Effects);
        Assert.Equal(UpdatePhase.Installing, install.State.Phase);
    }

    [Fact]
    public void Confirming_while_answers_run_asks_to_wait_and_changes_nothing_else()
    {
        var background = Downloading(V130);

        var refused = Next(background, Confirmed(V130), UpdateTestKit.Context(turns: true));

        Assert.Equal(new UpdateNotice(UpdateNoticeKind.WaitForTurns), refused.State.Notice);
        Assert.Equal(background.Download, refused.State.Download);
        Assert.Empty(refused.Effects);
    }

    [Fact]
    public void Confirming_another_version_replaces_the_background_download()
    {
        var background = Downloading(V130);
        var plan = UpdateTestKit.Plan(V131);

        var replaced = Next(background, new UpdateEvent.UpdateConfirmed(V131, plan, AllowUnverified: false));

        var download = Assert.IsType<UpdateDownload>(replaced.State.Download);
        Assert.Equal(UpdateDownloadOrigin.User, download.Origin);
        Assert.Equal(V131.Release, download.Version);
        Assert.Equal(2, download.Generation);
        Assert.Equal(
            new UpdateEffect[] { new UpdateEffect.CancelDownload(1), new UpdateEffect.StartDownload(2, V131, plan, false) },
            replaced.Effects);
    }

    [Fact]
    public void A_download_the_person_waits_for_installs_as_soon_as_it_is_verified()
    {
        var joined = Next(Downloading(V130), Confirmed(V130)).State;

        var done = Next(joined, Finished(1, V130));

        var installing = Assert.IsType<StagedUpdate>(done.State.Installing);
        Assert.Equal(V130.Release, installing.Version);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.Install(installing) }, done.Effects);
        Assert.Null(done.State.Download);
    }

    [Fact]
    public void A_background_download_is_only_put_aside_for_the_exit()
    {
        var done = Next(Downloading(V130), Finished(1, V130));

        Assert.Equal(V130.Release, done.State.Staged?.Version);
        Assert.Null(done.State.Installing);
        Assert.Empty(done.Effects);
        Assert.Equal(UpdatePhase.Downloaded, done.State.Phase);
    }

    [Fact]
    public void A_joined_download_that_ends_during_an_answer_waits_for_the_button()
    {
        // Перезапуск без спроса оборвал бы ответ, начатый, пока качали.
        var joined = Next(Downloading(V130), Confirmed(V130)).State;

        var done = Next(joined, Finished(1, V130), UpdateTestKit.Context(turns: true));

        Assert.NotNull(done.State.Staged);
        Assert.Null(done.State.Installing);
        Assert.Equal(UpdateNoticeKind.ReadyAfterTurns, done.State.Notice?.Kind);
        Assert.Empty(done.Effects);
    }

    [Fact]
    public void A_joined_download_that_ends_after_the_exit_began_is_left_to_the_exit()
    {
        var joined = Next(Downloading(V130), Confirmed(V130)).State with { Exiting = true };

        var done = Next(joined, Finished(1, V130));

        Assert.NotNull(done.State.Staged);
        Assert.Null(done.State.Installing);
        Assert.Empty(done.Effects);
    }

    [Fact]
    public void Cancel_stops_the_download_and_says_so()
    {
        var joined = Next(Downloading(V130), Confirmed(V130)).State;

        var cancelled = Next(joined, new UpdateEvent.CancelRequested());

        Assert.Null(cancelled.State.Download);
        Assert.Equal(UpdateNoticeKind.DownloadCancelled, cancelled.State.Notice?.Kind);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.CancelDownload(1) }, cancelled.Effects);

        // Найденная версия остаётся — кнопка «Обновить» снова на месте.
        Assert.Same(V130, cancelled.State.Latest);

        var nothing = Next(UpdateState.Initial, new UpdateEvent.CancelRequested());
        Assert.Empty(nothing.Effects);
    }

    [Fact]
    public void Late_news_from_a_cancelled_download_is_dropped()
    {
        // Отчёты о доле и итог приходят очередью и могут опоздать к отмене.
        var cancelled = Next(Downloading(V130), new UpdateEvent.CancelRequested()).State;

        Assert.Same(cancelled, Next(cancelled, new UpdateEvent.DownloadProgress(1, 0.9)).State);
        Assert.Same(cancelled, Next(cancelled, Finished(1, V130)).State);
        Assert.Same(cancelled, Next(cancelled, new UpdateEvent.DownloadCancelled(1)).State);
        Assert.Same(cancelled, Next(cancelled, new UpdateEvent.DownloadPlanned(1, UpdateTestKit.Plan(V130))).State);
    }

    [Fact]
    public void Progress_moves_only_its_own_download_and_never_past_the_end()
    {
        var background = Downloading(V130);

        Assert.Equal(0.75, Next(background, new UpdateEvent.DownloadProgress(1, 0.75)).State.Download?.Share);
        Assert.Equal(1.0, Next(background, new UpdateEvent.DownloadProgress(1, 1.5)).State.Download?.Share);
        Assert.Same(background, Next(background, new UpdateEvent.DownloadProgress(7, 0.75)).State);
    }

    [Fact]
    public void Changing_the_channel_forgets_everything_found_and_checks_again()
    {
        // Гонка 3. Ручная загрузка беты продолжалась после того, как галку беты сняли, и бета
        // вставала, хотя человек от неё отказался.
        var state = Downloading(V131, UpdateDownloadOrigin.User, install: true) with
        {
            Staged = UpdateTestKit.Staged(V130),
            CheckRunning = true,
            CheckGeneration = 4
        };

        var changed = Next(state, new UpdateEvent.ChannelChanged());

        Assert.Null(changed.State.Download);
        Assert.Null(changed.State.Staged);
        Assert.Null(changed.State.Latest);
        Assert.True(changed.State.CheckRunning);
        Assert.True(changed.State.CheckManual);
        Assert.Equal(5, changed.State.CheckGeneration);
        Assert.Equal(
            new UpdateEffect[] { new UpdateEffect.CancelDownload(1), new UpdateEffect.StartCheck(5) },
            changed.Effects);

        // Ответ проверки, начатой по прежнему каналу, уже не применяется.
        Assert.Same(changed.State, Next(changed.State, new UpdateEvent.CheckFinished(4, UpdateTestKit.Found(V131))).State);
    }

    [Fact]
    public void A_failed_download_stays_failed_until_something_new_happens()
    {
        // Гонка 5. Отказ фоновой загрузки красил плашку «Не удалось», а следующая перерисовка —
        // уже «Есть обновление»: что случилось, человек так и не узнавал.
        var failed = Next(Downloading(V130), new UpdateEvent.DownloadFinished(1, UpdateStepResult.Failed("обрыв"), null, null)).State;

        Assert.Equal(UpdatePhase.Failed, failed.Phase);
        Assert.Equal(new UpdateFailure(V130.Release, "обрыв"), failed.Failure);

        // Такт до срока и новое открытие страницы ничего не меняют, а фоновая проверка отказ
        // не прячет.
        var waiting = failed with { NextAutoCheckUtc = Context.NowUtc + TimeSpan.FromHours(1) };
        Assert.Same(waiting, Next(waiting, new UpdateEvent.Heartbeat()).State);
        Assert.Same(waiting, Next(waiting, new UpdateEvent.NoticesDismissed()).State);
        Assert.Equal(UpdatePhase.Failed, Next(failed, new UpdateEvent.CheckRequested(Manual: false)).State.Phase);

        // Следующая удачная проверка снимает отказ и качает снова.
        var retried = Checked(failed, UpdateTestKit.Found(V130));
        Assert.Null(retried.State.Failure);
        Assert.NotNull(retried.State.Download);
    }

    [Fact]
    public void A_refused_swap_keeps_the_downloaded_build_for_the_next_try()
    {
        // Отказ в окне UAC — не повод качать восемьдесят мегабайт заново.
        var staged = UpdateTestKit.Staged(V130, elevated: true);
        var installing = UpdateState.Initial with { Latest = V130, Staged = staged, Installing = staged };

        var refused = Next(installing, new UpdateEvent.SwapFinished(staged, UpdateStepResult.Failed("отказано")));

        Assert.Null(refused.State.Installing);
        Assert.Same(staged, refused.State.Staged);
        Assert.Equal(new UpdateFailure(V130.Release, "отказано"), refused.State.Failure);
        Assert.Empty(refused.Effects);

        var again = Next(refused.State, Confirmed(V130));
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.Install(staged) }, again.Effects);
    }

    [Fact]
    public void A_done_swap_restarts_and_a_failed_restart_is_never_redone()
    {
        // Гонка 6. Неудачный перезапуск не отмечал подмену, и выход принимал работающую ещё
        // прежнюю версию за недоведённое обновление — качал и подменял файл второй раз.
        var staged = UpdateTestKit.Staged(V130);
        var installing = UpdateState.Initial with { Latest = V130, Staged = staged, Installing = staged };

        var swapped = Next(installing, new UpdateEvent.SwapFinished(staged, UpdateStepResult.Success));
        Assert.True(swapped.State.SwapDone);
        Assert.Null(swapped.State.Staged);
        Assert.Equal(UpdatePhase.Installing, swapped.State.Phase);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.Restart(staged.Plan.ExePath) }, swapped.Effects);

        var failed = Next(swapped.State, new UpdateEvent.RestartFailed("нет доступа"));
        Assert.Equal("нет доступа", failed.State.RestartError);
        Assert.Null(failed.State.Installing);
        Assert.Equal(UpdatePhase.Failed, failed.State.Phase);

        // После подмены — ни проверок, ни загрузок, ни второй установки.
        Assert.Empty(Next(failed.State, new UpdateEvent.CheckRequested(Manual: true)).Effects);
        Assert.Empty(Next(failed.State, Confirmed(V130)).Effects);
        Assert.Empty(Next(failed.State with { Latest = V131 }, new UpdateEvent.RetryDownload()).Effects);
    }

    [Fact]
    public void Turning_auto_update_off_drops_what_it_brought_but_not_what_the_person_asked_for()
    {
        var off = UpdateTestKit.Context(auto: false);

        var background = Downloading(V131) with { Staged = UpdateTestKit.Staged(V130) };
        var dropped = Next(background, new UpdateEvent.AutoUpdateChanged(On: false), off);
        Assert.Null(dropped.State.Download);
        Assert.Null(dropped.State.Staged);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.CancelDownload(1) }, dropped.Effects);

        // Присоединённая загрузка становится загрузкой человека: снятая галка её не трогает.
        var joined = Next(Downloading(V130), Confirmed(V130)).State;
        var kept = Next(joined, new UpdateEvent.AutoUpdateChanged(On: false), off);
        Assert.Equal(UpdateDownloadOrigin.User, kept.State.Download?.Origin);
        Assert.Empty(kept.Effects);
    }

    [Fact]
    public void Turning_auto_update_on_checks_at_once()
    {
        var waiting = UpdateState.Initial with { NextAutoCheckUtc = Context.NowUtc + TimeSpan.FromHours(4) };

        var on = Next(waiting, new UpdateEvent.AutoUpdateChanged(On: true));

        Assert.Equal(new UpdateEffect[] { new UpdateEffect.StartCheck(1) }, on.Effects);
    }

    [Fact]
    public void A_rollback_forgets_the_update_and_ends_in_a_restart()
    {
        var state = Downloading(V131) with { Staged = UpdateTestKit.Staged(V130) };

        var rolling = Next(state, new UpdateEvent.RollbackStarted());
        Assert.Null(rolling.State.Download);
        Assert.Null(rolling.State.Staged);
        Assert.Equal(UpdateNoticeKind.RollingBack, rolling.State.Notice?.Kind);
        Assert.Equal(new UpdateEffect[] { new UpdateEffect.CancelDownload(1) }, rolling.Effects);

        // Прошлая версия на месте: до перезапуска плашка говорит «Возвращаю…», а выход не
        // примет её за недоведённое обновление.
        var done = Next(rolling.State, new UpdateEvent.RollbackDone());
        Assert.True(done.State.SwapDone);
        Assert.Equal(UpdateNoticeKind.RollingBack, done.State.Notice?.Kind);
        Assert.Equal(UpdatePhase.Installing, done.State.Phase);

        var failed = Next(rolling.State, new UpdateEvent.RollbackFailed("занято"));
        Assert.Equal(new UpdateFailure(null, "занято"), failed.State.Failure);
        Assert.False(failed.State.SwapDone);
    }

    [Fact]
    public void Retry_downloads_the_found_release_only_when_it_is_still_wanted()
    {
        var found = UpdateState.Initial with { Latest = V130 };

        Assert.Single(Next(found, new UpdateEvent.RetryDownload()).Effects, effect => effect is UpdateEffect.StartDownload);
        Assert.Empty(Next(found, new UpdateEvent.RetryDownload(), UpdateTestKit.Context(auto: false)).Effects);
        Assert.Empty(Next(found, new UpdateEvent.RetryDownload(), UpdateTestKit.Context(declined: V130.Release)).Effects);
        Assert.Empty(Next(found with { Staged = UpdateTestKit.Staged(V130) }, new UpdateEvent.RetryDownload()).Effects);
        Assert.Empty(Next(Downloading(V130), new UpdateEvent.RetryDownload()).Effects);
    }

    [Fact]
    public void A_swap_on_exit_marks_the_file_as_replaced()
    {
        var staged = UpdateState.Initial with { Staged = UpdateTestKit.Staged(V130) };

        var swapping = Next(staged, new UpdateEvent.SwapOnExitStarted());

        Assert.Null(swapping.State.Staged);
        Assert.True(swapping.State.SwapDone);
    }

    [Fact]
    public void Opening_the_page_dismisses_answers_to_old_clicks_but_not_facts()
    {
        var cancelled = UpdateState.Initial with { Latest = V130, Notice = new(UpdateNoticeKind.DownloadCancelled) };
        Assert.Null(Next(cancelled, new UpdateEvent.NoticesDismissed()).State.Notice);

        // «Скачана, поставлю по кнопке» и «Возвращаю прошлую версию» — не ответы на нажатие.
        var ready = UpdateState.Initial with { Staged = UpdateTestKit.Staged(V130), Notice = new(UpdateNoticeKind.ReadyAfterTurns) };
        Assert.Same(ready, Next(ready, new UpdateEvent.NoticesDismissed()).State);

        var rolling = UpdateState.Initial with { Notice = new(UpdateNoticeKind.RollingBack) };
        Assert.Same(rolling, Next(rolling, new UpdateEvent.NoticesDismissed()).State);
    }

    [Fact]
    public void The_phase_follows_the_order_of_the_card()
    {
        var staged = UpdateTestKit.Staged(V130);
        var download = Downloading(V131).Download;

        Assert.Equal(UpdatePhase.UpToDate, UpdateState.Initial.Phase);
        Assert.Equal(UpdatePhase.Failed, (UpdateState.Initial with { LastCheckError = "x" }).Phase);
        Assert.Equal(UpdatePhase.Checking, (UpdateState.Initial with { LastCheckError = "x", CheckRunning = true }).Phase);
        Assert.Equal(UpdatePhase.Found, (UpdateState.Initial with { Latest = V130, CheckRunning = true }).Phase);
        Assert.Equal(UpdatePhase.Downloaded, (UpdateState.Initial with { Latest = V130, Staged = staged }).Phase);
        Assert.Equal(UpdatePhase.Failed, (UpdateState.Initial with { Staged = staged, Failure = new(V130.Release, "x") }).Phase);
        Assert.Equal(UpdatePhase.Checking, (UpdateState.Initial with { Failure = new(null, "x"), CheckRunning = true, CheckManual = true }).Phase);
        Assert.Equal(UpdatePhase.Downloading, (UpdateState.Initial with { Download = download, CheckRunning = true, CheckManual = true }).Phase);
        Assert.Equal(UpdatePhase.Installing, (UpdateState.Initial with { Download = download, Installing = staged }).Phase);
        Assert.Equal(UpdatePhase.Failed, (UpdateState.Initial with { Installing = staged, SwapDone = true, RestartError = "x" }).Phase);
    }

    [Fact]
    public void Wanted_automatically_means_newer_than_installed_and_than_the_declined_one()
    {
        Assert.True(UpdateMachine.WantedAutomatically(V130, Context));
        Assert.False(UpdateMachine.WantedAutomatically(UpdateTestKit.Release("1.29.0"), Context));
        Assert.False(UpdateMachine.WantedAutomatically(V130, UpdateTestKit.Context(declined: V130.Release)));
        Assert.True(UpdateMachine.WantedAutomatically(V131, UpdateTestKit.Context(declined: V130.Release)));
    }

    private static UpdateTransition Next(UpdateState state, UpdateEvent update, UpdateContext? context = null) =>
        UpdateMachine.Next(state, update, context ?? Context);

    /// <summary>Фоновая проверка с этим ответом только что кончилась.</summary>
    private static UpdateTransition Checked(UpdateState state, UpdateCheckResult result, UpdateContext? context = null)
    {
        var running = Next(state, new UpdateEvent.CheckRequested(Manual: false), context).State;
        return Next(running, new UpdateEvent.CheckFinished(running.CheckGeneration, result), context);
    }

    /// <summary>Идёт загрузка номер один, уже спланированная и скачанная на сорок процентов.</summary>
    private static UpdateState Downloading(
        ReleaseInfo release,
        UpdateDownloadOrigin origin = UpdateDownloadOrigin.Background,
        bool install = false) =>
        UpdateState.Initial with
        {
            Latest = release,
            Download = new UpdateDownload(release, origin, install, AllowUnverified: false, Share: 0.4, Generation: 1, UpdateTestKit.Plan(release)),
            DownloadGeneration = 1
        };

    private static UpdateEvent.UpdateConfirmed Confirmed(ReleaseInfo release) =>
        new(release, UpdateTestKit.Plan(release), AllowUnverified: false);

    private static UpdateEvent.DownloadFinished Finished(int generation, ReleaseInfo release)
    {
        var plan = UpdateTestKit.Plan(release);
        return new(generation, UpdateStepResult.Success, plan.WorkDirectory + "/" + plan.Asset.Name, plan);
    }

    /// <summary>Ответ GitHub «новее нет».</summary>
    private static UpdateCheckResult Nothing() => new() { Latest = UpdateTestKit.Release("1.29.0") };
}
