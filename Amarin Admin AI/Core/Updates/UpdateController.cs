namespace Amarin.Core;

/// <summary>Что показать человеку по кнопке «Обновить»: вопрос с планом или ничего.</summary>
/// <param name="Release">Выпуск, о котором вопрос.</param>
/// <param name="Plan">Куда и как он встанет — это показывается в вопросе.</param>
public sealed record UpdateOffer(ReleaseInfo Release, UpdatePlan Plan);

/// <summary>
/// Драйвер автомата обновлений: подаёт события в <see cref="UpdateMachine"/> и исполняет его
/// эффекты — проверку, загрузку, подмену, перезапуск — через порты.
/// </summary>
/// <remarks>
/// <para>
/// Живёт на одном потоке — потоке окна. Команды зовутся оттуда, а итоги сетевых шагов
/// возвращаются туда же через <c>post</c> (у окна это очередь диспетчера, в тестах — ручная
/// очередь). Замка нет намеренно: эффекты исполняются строго в порядке переходов, и «отменить»
/// не может обогнать «начать».
/// </para>
/// <para>
/// Событие, поданное изнутри эффекта, не исполняется вложенно, а встаёт в очередь: переход
/// всегда видит состояние, которое оставил предыдущий.
/// </para>
/// </remarks>
public sealed class UpdateController : IDisposable
{
    /// <summary>
    /// Срок проверки. Тридцать секунд, а не двадцать: из них API достаётся только десять
    /// (<see cref="UpdateChecker.ApiTimeout"/>), остальное — запасному пути через github.com.
    /// </summary>
    internal static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Сколько загрузка может стоять без единого байта, прежде чем её признают зависшей.
    /// </summary>
    /// <remarks>
    /// У тела загрузки своего срока нет: клиент меряет только ожидание заголовков, а дальше
    /// зависшую без обрыва сеть держала бы лишь отмена — или потолок выхода в пятнадцать минут.
    /// Сторож взводится первым отчётом о доле: без известного размера отчётов нет вовсе, и
    /// здоровую загрузку он не тронет.
    /// </remarks>
    internal static readonly TimeSpan StallLimit = TimeSpan.FromMinutes(2);

    private readonly IUpdateSource _source;
    private readonly IUpdateFiles _files;
    private readonly IUpdateApp _app;
    private readonly ReleaseVersion _current;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly Queue<UpdateEvent> _pending = new();
    private bool _dispatching;
    private ITimer? _heartbeat;
    private DownloadRun? _download;

