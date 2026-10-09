namespace Amarin.Core;

/// <summary>Чем кончился прогон задачи.</summary>
/// <param name="ChatId">Чат, куда пришёл отчёт (исходный или новый, если исходного уже не было).</param>
internal sealed record DeferredOutcome(bool Success, string? Error, decimal CostUsd, string? ChatId, string? Output = null);

/// <summary>То, что от задач нужно окну: показать карточку, выполнить в чате, сказать об итоге.</summary>
internal interface IDeferredHost
{
    /// <summary>Показать напоминание или обновить уже висящее (повторный срок, пропущенные).</summary>
    void ShowReminder(DeferredTask task, TimeSpan? late);

    /// <summary>Задача начала выполняться в прошлый раз и оборвалась: спросить, повторять ли.</summary>
    void ShowInterrupted(DeferredTask task);

    /// <summary>
    /// Выполнить задачу агента, команды или возврата программ — в её чате. Null — сейчас нельзя
    /// (чат отвечает, нет свободного места для хода): задача подождёт следующей проверки.
    /// </summary>
    Task<DeferredOutcome?> RunAsync(DeferredTask task, TimeSpan? late, CancellationToken cancellationToken);

    /// <summary>Задача закончилась: карточка с мелодией.</summary>
    void Completed(DeferredTask task, DeferredOutcome outcome);
}

/// <summary>Куда отложенные задачи просят Windows разбудить программу.</summary>
internal interface IDeferredWake
{
    /// <summary>Есть ли что ждать и к какому ближайшему моменту запустить программу.</summary>
    void Reconcile(bool pending, DateTime? dueUtc);
}

/// <summary>
/// Проверяет сроки отложенных задач и выполняет наступившие.
/// </summary>
/// <remarks>
/// <para>
/// Таймер не минутный, а ставится на ближайший срок (но не дальше минуты): напоминание на 18:30
/// приходит в 18:30, а не в 18:30:59. Пробуждение после сна и смена времени системы будят проверку
/// сразу (<see cref="Poke"/>, <see cref="TimeChanged"/>).
/// </para>
/// <para>
/// Ничего не теряется молча. Перед запуском задача помечается «выполняется» и записывается на диск:
/// если программу закроют или выключат ПК посреди прогона, при следующем запуске задача
/// окажется «прервалась», и человек решит, повторять ли, — запись в систему без человека дважды
/// не повторяется. Неподтверждённое напоминание при запуске встаёт снова. Пропущенный срок
/// срабатывает один раз с пометкой «опоздало на …».
/// </para>
/// <para>
/// Прогоны агента и команд идут каждый своим ходом и проверку не держат: агент может работать
/// час, а напоминание за это время должно прийти вовремя.
/// </para>
/// </remarks>
internal sealed class DeferredRunner : IDisposable
{
    /// <summary>Дальше этого таймер не спит: часы могли перевести, а ПК — усыпить и разбудить.</summary>
    internal static readonly TimeSpan MaxSleep = TimeSpan.FromMinutes(1);

    private readonly DeferredBook _book;
    private readonly IDeferredHost _host;
    private readonly IDeferredWake _wake;
    private readonly Func<DeferredFacts> _facts;
    private readonly Func<string, CancellationToken, Task<bool?>> _probe;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _running = [];
    private readonly Lock _gate = new();
    private Timer? _timer;
    private bool _recovered;

    /// <param name="probe">Проверка условия: скрипт только на чтение; null — не выяснилось.</param>
    public DeferredRunner(
        DeferredBook book,
        IDeferredHost host,
        IDeferredWake wake,
        Func<string, CancellationToken, Task<bool?>> probe,
        Func<DeferredFacts>? facts = null)
    {
        _book = book;
        _host = host;
        _wake = wake;
        _probe = probe;
        _facts = facts ?? DeferredClock.Now;
        _book.Changed += Poke;
    }

