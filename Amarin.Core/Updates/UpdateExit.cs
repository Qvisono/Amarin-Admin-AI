namespace Amarin.Core;

/// <summary>Окно вокруг выхода с обновлением. Подменяется в тестах.</summary>
public interface IExitHost
{
    /// <summary>
    /// Выход этого окна — выход программы: оно главное окно приложения или приложения нет.
    /// Окно, поднятое тестом рядом с общим, закрывается само по себе и обновление не доводит.
    /// </summary>
    bool OwnsApplication { get; }

    /// <summary>
    /// Выход ради стирания данных или перезапуска от администратора: преемник ждёт этот процесс
    /// не дольше полуминуты, и доводить обновление без окна до пятнадцати минут нельзя.
    /// </summary>
    bool UpdateForbidden { get; }

    /// <summary>
    /// Уступить очередь окна. Выход зовут из самого <c>Closing</c>, и прятанье окна с завершением
    /// приложения обязаны случиться после того, как обработчик вернётся и отмена закрытия вступит в силу.
    /// </summary>
    Task YieldAsync();

    /// <summary>
    /// Спрятать окно на время фоновой работы: геометрия — до прятанья (спрятанное Windows отдаёт как
    /// «скрыто»), всё сохранено, ходы остановлены, попапы закрыты.
    /// </summary>
    void HideForBackgroundExit();

    /// <summary>Вернуть спрятанное окно: второй запуск программы.</summary>
    void ShowAgain();

    /// <summary>Запустить подменённый exe после этого процесса.</summary>
    void StartSuccessor(string exePath);

    /// <summary>Завершить приложение.</summary>
    void Shutdown();
}

/// <summary>
/// Выход из программы с обновлением: окно исчезает сразу, а процесс без окна дожидается
/// проверки, докачивает найденную версию, подменяет файл и выходит.
/// </summary>
/// <remarks>
/// <para>
/// Человек просил закрыть программу, и ждать загрузку или подмену перед экраном он не должен.
/// Сверка по размеру и SHA-256 — та же, что всегда. Новая версия сама не запускается — её
/// поднимет следующий запуск. Потолок на всё — <see cref="UpdateSchedule.ExitLimit"/>: зависшая
/// сеть не должна оставлять невидимый процесс навсегда, а отказ на любом шаге выходу не мешает.
/// </para>
/// <para>
/// Повторный запуск во время этой работы возвращает окно (<see cref="Revive"/>), а если подмена
/// уже шла — поднимает новую версию после неё.
/// </para>
/// </remarks>
public sealed class UpdateExit
{
    private readonly UpdateController _updates;
    private readonly IUpdateFiles _files;
    private readonly IExitHost _host;
    private readonly TimeProvider _time;

