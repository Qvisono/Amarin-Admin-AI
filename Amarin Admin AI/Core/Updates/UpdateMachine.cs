namespace Amarin.Core;

/// <summary>
/// Конечный автомат обновлений: все переходы в одном месте, без сети, диска и окна.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 эти решения жили полями главного окна и расползались по обработчикам: кнопка
/// «Обновить» во время фоновой загрузки её отменяла (подписана была «Отмена»), «Применить»
/// молча ничего не делал, если за время вопроса началась фоновая загрузка, смена канала не
/// останавливала ручную загрузку беты, а выход ставил уже скачанную v1, не дождавшись v2.
/// Здесь каждое событие — одна ветка, и каждая проверяется тестом без окна.
/// </para>
/// <para>
/// Переход чистый: состояние и событие на входе, новое состояние и список эффектов на выходе.
/// Эффекты — сеть, диск, процессы — исполняет <see cref="UpdateController"/>, а их итоги
/// возвращаются сюда же событиями с номером проверки или загрузки.
/// </para>
/// </remarks>
public static class UpdateMachine
{
    public static UpdateTransition Next(UpdateState state, UpdateEvent update, UpdateContext context)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(update);

        return update switch
        {
            // Файл уже подменён: что бы ни нашла проверка, ставить это некому до перезапуска.
            UpdateEvent.CheckRequested when state.SwapDone => UpdateTransition.Stay(state),
            UpdateEvent.CheckRequested check => StartCheck(state, check.Manual),
            UpdateEvent.Heartbeat => Heartbeat(state, context),
            UpdateEvent.CheckFinished finished => CheckFinished(state, finished, context),
            UpdateEvent.UpdateConfirmed confirmed => Confirm(state, confirmed, context),
            UpdateEvent.PlanFailed failed => UpdateTransition.Stay(state with { Failure = new(failed.Version, failed.Error), Notice = null }),
            UpdateEvent.CancelRequested => Cancel(state),
            UpdateEvent.DownloadPlanned planned => UpdateTransition.Stay(
                state.Download is { } planning && planning.Generation == planned.Generation
                    ? state with { Download = planning with { Plan = planned.Plan } }
                    : state),
            UpdateEvent.DownloadProgress progress => Progress(state, progress),
            UpdateEvent.DownloadFinished finished => DownloadFinished(state, finished, context),
            UpdateEvent.DownloadCancelled cancelled => UpdateTransition.Stay(
                state.Download?.Generation == cancelled.Generation ? state with { Download = null } : state),
            UpdateEvent.SwapFinished swap => SwapFinished(state, swap),
            UpdateEvent.RestartFailed failed => UpdateTransition.Stay(state with { Installing = null, RestartError = failed.Error, Notice = null }),
            UpdateEvent.AutoUpdateChanged changed => AutoUpdateChanged(state, changed.On, context),
            UpdateEvent.ChannelChanged => ChannelChanged(state),
            UpdateEvent.RollbackStarted => Rollback(state),
            // Строка «Возвращаю прошлую версию…» остаётся до перезапуска: он и есть конец отката.
            UpdateEvent.RollbackDone => UpdateTransition.Stay(state with { SwapDone = true }),
            UpdateEvent.RetryDownload => RetryDownload(state, context),
            UpdateEvent.RollbackFailed failed => UpdateTransition.Stay(state with { Failure = new(null, failed.Error), Notice = null }),
            UpdateEvent.NoticeShown notice => UpdateTransition.Stay(state with { Notice = new(notice.Kind, notice.Detail) }),
            UpdateEvent.NoticesDismissed => UpdateTransition.Stay(state.Notice is { Transient: true } ? state with { Notice = null } : state),
            UpdateEvent.ExitStarted => UpdateTransition.Stay(state with { Exiting = true }),
            UpdateEvent.ExitCancelled => UpdateTransition.Stay(state with { Exiting = false }),
            UpdateEvent.SwapOnExitStarted => UpdateTransition.Stay(state with { Staged = null, SwapDone = true }),
            _ => UpdateTransition.Stay(state)
        };
    }

    /// <summary>
    /// Стоит ли этот выпуск доводить самим, без кнопки: новее установленной и не та версия, от
    /// которой человек вернулся к прошлой.
    /// </summary>
    public static bool WantedAutomatically(ReleaseInfo release, UpdateContext context) =>
        release.Release > context.Current &&
        (context.Declined is not { } declined || release.Release > declined);

    private static UpdateTransition StartCheck(UpdateState state, bool manual)
    {
        // Вторая проверка поверх идущей ничего бы не прибавила: кнопка «Проверить» на это время
        // и так погашена, а такт подождёт.
        if (state.CheckRunning)
        {
            return UpdateTransition.Stay(state);
        }

        // Ручная проверка — новое нажатие: ответ на прошлое («отменено», «дождитесь») уже не к месту.
        var generation = state.CheckGeneration + 1;
        return new(
            state with
            {
                CheckRunning = true,
                CheckManual = manual,
                CheckGeneration = generation,
                Notice = manual ? null : state.Notice
            },
            [new UpdateEffect.StartCheck(generation)]);
    }

    /// <summary>
    /// Такт часов. Короткий такт, а не один тик на весь интервал: таймер не досчитывает время
    /// сна и гибернации, и единственный пятичасовой тик после пробуждения сдвинулся бы ровно на
    /// столько, сколько машина спала.
    /// </summary>
    private static UpdateTransition Heartbeat(UpdateState state, UpdateContext context) =>
        !context.AutoUpdate || state.Exiting || state.SwapDone || state.CheckRunning || context.NowUtc < state.NextAutoCheckUtc
            ? UpdateTransition.Stay(state)
            : StartCheck(state, manual: false);

    private static UpdateTransition CheckFinished(UpdateState state, UpdateEvent.CheckFinished finished, UpdateContext context)
    {
        // Итог проверки, которую уже заменили (смена канала), применять нельзя: он про другой канал.
        if (!state.CheckRunning || finished.Generation != state.CheckGeneration)
        {
            return UpdateTransition.Stay(state);
        }

        var result = finished.Result;
        var now = context.NowUtc;

        // Неудачную проверку повторяем заметно раньше удачной: обрыв связи не повод молчать пять
        // часов, когда сеть вернулась через минуту.
        var next = state with
        {
            CheckRunning = false,
            CheckManual = false,
            NextAutoCheckUtc = UpdateSchedule.NextAfter(now, result.Ok),
            LastSuccessUtc = result.Ok ? now : state.LastSuccessUtc,
            Notice = null
        };
        List<UpdateEffect> effects = [new UpdateEffect.RememberCheck()];

        // Неудачная повторная проверка уже найденную версию не стирает: её файл и сумма известны,
        // и ставить её можно по-прежнему.
        if (!result.Ok || result.Latest is null)
        {
            return new(next with { LastCheckError = result.Error ?? Loc.Get("S.Updates.CheckFailed") }, effects);
        }

        next = next with { LastCheckError = null };
        if (!result.UpdateAvailable)
        {
            return new(next, effects);
        }

        // Тот же выпуск, найденный беднее (запасной путь без файла сумм), не затирает найденный
        // через API: иначе кнопка «Обновить» пропадала посреди сеанса.
        var latest = UpdateChecker.Richer(state.Latest, result.Latest);

        // Неудача прошлой загрузки после удачной проверки уже неважна: загрузка пойдёт снова.
        next = next with { Latest = latest, Failure = next.Installing is null ? null : next.Failure };

        if (context.AutoUpdate &&
            next.Download is null &&
            next.Installing is null &&
            !next.SwapDone &&
            UpdateSchedule.ShouldAutoDownload(autoUpdate: true, latest, next.Staged?.Version, context.Declined))
        {
            var (started, start) = BeginDownload(next, latest, UpdateDownloadOrigin.Background, installRequested: false, allowUnverified: false, plan: null);
            next = started;
            effects.Add(start);
        }

        return new(next, effects);
    }

    private static UpdateTransition Confirm(UpdateState state, UpdateEvent.UpdateConfirmed confirmed, UpdateContext context)
    {
        // Перезапуск оборвал бы идущие ответы — это про всю программу, а не про один чат.
        if (context.TurnsRunning)
        {
            return UpdateTransition.Stay(state with { Notice = new(UpdateNoticeKind.WaitForTurns) });
        }

        if (state.Installing is not null || state.SwapDone || state.Exiting)
        {
            return UpdateTransition.Stay(state);
        }

        var version = confirmed.Release.Release;
        var clean = state with { Notice = null, Failure = null };

        // Автообновление могло принести этот самый файл в фоне. Качать его второй раз —
        // семьдесят восемь мегабайт впустую и лишняя минута ожидания.
        if (state.Staged is { } staged && staged.Version == version)
        {
            return new(clean with { Installing = staged }, [new UpdateEffect.Install(staged)]);
        }

        // Тот же выпуск уже качается в фоне: присоединяемся к загрузке, а не обрываем её. До
        // 1.30.0 именно здесь кнопка, подписанная «Отмена», отменяла фоновую загрузку, полоса
        // пропадала, и нажимать приходилось ещё раз — уже на пустом месте.
        if (state.Download is { } running && running.Version == version)
        {
            return UpdateTransition.Stay(clean with
            {
                Download = running with { InstallRequested = true, Plan = running.Plan ?? confirmed.Plan }
            });
        }

        List<UpdateEffect> effects = [];
        if (state.Download is { } other)
        {
            effects.Add(new UpdateEffect.CancelDownload(other.Generation));
        }

        var (started, start) = BeginDownload(
            clean with { Download = null },
            confirmed.Release,
            UpdateDownloadOrigin.User,
            installRequested: true,
            confirmed.AllowUnverified,
            confirmed.Plan);
        effects.Add(start);
        return new(started, effects);
    }

    private static UpdateTransition Cancel(UpdateState state) =>
        state.Download is { } download
            ? new(
                state with { Download = null, Notice = new(UpdateNoticeKind.DownloadCancelled) },
                [new UpdateEffect.CancelDownload(download.Generation)])
            : UpdateTransition.Stay(state);

    private static UpdateTransition Progress(UpdateState state, UpdateEvent.DownloadProgress progress) =>
        // Отчёт о доле приходит очередью и может опоздать к концу загрузки.
        state.Download is { } download && download.Generation == progress.Generation
            ? UpdateTransition.Stay(state with { Download = download with { Share = Math.Clamp(progress.Share, 0, 1) } })
            : UpdateTransition.Stay(state);

    private static UpdateTransition DownloadFinished(UpdateState state, UpdateEvent.DownloadFinished finished, UpdateContext context)
    {
        if (state.Download is not { } download || download.Generation != finished.Generation)
        {
            return UpdateTransition.Stay(state);
        }

        var next = state with { Download = null };
        if (!finished.Result.Ok || finished.File is null || (finished.Plan ?? download.Plan) is not { } plan)
        {
            return UpdateTransition.Stay(next with { Failure = new(download.Version, finished.Result.Error ?? "") });
        }

        var staged = new StagedUpdate(plan, finished.File, download.Version, download.AllowUnverified);
        next = next with { Staged = staged, Failure = null };

        // Пока качали, человек закрыл программу: ставить и перезапускать уже нельзя — он просил
        // закрыть. Скачанное откладывается, и его поставит сам выход.
        if (!download.InstallRequested || state.Exiting)
        {
            return UpdateTransition.Stay(next);
        }

        // Пока качали, начался ответ. Перезапуск без спроса оборвал бы его — сборка ждёт кнопки
        // «Установить» или закрытия программы.
        if (context.TurnsRunning)
        {
            return UpdateTransition.Stay(next with { Notice = new(UpdateNoticeKind.ReadyAfterTurns) });
        }

        return new(next with { Installing = staged, Notice = null }, [new UpdateEffect.Install(staged)]);
    }

    private static UpdateTransition SwapFinished(UpdateState state, UpdateEvent.SwapFinished swap)
    {
        // Сборка остаётся «устанавливаемой» до перезапуска: окно ещё видно долю секунды, и
        // строка в нём — «Устанавливаем версию…», а не пустое «Не удалось».
        if (swap.Result.Ok)
        {
            return new(
                state with { Installing = swap.Staged, Staged = null, SwapDone = true, Failure = null, Notice = null },
                [new UpdateEffect.Restart(swap.Staged.Plan.ExePath)]);
        }

        // Отказ UAC или занятый файл: скачанное остаётся — второй попытке не нужно качать заново.
        return UpdateTransition.Stay(state with
        {
            Installing = null,
            Failure = new(swap.Staged.Version, swap.Result.Error ?? "")
        });
    }

    private static UpdateTransition AutoUpdateChanged(UpdateState state, bool on, UpdateContext context)
    {
        // Включили — проверить сразу, а не через пять часов.
        if (on)
        {
            return Heartbeat(state with { NextAutoCheckUtc = DateTime.MinValue }, context);
        }

        // Снятая галка значит «ничего не делай сам»: фоновую загрузку бросаем, уже скачанное
        // забываем — иначе оно всё равно встало бы при закрытии. Загрузку, которую человек
        // попросил кнопкой (в том числе присоединившись к фоновой), галка не трогает.
        List<UpdateEffect> effects = [];
        var download = state.Download;
        if (download is { Origin: UpdateDownloadOrigin.Background, InstallRequested: false })
        {
            effects.Add(new UpdateEffect.CancelDownload(download.Generation));
            download = null;
        }
        else if (download is { Origin: UpdateDownloadOrigin.Background, InstallRequested: true })
        {
            download = download with { Origin = UpdateDownloadOrigin.User };
        }

        return new(state with { Download = download, Staged = null }, effects);
    }

    private static UpdateTransition ChannelChanged(UpdateState state)
    {
        // Найденное по прежнему каналу забываем целиком — и скачанное, и качающееся, кем бы оно
        // ни было начато: иначе снятая галка беты всё равно поставила бы бету. Идущую проверку
        // заменяет новая с новым номером, и итог прежней отбросится.
        List<UpdateEffect> effects = [];
        if (state.Download is { } download)
        {
            effects.Add(new UpdateEffect.CancelDownload(download.Generation));
        }

        var cleared = state with
        {
            Download = null,
            Staged = null,
            Latest = null,
            Failure = null,
            Notice = null,
            LastCheckError = null,
            CheckRunning = false
        };
        var check = StartCheck(cleared, manual: true);
        return new(check.State, [.. effects, .. check.Effects]);
    }

    private static UpdateTransition Rollback(UpdateState state)
    {
        // Скачанное в фоне поставил бы выход — а выход здесь ради отката, не обновления.
        List<UpdateEffect> effects = [];
        if (state.Download is { } download)
        {
            effects.Add(new UpdateEffect.CancelDownload(download.Generation));
        }

        return new(state with { Download = null, Staged = null, Failure = null, Notice = new(UpdateNoticeKind.RollingBack) }, effects);
    }

    private static UpdateTransition RetryDownload(UpdateState state, UpdateContext context)
    {
        if (state.Download is not null || state.Installing is not null || state.SwapDone ||
            state.Latest is not { } release || !WantedAutomatically(release, context) ||
            !UpdateSchedule.ShouldAutoDownload(context.AutoUpdate, release, state.Staged?.Version, context.Declined))
        {
            return UpdateTransition.Stay(state);
        }

        var (started, start) = BeginDownload(state, release, UpdateDownloadOrigin.Background, installRequested: false, allowUnverified: false, plan: null);
        return new(started, [start]);
    }

    private static (UpdateState State, UpdateEffect Start) BeginDownload(
        UpdateState state,
        ReleaseInfo release,
        UpdateDownloadOrigin origin,
        bool installRequested,
        bool allowUnverified,
        UpdatePlan? plan)
    {
        var generation = state.DownloadGeneration + 1;
        var download = new UpdateDownload(release, origin, installRequested, allowUnverified, 0, generation, plan);
        return (state with { Download = download, DownloadGeneration = generation },
            new UpdateEffect.StartDownload(generation, release, plan, allowUnverified));
    }
}