    /// <summary>Запускает проверки; первая — через <paramref name="firstDelay"/>, когда запуск программы уже позади.</summary>
    public void Start(TimeSpan firstDelay)
    {
        lock (_gate)
        {
            _timer ??= new Timer(_ => _ = TickQuietlyAsync(), null, firstDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Проверить сроки прямо сейчас: список изменился, ПК проснулся, нажали «Выполнить сейчас».</summary>
    public void Poke()
    {
        lock (_gate)
        {
            _ = _timer?.Change(TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Сменились часы или часовой пояс системы: «каждый день в 9:00» пересчитывается.</summary>
    public void TimeChanged()
    {
        var facts = _facts();
        foreach (var task in _book.Snapshot().Where(task => task.Repeat.Kind is DeferredRepeatKind.Daily or DeferredRepeatKind.Weekly))
        {
            _book.Update(task.Id, stored => DeferredClock.Recompute(stored, facts));
        }

        Poke();
    }

    /// <summary>Книга переехала в другой профиль: прерванное и неподтверждённое там тоже поднимается.</summary>
    public void Reload()
    {
        _recovered = false;
        Poke();
    }

    // ───────────────────────── действия человека ─────────────────────────

    /// <summary>«Готово» на карточке напоминания.</summary>
    public void Acknowledge(string id) => _book.Update(id, task =>
    {
        task.AwaitingAckSinceUtc = null;
        task.MissedWhileUnacked = 0;
    });

    /// <summary>«Отложить»: этот срок переносится, следующий регулярный — по-прежнему.</summary>
    public void Snooze(string id, DateTime untilUtc) => _book.Update(id, task =>
    {
        task.AwaitingAckSinceUtc = null;
        task.MissedWhileUnacked = 0;
        task.SnoozedUntilUtc = untilUtc;
        if (task.Status is DeferredStatus.Done)
        {
            task.Status = DeferredStatus.Pending;
        }
    });

    /// <summary>«Выполнить сейчас» и «Повторить» у прерванной: срок — сейчас, остальное как было.</summary>
    public void RunNow(string id) => _book.Update(id, task =>
    {
        if (task.Status is DeferredStatus.Running || _running.Contains(task.Id))
        {
            return;
        }

        task.Status = DeferredStatus.Pending;
        task.ConsecutiveFailures = 0;
        task.SnoozedUntilUtc = _facts().NowUtc;
    });

    public void Cancel(string id) => _book.Update(id, task =>
    {
        if (task.Status != DeferredStatus.Running)
        {
            task.Status = DeferredStatus.Cancelled;
            task.AwaitingAckSinceUtc = null;
        }
    });

    /// <summary>Повторяющаяся задача, остановленная после неудач, — снова в работе.</summary>
    public void Resume(string id) => _book.Update(id, task =>
    {
        if (task.Status == DeferredStatus.Paused)
        {
            task.Status = DeferredStatus.Pending;
            task.ConsecutiveFailures = 0;
            if (task.Trigger.Kind == DeferredTriggerKind.At && task.NextDueUtc < _facts().NowUtc)
            {
                DeferredClock.Rearm(task, _facts());
            }
        }
    });

    // ───────────────────────── проверка ─────────────────────────

    private async Task TickQuietlyAsync()
    {
        try
        {
            await TickAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Сбой одной проверки не повод гасить задачи: следующая — через минуту.
            PerfLog.Write($"deferred_tick_failed {ex.GetType().Name}");
        }
        finally
        {
            Rearm();
        }
    }

    /// <summary>Одна проверка: поднять прерванное, выполнить наступившее, проверить условия.</summary>
    internal async Task TickAsync()
    {
        if (!await _busy.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var facts = _facts();
            if (!_recovered)
            {
                _recovered = true;
                Recover();
            }

            foreach (var task in _book.Snapshot())
            {
                if (_stop.IsCancellationRequested)
                {
                    return;
                }

                if (BackingOff(task.Id, facts.NowUtc))
                {
                    continue;
                }

                if (DeferredClock.IsDue(task, facts))
                {
                    await FireAsync(task, facts).ConfigureAwait(false);
                }
                else if (DeferredClock.NeedsProbe(task, facts))
                {
                    await ProbeAsync(task, facts).ConfigureAwait(false);
                }
            }

            var pending = _book.Snapshot();
            _wake.Reconcile(pending.Any(task => task.Status == DeferredStatus.Pending), DeferredClock.WakeAtUtc(pending, _facts()));
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>
    /// При запуске (и смене профиля): то, что выполнялось, когда программа закрылась, — «прервалась»;
    /// неподтверждённые напоминания — снова на экран.
    /// </summary>
    private void Recover()
    {
        foreach (var task in _book.Snapshot())
        {
            if (task.Status == DeferredStatus.Running && !IsRunningHere(task.Id))
            {
                _book.Update(task.Id, stored => stored.Status = DeferredStatus.Interrupted);
                _host.ShowInterrupted(_book.Peek(task.Id) ?? task);
            }
            else if (task.Status == DeferredStatus.Interrupted)
            {
                _host.ShowInterrupted(task);
            }
            else if (task.Kind == DeferredKind.Reminder && task.AwaitingAckSinceUtc is not null)
            {
                _host.ShowReminder(task, null);
            }
        }
    }

    private async Task ProbeAsync(DeferredTask task, DeferredFacts facts)
    {
        bool? holds;
        try
        {
            holds = await _probe(task.Trigger.Condition ?? "", _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            holds = null;
        }

        var edge = false;
        _book.Update(task.Id, stored =>
        {
            stored.LastProbeUtc = facts.NowUtc;
            if (holds is { } value)
            {
                edge = value && !stored.ConditionWasTrue;
                stored.ConditionWasTrue = value;
            }
        });

        if (edge && _book.Peek(task.Id) is { } fresh)
        {
            await FireAsync(fresh, facts).ConfigureAwait(false);
        }
    }

    private async Task FireAsync(DeferredTask task, DeferredFacts facts)
    {
        var late = DeferredClock.Lateness(task, facts);
        if (task.Kind == DeferredKind.Reminder)
        {
            _book.Update(task.Id, stored =>
            {
                if (stored.AwaitingAckSinceUtc is null)
                {
                    stored.AwaitingAckSinceUtc = facts.NowUtc;
                }
                else
                {
                    stored.MissedWhileUnacked++;
                }

                if (!DeferredClock.Rearm(stored, facts))
                {
                    stored.Status = DeferredStatus.Done;
                }
            });
            _host.ShowReminder(_book.Peek(task.Id) ?? task, late);
            return;
        }

        lock (_gate)
        {
            if (!_running.Add(task.Id))
            {
                return;
            }
        }

        // Отметка «выполняется» — на диск до запуска: обрыв посреди прогона не пройдёт молча.
        _book.Update(task.Id, stored =>
        {
            stored.Status = DeferredStatus.Running;
            stored.LastAttemptUtc = facts.NowUtc;
        });

        // Прогон идёт сам по себе: агент может работать час, а проверка сроков — нет.
        _ = Task.Run(() => RunAsync(task, late));
    }

    private async Task RunAsync(DeferredTask task, TimeSpan? late)
    {
        DeferredOutcome? outcome;
        try
        {
            outcome = await _host.RunAsync(task, late, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Программу закрывают: задача останется «выполняется» и при следующем запуске
            // станет «прервалась» — решать человеку.
            Forget(task.Id);
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            outcome = new DeferredOutcome(false, ex.Message, 0m, null);
        }

        if (outcome is null)
        {
            // Сейчас нельзя (чат отвечает, мест для хода нет) — попытка не в счёт, повтор через
            // полминуты, а не на каждой проверке: просроченный срок иначе гонял бы её без передышки.
            lock (_gate)
            {
                _backoff[task.Id] = _facts().NowUtc + BusyRetry;
            }

            _book.Update(task.Id, stored => stored.Status = DeferredStatus.Pending);
            Forget(task.Id);
            return;
        }

        Finish(task.Id, outcome);
    }

    /// <summary>Через сколько повторить задачу, которую не пустил занятый чат.</summary>
    internal static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(30);

    /// <summary>Задачи, которые ждут повтора после «занято», — до какого момента.</summary>
    private readonly Dictionary<string, DateTime> _backoff = [];

    private bool BackingOff(string id, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_backoff.TryGetValue(id, out var until))
            {
                return false;
            }

            if (nowUtc < until)
            {
                return true;
            }

            _backoff.Remove(id);
            return false;
        }
    }

    /// <summary>Записывает итог прогона: следующий срок, неудачи подряд, траты, отчёт.</summary>
    private void Finish(string id, DeferredOutcome outcome)
    {
        var facts = _facts();
        _book.Update(id, stored =>
        {
            stored.CostUsd += outcome.CostUsd;
            stored.Output = outcome.Output;
            stored.Error = outcome.Success ? null : outcome.Error;
            stored.ResultChatId = outcome.ChatId ?? stored.ResultChatId;
            stored.ConsecutiveFailures = outcome.Success ? 0 : stored.ConsecutiveFailures + 1;
            var repeats = DeferredClock.Rearm(stored, facts);
            stored.Status = !repeats
                ? outcome.Success ? DeferredStatus.Done : DeferredStatus.Failed
                : stored.ConsecutiveFailures >= DeferredLimits.MaxConsecutiveFailures ? DeferredStatus.Paused : DeferredStatus.Pending;
        });

        Forget(id);
        if (_book.Peek(id) is { } finished)
        {
            _host.Completed(finished, outcome);
        }
    }

    private bool IsRunningHere(string id)
    {
        lock (_gate)
        {
            return _running.Contains(id);
        }
    }

    private void Forget(string id)
    {
        lock (_gate)
        {
            _running.Remove(id);
        }
    }

    /// <summary>Ставит таймер на ближайший срок, но не дальше <see cref="MaxSleep"/>.</summary>
    private void Rearm()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        var facts = _facts();
        var sleep = MaxSleep;
        foreach (var task in _book.Snapshot())
        {
            if (DeferredClock.WakeAtUtc([task], facts) is not { } due)
            {
                continue;
            }

            // Задача, которую не пустил занятый чат, ждёт своей передышки, а не будит таймер сразу.
            lock (_gate)
            {
                if (_backoff.TryGetValue(task.Id, out var retry) && retry > due)
                {
                    due = retry;
                }
            }

            var until = due - facts.NowUtc;
            if (until < sleep)
            {
                sleep = until < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : until;
            }
        }

        lock (_gate)
        {
            _ = _timer?.Change(sleep, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _book.Changed -= Poke;
        _stop.Cancel();
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