    /// <param name="post">
    /// Как вернуть итог фоновой работы на поток окна. Порядок обязан сохраняться: очередь
    /// диспетчера внутри одного приоритета строгая.
    /// </param>
    public UpdateController(
        IUpdateSource source,
        IUpdateFiles files,
        IUpdateApp app,
        ReleaseVersion current,
        Action<Action> post,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(post);

        _source = source;
        _files = files;
        _app = app;
        _current = current;
        _post = post;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Всё, что известно об обновлениях сейчас.</summary>
    public UpdateState State { get; private set; } = UpdateState.Initial;

    /// <summary>Состояние поменялось — плашку пора перерисовать. Зовётся на потоке окна.</summary>
    public event Action? Changed;

    /// <summary>Установленная версия.</summary>
    public ReleaseVersion Current => _current;

    /// <summary>Галка «Автообновление» — то, что видит автомат.</summary>
    public bool AutoUpdate => _app.AutoUpdate;

    /// <summary>
    /// Запуск: проверка сразу — при каждом запуске, а не по сроку от прошлой, — и дальше по такту.
    /// </summary>
    /// <remarks>
    /// До 1.26.0 здесь считалось пять часов от прошлой проверки, и человек, открывавший программу
    /// раз в день, узнавал о новой версии через запуск; обновление при закрытии поэтому часто
    /// просто не успевало найтись.
    /// </remarks>
    public void Start()
    {
        _heartbeat ??= _time.CreateTimer(
            _ => _post(() => Dispatch(new UpdateEvent.Heartbeat())),
            null,
            UpdateSchedule.Heartbeat,
            UpdateSchedule.Heartbeat);
        Dispatch(new UpdateEvent.Heartbeat());
    }

    /// <summary>Остановить такт: окно закрывается или программа выходит.</summary>
    public void Stop()
    {
        _heartbeat?.Dispose();
        _heartbeat = null;
    }

    /// <summary>Кнопка «Проверить».</summary>
    public void CheckNow() => Dispatch(new UpdateEvent.CheckRequested(Manual: true));

    /// <summary>
    /// Кнопка «Обновить» или «Установить сейчас»: что спросить у человека.
    /// </summary>
    /// <returns>Вопрос с планом; null — спрашивать нечего (нет версии, идут ответы, план не сложился — это уже на плашке).</returns>
    public UpdateOffer? Prepare()
    {
        if (State.Latest is not { } release)
        {
            return null;
        }

        // Тоже про всю программу: после установки она перезапустится и оборвёт все ходы.
        if (_app.TurnsRunning)
        {
            Dispatch(new UpdateEvent.NoticeShown(UpdateNoticeKind.WaitForTurns));
            return null;
        }

        if (State.Staged is { } staged && staged.Version == release.Release)
        {
            return new UpdateOffer(release, staged.Plan);
        }

        if (State.Download is { Plan: { } plan } download && download.Version == release.Release)
        {
            return new UpdateOffer(release, plan);
        }

        if (!_files.TryPlan(release, out var fresh, out var error))
        {
            Dispatch(new UpdateEvent.PlanFailed(release.Release, error));
            return null;
        }

        return new UpdateOffer(release, fresh);
    }

    /// <summary>«Применить» в вопросе: поставить, присоединиться к загрузке или начать её.</summary>
    /// <param name="allowUnverified">Сборка без суммы, и человек дважды согласился.</param>
    public void Confirm(UpdateOffer offer, bool allowUnverified = false)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Dispatch(new UpdateEvent.UpdateConfirmed(offer.Release, offer.Plan, allowUnverified));
    }

    /// <summary>Отменить загрузку, которую ждёт человек.</summary>
    public void Cancel() => Dispatch(new UpdateEvent.CancelRequested());

    /// <summary>Галка «Автообновление» поменялась; настройка уже записана.</summary>
    public void SetAutoUpdate(bool on) => Dispatch(new UpdateEvent.AutoUpdateChanged(on));

    /// <summary>Бета-канал поменялся; настройка уже записана.</summary>
    public void ChangeChannel() => Dispatch(new UpdateEvent.ChannelChanged());

    /// <summary>Пошёл возврат к прошлой версии.</summary>
    public void BeginRollback() => Dispatch(new UpdateEvent.RollbackStarted());

    /// <summary>Прошлая версия встала на место программы.</summary>
    public void CompleteRollback() => Dispatch(new UpdateEvent.RollbackDone());

    /// <summary>Возврат не удался.</summary>
    public void FailRollback(string error) => Dispatch(new UpdateEvent.RollbackFailed(error));

    /// <summary>Файл подменён (обновлением или откатом), а запустить новую версию не вышло.</summary>
    public void ReportRestartFailed(string error) => Dispatch(new UpdateEvent.RestartFailed(error));

    /// <summary>Разовая строка на плашке.</summary>
    public void ShowNotice(UpdateNoticeKind kind, string? detail = null) => Dispatch(new UpdateEvent.NoticeShown(kind, detail));

    /// <summary>Страницу открыли заново: снять ответы на прошлые нажатия.</summary>
    public void DismissNotices() => Dispatch(new UpdateEvent.NoticesDismissed());