    public UpdateExit(UpdateController updates, IUpdateFiles files, IExitHost host, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(host);

        _updates = updates;
        _files = files;
        _host = host;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Выход уже начат — второй раз его начинать нельзя.</summary>
    public bool Exiting { get; private set; }

    /// <summary>Окно спрятано, а процесс доводит обновление, прежде чем завершиться.</summary>
    public bool HiddenForExit { get; private set; }

    /// <summary>Файл уже подменяется — вернуть окно нельзя, можно только перезапуститься.</summary>
    public bool SwapStarted { get; private set; }

    /// <summary>Во время фоновой подмены программу запустили снова — после неё поднять новую версию.</summary>
    public bool RelaunchAfterExit { get; private set; }

    /// <summary>
    /// Номер попытки выхода. Возвращённое повторным запуском окно начинает новую попытку, и прежняя,
    /// дождавшись своей загрузки, обязана тихо отступить — иначе файл подменили бы дважды.
    /// </summary>
    public int Generation { get; private set; }

    /// <summary>
    /// Обновление ещё не скачано, но его стоит довести после закрытия: идёт проверка или загрузка,
    /// проверка давняя, или найденная версия ждёт своей очереди.
    /// </summary>
    /// <remarks>
    /// Только при включённом автообновлении: снятая галка значит «ничего не делай сам», и процесс,
    /// оставшийся жить после закрытия окна, был бы именно этим.
    /// </remarks>
    public bool PendingForExit
    {
        get
        {
            var context = _updates.Context();
            var state = _updates.State;
            return context.AutoUpdate &&
                   (state.CheckRunning ||
                    UpdateSchedule.CheckDueOnExit(context.NowUtc, state.LastSuccessUtc) ||
                    state.Download is not null ||
                    (state.Latest is { WindowsBuild: not null } found && UpdateMachine.WantedAutomatically(state, found, context)));
        }
    }

    /// <summary>
    /// Надо ли отложить закрытие окна ради обновления. Отдельно от выхода: проверить решение, не
    /// погасив при этом всё приложение, можно только здесь.
    /// </summary>
    public bool ShouldDeferClose =>
        _host.OwnsApplication && !Exiting && !_updates.State.SwapDone && (_updates.State.Staged is not null || PendingForExit);

    /// <summary>
    /// Начать выход. Синхронно отмечает «выход идёт» (закрытие окна отменяется тем же обработчиком),
    /// дальше — фоновая работа.
    /// </summary>
    /// <returns>Задача доведения; окно её не ждёт.</returns>
    public Task BeginAsync()
    {
        if (Exiting)
        {
            return Task.CompletedTask;
        }

        Exiting = true;
        _updates.Dispatch(new UpdateEvent.ExitStarted());
        return FinishAsync();
    }

    /// <summary>
    /// Программу запустили снова, пока она доводила обновление без окна.
    /// </summary>
    /// <returns>
    /// <c>true</c> — окно возвращено (или не пряталось), выход отменён, загрузка продолжается обычной
    /// фоновой. <c>false</c> — файл уже подменяется: вернуть прежнюю версию нельзя, и после подмены
    /// поднимется новая.
    /// </returns>
    public bool Revive()
    {
        if (!HiddenForExit)
        {
            return true;
        }

        if (SwapStarted)
        {
            RelaunchAfterExit = true;
            return false;
        }

        Generation++;
        Exiting = false;
        HiddenForExit = false;
        _updates.Dispatch(new UpdateEvent.ExitCancelled());
        _updates.Start();
        _host.ShowAgain();
        return true;
    }

    /// <summary>
    /// Windows завершает сеанс: ждать загрузку и окно UAC некогда.
    /// </summary>
    /// <remarks>
    /// Ставится только уже скачанное и только без прав администратора — это два переименования,
    /// доли секунды. Остальное доведёт следующий запуск.
    /// </remarks>
    public void OnSessionEnding()
    {
        _updates.Dispatch(new UpdateEvent.CancelRequested());

        var state = _updates.State;
        if (SwapStarted || state.SwapDone || state.Staged is not { Plan.NeedsElevation: false } staged)
        {
            return;
        }

        SwapStarted = true;
        _updates.Dispatch(new UpdateEvent.SwapOnExitStarted());
        var swap = _files.Swap(staged);
        if (!swap.Ok)
        {
            PerfLog.Write("update_on_session_end failed " + swap.Error);
        }
    }

    private async Task FinishExitAsync(int generation, bool finishUpdate)
    {
        if (finishUpdate && NeedsMore(_updates.State))
        {
            await BringUpdateToStageAsync().ConfigureAwait(true);

            // Пока ждали, программу открыли снова — этот выход отменён.
            if (generation != Generation || !Exiting)
            {
                return;
            }
        }

        // Незаконченная загрузка уже не пригодится: всё, что успело, лежит в скачанном.
        _updates.Dispatch(new UpdateEvent.CancelRequested());

        if (finishUpdate && _updates.State.Staged is { } staged)
        {
            SwapStarted = true;
            _updates.Dispatch(new UpdateEvent.SwapOnExitStarted());
            var swap = staged.Plan.NeedsElevation
                ? await _files.SwapElevatedAsync(staged).ConfigureAwait(true)
                : await Task.Run(() => _files.Swap(staged)).ConfigureAwait(true);

            if (!swap.Ok)
            {
                PerfLog.Write("update_on_exit failed " + swap.Error);
            }

            if (RelaunchAfterExit)
            {
                _host.StartSuccessor(staged.Plan.ExePath);
            }
        }

        _host.Shutdown();
    }

    private async Task FinishAsync()
    {
        await _host.YieldAsync().ConfigureAwait(true);

        var generation = ++Generation;
        _updates.Stop();

        // После ручной установки файл уже новый — доводить нечего. Выход ради стирания данных
        // или перезапуска от администратора обновление не доводит: преемник ждёт этот процесс не
        // дольше полуминуты, а загрузка без окна длилась бы до ExitLimit.
        var state = _updates.State;
        var finishUpdate = !state.SwapDone && !_host.UpdateForbidden && (state.Staged is not null || PendingForExit);
        if (finishUpdate)
        {
            HiddenForExit = true;
            _host.HideForBackgroundExit();
        }

        await FinishExitAsync(generation, finishUpdate).ConfigureAwait(true);
    }

    /// <summary>
    /// Ждать ли ещё: скачанного нет, или качается версия новее скачанной. До 1.30.0 выход ставил
    /// скачанную v1, не дождавшись уже качавшейся v2.
    /// </summary>
    private static bool NeedsMore(UpdateState state) =>
        state.Staged is null || (state.Download is { } download && download.Version > state.Staged.Version);

    /// <summary>Ждёт проверку и загрузку, пока не наберётся готовая сборка или не выйдет срок.</summary>
    private async Task BringUpdateToStageAsync()
    {
        using var limit = new CancellationTokenSource(UpdateSchedule.ExitLimit, _time);
        try
        {
            // Проверка при запуске сорвалась или была давно — спрашиваем GitHub ещё раз, уже без
            // окна: ставить надо то, что лежит там сейчас. Только при автообновлении: снятая галка
            // значит «ничего не делай сам», и ждём тогда лишь загрузку, которую человек попросил.
            var context = _updates.Context();
            if (context.AutoUpdate &&
                !_updates.State.CheckRunning &&
                UpdateSchedule.CheckDueOnExit(context.NowUtc, _updates.State.LastSuccessUtc))
            {
                _updates.Dispatch(new UpdateEvent.CheckRequested(Manual: false));
            }

            await _updates.WaitUntilAsync(state => !state.CheckRunning, limit.Token).ConfigureAwait(true);

            // Загрузка по кнопке, увидев выход, сама откладывает скачанное, а не ставит его.
            await _updates.WaitUntilAsync(state => state.Download is null, limit.Token).ConfigureAwait(true);

            // Версия найдена, а загрузка не шла вовсе или сорвалась — пробуем ещё раз, теперь уже
            // без окна.
            if (_updates.State.Staged is null)
            {
                _updates.Dispatch(new UpdateEvent.RetryDownload());
                await _updates.WaitUntilAsync(state => state.Download is null, limit.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            PerfLog.Write("update_on_exit timed out");
        }
    }
}