    /// <summary>
    /// Ждёт, пока состояние не станет таким, как нужно, — или пока не выйдет срок.
    /// </summary>
    /// <remarks>
    /// Выход ждёт не задачи, а состояние: итог задачи ещё должен дойти очередью до автомата, и
    /// «задача кончилась» не значит «скачанное уже записано в состояние».
    /// </remarks>
    public Task WaitUntilAsync(Func<UpdateState, bool> condition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (condition(State))
        {
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (condition(State))
            {
                Changed -= Check;
                done.TrySetResult();
            }
        }

        Changed += Check;

        // Снять подписку можно с любого потока (подписка на событие атомарна), а срок обязан
        // сработать, даже если очередь окна сейчас никто не разбирает.
        var registration = cancellationToken.Register(() =>
        {
            Changed -= Check;
            done.TrySetCanceled(cancellationToken);
        });
        _ = done.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return done.Task;
    }

    /// <summary>
    /// Подать событие. Только с потока окна; поданное изнутри эффекта встаёт в очередь.
    /// </summary>
    internal void Dispatch(UpdateEvent update)
    {
        _pending.Enqueue(update);
        if (_dispatching)
        {
            return;
        }

        _dispatching = true;
        var changed = false;
        try
        {
            while (_pending.TryDequeue(out var next))
            {
                var transition = UpdateMachine.Next(State, next, Context());
                if (transition.State != State)
                {
                    State = transition.State;
                    changed = true;
                }

                foreach (var effect in transition.Effects)
                {
                    Run(effect);
                }
            }
        }
        finally
        {
            _dispatching = false;
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Подставить состояние целиком. Только для тестов: проверить, как плашка рисует найденную
    /// или скачанную версию, можно лишь подсунув её.
    /// </summary>
    internal void Seed(Func<UpdateState, UpdateState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        State = change(State);
        Changed?.Invoke();
    }

    /// <summary>Что переходу нужно знать о мире прямо сейчас.</summary>
    internal UpdateContext Context() =>
        new(_current, _app.AutoUpdate, _app.Declined, _app.TurnsRunning, _time.GetUtcNow().UtcDateTime);

    public void Dispose()
    {
        Stop();
        _download?.Dispose();
        _download = null;
    }

    private void Run(UpdateEffect effect)
    {
        switch (effect)
        {
            case UpdateEffect.StartCheck check:
                Watch(CheckAsync(check.Generation, _app.Beta), "update_check");
                break;

            case UpdateEffect.StartDownload download:
                StartDownload(download);
                break;

            case UpdateEffect.CancelDownload cancel:
                if (_download is { } running && running.Generation == cancel.Generation)
                {
                    running.Cancel();
                }

                break;

            case UpdateEffect.Install install:
                Watch(InstallAsync(install.Staged), "update_install");
                break;

            case UpdateEffect.Restart restart:
                // Перезапуск зовёт выход окна, а тот подаёт событие сам — оно встанет в очередь.
                if (_app.RestartInto(restart.ExePath) is { } error)
                {
                    Dispatch(new UpdateEvent.RestartFailed(error));
                }

                break;

            case UpdateEffect.RememberCheck:
                _app.RememberCheck(_time.GetUtcNow().UtcDateTime);
                break;
        }
    }

    private async Task CheckAsync(int generation, bool beta)
    {
        UpdateCheckResult result;
        using var timeout = new CancellationTokenSource(CheckTimeout, _time);
        try
        {
            result = await _source.CheckAsync(_current, beta, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Свой срок — обычный отказ, а не авария: без этого повтор через полчаса не
            // назначался бы, а выход не узнал бы, что проверка кончилась.
            result = UpdateCheckResult.Failed(Loc.Get("S.Updates.Timeout"));
        }
        catch (HttpRequestException ex)
        {
            result = UpdateCheckResult.Failed(Loc.Format("S.Updates.NoConnection", ex.Message));
        }

        _post(() => Dispatch(new UpdateEvent.CheckFinished(generation, result)));
    }

    private void StartDownload(UpdateEffect.StartDownload start)
    {
        var plan = start.Plan;
        if (plan is null && !_files.TryPlan(start.Release, out plan, out var error))
        {
            _post(() => Dispatch(new UpdateEvent.DownloadFinished(start.Generation, UpdateStepResult.Failed(error), null, null)));
            return;
        }

        _download?.Dispose();
        var run = new DownloadRun(start.Generation);
        _download = run;

        // План — в состояние: по нему вопрос «Обновить» показывает, куда встанет файл, и не
        // планирует заново посреди загрузки.
        if (start.Plan is null)
        {
            Dispatch(new UpdateEvent.DownloadPlanned(start.Generation, plan));
        }

        Watch(DownloadAsync(run, plan, start.AllowUnverified), "update_download");
    }

    private async Task DownloadAsync(DownloadRun run, UpdatePlan plan, bool allowUnverified)
    {
        var progress = new Reporter(share => _post(() =>
        {
            ArmStallWatch(run);
            Dispatch(new UpdateEvent.DownloadProgress(run.Generation, share));
        }));

        UpdateEvent finished;
        try
        {
            var (result, file) = await _files.DownloadAsync(plan, progress, allowUnverified, run.Token).ConfigureAwait(false);
            finished = new UpdateEvent.DownloadFinished(run.Generation, result, file, plan);
        }
        catch (OperationCanceledException)
        {
            finished = run.Stalled
                ? new UpdateEvent.DownloadFinished(run.Generation, UpdateStepResult.Failed(Loc.Get("S.Updates.Stalled")), null, plan)
                : new UpdateEvent.DownloadCancelled(run.Generation);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            // Порт обещает отвечать отказом, а не исключением; если оно всё же вылетело, загрузка
            // обязана кончиться отказом — иначе плашка застыла бы на «Скачивание» навсегда.
            finished = new UpdateEvent.DownloadFinished(run.Generation, UpdateStepResult.Failed(ex.Message), null, plan);
        }

        _post(() =>
        {
            if (ReferenceEquals(_download, run))
            {
                _download = null;
            }

            run.Dispose();
            Dispatch(finished);
        });
    }

    /// <summary>Каждый отчёт о доле отодвигает сторож зависания.</summary>
    private void ArmStallWatch(DownloadRun run)
    {
        if (!ReferenceEquals(_download, run))
        {
            return;
        }

        run.Watch ??= _time.CreateTimer(
            _ => _post(() =>
            {
                if (ReferenceEquals(_download, run))
                {
                    run.Stalled = true;
                    run.Cancel();
                }
            }),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        run.Watch.Change(StallLimit, Timeout.InfiniteTimeSpan);
    }

    private async Task InstallAsync(StagedUpdate staged)
    {
        // Чат сохраняем до подмены: дальше процесс уже завершается.
        _app.BeforeSwap();

        // Подмена сверяет сумму всего файла ещё раз — это доли секунды на восьмидесяти
        // мегабайтах, и потоку окна их ждать незачем.
        var result = staged.Plan.NeedsElevation
            ? await _files.SwapElevatedAsync(staged).ConfigureAwait(false)
            : await Task.Run(() => _files.Swap(staged)).ConfigureAwait(false);

        _post(() => Dispatch(new UpdateEvent.SwapFinished(staged, result)));
    }

    /// <summary>Брошенная задача, упавшая неожиданно, — в журнал аварий, а не в никуда.</summary>
    private static void Watch(Task task, string what) =>
        _ = task.ContinueWith(
            failed => CrashLog.Write($"detached {what}: {failed.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Одна загрузка: её отмена и сторож зависания.</summary>
    private sealed class DownloadRun(int generation) : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        public int Generation { get; } = generation;

        public CancellationToken Token => _cancellation.Token;

        public ITimer? Watch { get; set; }

        /// <summary>Отменена сторожем, а не человеком: итог — отказ «загрузка остановилась».</summary>
        public bool Stalled { get; set; }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Загрузка уже кончилась и прибрана.
            }
        }

        public void Dispose()
        {
            Watch?.Dispose();
            _cancellation.Dispose();
        }
    }

    /// <summary>
    /// Отчёт о доле без захвата контекста: <see cref="Progress{T}"/> сам ищет контекст потока, а
    /// здесь возврат на поток окна делает <c>post</c> — один путь на все итоги.
    /// </summary>
    private sealed class Reporter(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
