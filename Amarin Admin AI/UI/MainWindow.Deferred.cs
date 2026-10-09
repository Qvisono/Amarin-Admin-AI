using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;
using Microsoft.Win32;

namespace Amarin.UI
{
    /// <summary>
    /// Отложенные задачи в окне: проверка сроков, карточки напоминаний, прогоны в чате задачи.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Агент и команда идут ходом <see cref="TurnKind.Deferred"/> в чате, где задачу поставили: так
    /// они наследуют всё, что положено ходу, — живую отрисовку открытого чата, запись через
    /// <see cref="ChatTurnRouter"/>, запрет смены профиля и выхода с обновлением посреди хода.
    /// Занятый чат или все места под ходы — не отказ, а «позже»: задача подождёт полминуты.
    /// </para>
    /// <para>
    /// Напоминание — своя карточка (<see cref="ReminderToast"/>), а не <c>Notify</c>: у системного
    /// уведомления трея нет кнопок «Готово» и «Отложить». Итог прогона — обычным <c>Notify</c>.
    /// </para>
    /// </remarks>
    public partial class MainWindow : IDeferredHost
    {
        /// <summary>Как часто, пока ждёт задача «вернуть программы», записывать открытые программы.</summary>
        internal static readonly TimeSpan ProgramsSnapshotEvery = TimeSpan.FromMinutes(5);

        private DeferredRunner? _deferredRunner;
        private ReminderStack? _reminders;
        private SchtasksDeferredWake? _deferredWake;
        private DispatcherTimer? _programsSnapshot;

        /// <summary>Проверка сроков; null — до первого кадра и в окне без служб.</summary>
        internal DeferredRunner? DeferredRunner => _deferredRunner;

        /// <summary>Стопка карточек напоминаний; окно стопки создаётся с первой карточкой.</summary>
        internal ReminderStack Reminders => _reminders ??= CreateReminders();

        /// <summary>Почему Планировщик не завёл запуск к сроку; null — завёл или не просили.</summary>
        internal string? DeferredWakeProblem => _deferredWake?.Problem;

        /// <summary>Заводит проверку сроков. Зовётся после первого кадра.</summary>
        /// <param name="inTests">
        /// Оконные тесты поднимают то же окно, и сами по себе сроки в них не проверяются: задача,
        /// заготовленная для снимка, иначе сработала бы и выставила карточку в угол настоящего
        /// экрана. Тест, которому проверка нужна, заводит её явно.
        /// </param>
        internal void StartDeferred(bool inTests = false)
        {
            if (_services is null || _deferredRunner is not null || (!IsRealApp && !inTests))
            {
                return;
            }

            // Планировщик — только настоящей программе: оконные тесты поднимают то же окно, и
            // иначе завели бы задачу Windows, запускающую тестовый exe.
            _deferredWake = IsRealApp ? CreateWake() : null;
            if (_deferredWake is { } wake)
            {
                wake.ProblemChanged += () => Later(RefreshDeferredPanel);
            }

            var services = _services;
            _deferredRunner = new DeferredRunner(
                services.Deferred,
                this,
                (IDeferredWake?)_deferredWake ?? NoDeferredWake.Instance,
                (script, token) => DeferredCommands.ProbeAsync(script, services.RunTools(), services.Settings, token));
            services.Deferred.Changed += OnDeferredChanged;
            SystemEvents.PowerModeChanged += OnDeferredPowerModeChanged;
            SystemEvents.TimeChanged += OnDeferredTimeChanged;
            Closed += (_, _) => StopDeferred();

            // Через три секунды, а не сразу: первые секунды после окна — фоновая компиляция и
            // достройка списка, а просроченное напоминание подождёт их без вреда.
            _deferredRunner.Start(TimeSpan.FromSeconds(3));
            Detached.Run(WatchProgramsAfterLoadAsync(services.Deferred), "deferred_start");
        }

        /// <summary>
        /// Книгу задач с диска читает рабочий поток: поток окна сразу после первого кадра занят
        /// достройкой списка чатов, а файл задач ему для этого не нужен.
        /// </summary>
        private async Task WatchProgramsAfterLoadAsync(DeferredBook book)
        {
            await Task.Run(book.Snapshot);
            UpdateProgramsSnapshot();
        }

        private void StopDeferred()
        {
            SystemEvents.PowerModeChanged -= OnDeferredPowerModeChanged;
            SystemEvents.TimeChanged -= OnDeferredTimeChanged;
            if (_services is { } services)
            {
                services.Deferred.Changed -= OnDeferredChanged;
            }

            _programsSnapshot?.Stop();
            _deferredRunner?.Dispose();
            _deferredRunner = null;
            _reminders?.Clear();
        }

        private static SchtasksDeferredWake? CreateWake()
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            var exe = Environment.ProcessPath;
            if (sid is null || exe is null)
            {
                return null;
            }

            // Отметка поставленного — дело машины, как и профиль JIT: рядом с ним.
            var marker = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmarinAdminAI", "deferred-wake");
            return new SchtasksDeferredWake(exe, sid, marker);
        }

        /// <summary>Сменился профиль: его задачи, его карточки, его Планировщик.</summary>
        private void OnDeferredProfileChanged()
        {
            _reminders?.Clear();
            _deferredRunner?.Reload();
            RefreshDeferredPanel();
            UpdateProgramsSnapshot();
        }

        private void OnDeferredPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                _deferredRunner?.Poke();
            }
        }

        private void OnDeferredTimeChanged(object? sender, EventArgs e) => _deferredRunner?.TimeChanged();

        /// <summary>Книга задач изменилась — из чата, из прогона или с вкладки.</summary>
        private void OnDeferredChanged() => Later(() =>
        {
            RefreshDeferredPanel();
            DropStaleReminders();
            UpdateProgramsSnapshot();
        });

        /// <summary>
        /// На поток окна, не дожидаясь: книга и проверка сроков зовут окно со своих потоков и ждать
        /// его им незачем.
        /// </summary>
        private void Later(Action action) => _ = Dispatcher.BeginInvoke(action, DispatcherPriority.Background);

        // ───────────────────────── карточки ─────────────────────────

        private ReminderStack CreateReminders()
        {
            var stack = new ReminderStack(() => _services?.Settings.UiScalePercent ?? 100, OwnHandle);
            stack.Acted += OnReminderAction;
            return stack;
        }

        /// <summary>Карточка «ответ готов» внизу — стопка напоминаний встаёт над ней.</summary>
        private void KeepRemindersAbove(NotificationToast? toast)
        {
            if (_reminders is null)
            {
                return;
            }

            // Поля карточек под тень перекрываются: у стопки снизу своё поле, у карточки — своё.
            _reminders.BottomInset = toast is { IsVisible: true, ActualHeight: > 0 } ? Math.Max(0, toast.ActualHeight - 30) : 0;
        }

        void IDeferredHost.ShowReminder(DeferredTask task, TimeSpan? late) => Later(() =>
        {
            Reminders.Put(task.Id, ReminderLook.Reminder, item =>
            {
                item.Late = late ?? item.Late;
                FillReminder(item, _services?.Deferred.Peek(task.Id) ?? task);
            });
            _ = ChimePlayer.Play(ChimeTune.Reminder, _services?.Settings.DeferredSound == true);
        });

        void IDeferredHost.ShowInterrupted(DeferredTask task) => Later(() =>
            Reminders.Put(task.Id, ReminderLook.Interrupted, item => FillInterrupted(item, task)));

        private void FillReminder(ReminderItem item, DeferredTask task)
        {
            item.Hidden = IsLocked;
            item.HasChat = task.ChatId is not null;
            if (IsLocked)
            {
                // Под блокировкой текст напоминания — чужим глазам незачем.
                item.Heading = Loc.Get("S.Deferred.Toast.Reminder");
                item.Body = Loc.Get("S.Deferred.Toast.Locked");
                item.Meta = "";
                return;
            }

            item.Heading = task.Title;
            item.Body = string.Equals(task.Text.Trim(), task.Title.Trim(), StringComparison.Ordinal) ? "" : task.Text;
            item.Meta = ReminderMeta(task, item.Late, DeferredClock.Now());
        }

        /// <summary>Строка под напоминанием: когда сработало, насколько опоздало, сколько пропущено.</summary>
        internal static string ReminderMeta(DeferredTask task, TimeSpan? late, DeferredFacts facts)
        {
            var parts = new List<string> { Loc.Get("S.Deferred.Toast.Reminder") };
            if (task.AwaitingAckSinceUtc is { } since)
            {
                parts.Add(DeferredText.Moment(since, facts));
            }

            if (late is { } lateness)
            {
                parts.Add(DeferredText.Late(lateness));
            }

            if (task.MissedWhileUnacked > 0)
            {
                parts.Add(Loc.Format("S.Deferred.Toast.Missed", task.MissedWhileUnacked));
            }

            return string.Join(" · ", parts);
        }

        private void FillInterrupted(ReminderItem item, DeferredTask task)
        {
            item.Hidden = IsLocked;
            item.HasChat = (task.ResultChatId ?? task.ChatId) is not null;
            item.Heading = Loc.Get("S.Deferred.Toast.Interrupted");
            item.Body = IsLocked ? Loc.Get("S.Deferred.Toast.Locked") : Loc.Format("S.Deferred.Toast.InterruptedText", task.Title);
            item.Meta = IsLocked ? "" : Loc.Get("S.Deferred.Kind." + task.Kind);
        }

        /// <summary>Блокировка встала или снялась — карточки прячут или показывают текст.</summary>
        private void RefillReminders()
        {
            if (_reminders is null || _services is null)
            {
                return;
            }

            var book = _services.Deferred;
            _reminders.Refill(item =>
            {
                if (book.Peek(item.TaskId) is not { } task)
                {
                    return;
                }

                if (item.Look == ReminderLook.Reminder)
                {
                    FillReminder(item, task);
                }
                else
                {
                    FillInterrupted(item, task);
                }
            });
        }

        /// <summary>
        /// Убирает карточки, которые уже не о чем: напоминание подтвердили или отменили с вкладки,
        /// прерванную задачу повторили.
        /// </summary>
        private void DropStaleReminders()
        {
            if (_reminders is null || _services is null)
            {
                return;
            }

            foreach (var item in _reminders.Items.ToList())
            {
                var task = _services.Deferred.Peek(item.TaskId);
                var stale = task is null || task.Status == DeferredStatus.Cancelled || item.Look switch
                {
                    ReminderLook.Reminder => task.AwaitingAckSinceUtc is null,
                    _ => task.Status != DeferredStatus.Interrupted
                };
                if (stale)
                {
                    _reminders.Remove(item.TaskId);
                }
            }
        }

        private void OnReminderAction(ReminderItem item, ReminderAction action, ReminderSnooze snooze)
        {
            var runner = _deferredRunner;
            switch (action)
            {
                case ReminderAction.Done:
                    runner?.Acknowledge(item.TaskId);
                    Reminders.Remove(item.TaskId);
                    break;
                case ReminderAction.Snooze:
                    runner?.Snooze(item.TaskId, SnoozeUntil(snooze, DateTime.Now));
                    Reminders.Remove(item.TaskId);
                    break;
                case ReminderAction.Retry:
                    runner?.RunNow(item.TaskId);
                    Reminders.Remove(item.TaskId);
                    break;
                case ReminderAction.Cancel:
                    runner?.Cancel(item.TaskId);
                    Reminders.Remove(item.TaskId);
                    break;
                case ReminderAction.Dismiss:
                    Reminders.Remove(item.TaskId);
                    break;
                case ReminderAction.OpenChat:
                    OpenDeferredChat(item.TaskId);
                    break;
                case ReminderAction.Open:
                    ShowFromTray();
                    break;
            }
        }

        /// <summary>До какого момента (UTC) отложить напоминание.</summary>
        internal static DateTime SnoozeUntil(ReminderSnooze snooze, DateTime nowLocal) => snooze switch
        {
            ReminderSnooze.OneHour => nowLocal.AddHours(1).ToUniversalTime(),
            ReminderSnooze.TomorrowMorning => nowLocal.Date.AddDays(1).AddHours(9).ToUniversalTime(),
            _ => nowLocal.AddMinutes(10).ToUniversalTime()
        };

        /// <summary>Чат задачи: куда пришёл её последний итог, иначе где её поставили.</summary>
        private void OpenDeferredChat(string taskId)
        {
            if (_services?.Deferred.Peek(taskId) is not { } task || (task.ResultChatId ?? task.ChatId) is not { } chatId)
            {
                return;
            }

            ShowFromTray();
            SettingsOverlay.Visibility = Visibility.Collapsed;
            OpenChat(chatId);
        }

        // ───────────────────────── прогон ─────────────────────────

        Task<DeferredOutcome?> IDeferredHost.RunAsync(DeferredTask task, TimeSpan? late, CancellationToken cancellationToken) =>
            Dispatcher.InvokeAsync(() => RunDeferredAsync(task, late, cancellationToken)).Task.Unwrap();

        /// <summary>
        /// Прогон в чате задачи. Null — сейчас нельзя: в чате идёт ход или заняты все места.
        /// </summary>
        /// <param name="stop">Программа закрывается: задача останется «выполняется» и при следующем запуске станет «прервалась».</param>
        private async Task<DeferredOutcome?> RunDeferredAsync(DeferredTask task, TimeSpan? late, CancellationToken stop)
        {
            if (_services is not { } services)
            {
                return null;
            }

            var session = DeferredChatFor(services, task);
            DeferredOutcome? outcome = null;
            var started = await TryRunTurnAsync(session, TurnKind.Deferred, async (chat, observer, token) =>
            {
                // Общий срок прогона — с ожиданием ответов на вопросы: человека может не быть рядом
                // часами, а место под ход и чат всё это время заняты.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, stop);
                deadline.CancelAfter(DeferredLimits.AgentDeadline);
                try
                {
                    outcome = await RunDeferredWorkAsync(services, chat, task, late, observer, deadline.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                    outcome = new DeferredOutcome(
                        false,
                        token.IsCancellationRequested
                            ? Loc.Get("S.Deferred.Result.Stopped")
                            : Loc.Format("S.Deferred.Result.NoAnswer", DeferredText.Span(DeferredLimits.AgentDeadline)),
                        0m,
                        chat.Id);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    outcome = new DeferredOutcome(false, ex.Message, 0m, chat.Id);
                    throw;
                }
            });

            if (!started)
            {
                return null;
            }

            stop.ThrowIfCancellationRequested();
            return outcome ?? new DeferredOutcome(false, Loc.Get("S.Deferred.Result.NoReply"), 0m, session.Id);
        }

        /// <summary>
        /// Чат для прогона: идущий ход, открытый, с диска — или новый, если тот уже удалили.
        /// </summary>
        /// <remarks>
        /// Живой объект раньше файла: движок пишет в свой, и прогон в копии с диска разошёлся бы с
        /// тем, что на экране.
        /// </remarks>
        private ChatSession DeferredChatFor(AppServices services, DeferredTask task)
        {
            if ((task.ResultChatId ?? task.ChatId) is { } id)
            {
                if (FindTurn(id)?.Session is { } live)
                {
                    return live;
                }

                if (string.Equals(_session.Id, id, StringComparison.Ordinal))
                {
                    return _session;
                }

                if (services.ChatStore.TryLoad(id) is { } loaded)
                {
                    _ = ChatEngine.CloseInterruptedReplies(loaded);
                    return loaded;
                }
            }

            var fresh = services.ChatStore.CreateNew(services.Settings.ChatModelId);
            fresh.Title = Loc.Format("S.Deferred.ChatTitle", task.Title);
            return fresh;
        }

        private static async Task<DeferredOutcome> RunDeferredWorkAsync(
            AppServices services,
            ChatSession chat,
            DeferredTask task,
            TimeSpan? late,
            IChatTurnObserver observer,
            CancellationToken cancellationToken)
        {
            switch (task.Kind)
            {
                case DeferredKind.Agent:
                    // Без человека рядом: потолок цены прогона и лимиты трат без вопроса, как у расписания.
                    using (task.ReadOnly ? ToolGate.ForceReadOnly() : null)
                    using (SpendScope.Push(new SpendMeter { Unattended = true, Cap = task.MaxCostUsd ?? DeferredLimits.AgentRunCapUsd }))
                    {
                        return await services.Chat.RunDeferredAgentAsync(chat, task, late, observer, cancellationToken);
                    }

                case DeferredKind.Command:
                    return await DeferredChat.RecordAsync(
                        chat,
                        task,
                        late,
                        "run_powershell",
                        new { command = task.Command },
                        token => DeferredCommands.RunAsync(
                            task, services.Deferred, services.RunTools(), services.Settings, services.Audit, () => new SessionUndoTracker(), token),
                        outcome => outcome.Success
                            ? Loc.Get("S.Deferred.Result.CommandDone")
                            : Loc.Format("S.Deferred.Result.CommandFailed", DeferredChat.Head(outcome.Error)),
                        observer,
                        cancellationToken);

                default:
                    return await DeferredChat.RecordAsync(
                        chat,
                        task,
                        late,
                        DeferredTaskTool.ToolName,
                        new { action = "restore_programs" },
                        token => RestoreProgramsAsync(services, task, token),
                        outcome => outcome.Success ? outcome.Output ?? "" : outcome.Error ?? "",
                        observer,
                        cancellationToken);
            }
        }

        /// <summary>
        /// Возврат программ: без модели и без вопроса — согласие человек дал, когда задачу ставили.
        /// Режим «только чтение» и печать задачи действуют и здесь; запуск пишется в журнал аудита.
        /// </summary>
        private static async Task<DeferredOutcome> RestoreProgramsAsync(AppServices services, DeferredTask task, CancellationToken cancellationToken)
        {
            if (!services.Deferred.Verify(task))
            {
                return new DeferredOutcome(false, Loc.Get("S.Deferred.Result.Unsealed"), 0m, null);
            }

            if (task.ReadOnly || ToolGate.ScopedReadOnly || ToolGate.IsReadOnly(services.Settings))
            {
                return new DeferredOutcome(false, Loc.Get("S.Deferred.Result.ReadOnly"), 0m, null);
            }

            var snapshot = SessionSnapshot.Load(services.Deferred.Root);
            var result = await Task.Run(
                () => SessionSnapshot.RestoreAsync(snapshot, OpenWindows.Capture(), null, TimeSpan.FromSeconds(1.5), cancellationToken),
                cancellationToken);
            var summary = RestoreSummary(result);
            services.Audit?.Record(
                new AuditOrigin(task.ChatId, task.Title, "deferred:" + task.Id),
                null,
                DeferredTaskTool.ToolName,
                """{"action":"restore_programs"}""",
                ToolEffect.Write,
                result.Failed.Count == 0 ? AuditOutcome.Ok : AuditOutcome.Failed,
                ApprovalSource.Deferred,
                AuditGuard.Off,
                summary);

            return result.NothingToDo || result.Opened.Count > 0
                ? new DeferredOutcome(true, null, 0m, null, summary)
                : new DeferredOutcome(false, summary, 0m, null);
        }

        internal static string RestoreSummary(RestoreResult result)
        {
            if (result.NothingToDo)
            {
                return Loc.Get("S.Deferred.Result.RestoreNone");
            }

            var text = Loc.Format("S.Deferred.Result.Restored", result.Opened.Count);
            return result.Failed.Count == 0 ? text : text + " " + Loc.Format("S.Deferred.Result.RestoreSkipped", string.Join(", ", result.Failed));
        }

        void IDeferredHost.Completed(DeferredTask task, DeferredOutcome outcome) => Later(() => OnDeferredCompleted(task, outcome));

        private void OnDeferredCompleted(DeferredTask task, DeferredOutcome outcome)
        {
            RefreshDeferredPanel();
            var played = ChimePlayer.Play(outcome.Success ? ChimeTune.Done : ChimeTune.Failed, _services?.Settings.DeferredSound == true);

            // Человек смотрит на этот самый чат — итог у него перед глазами, карточка лишняя.
            var chatId = outcome.ChatId ?? task.ResultChatId ?? task.ChatId;
            if (IsForeground() && WindowState != WindowState.Minimized && chatId is not null &&
                string.Equals(_session.Id, chatId, StringComparison.Ordinal) && SettingsOverlay.Visibility != Visibility.Visible)
            {
                return;
            }

            var detail = DeferredChat.Head(outcome.Success ? outcome.Output : outcome.Error);
            Notify(
                "",
                ToastText(detail.Length > 0 ? task.Title + ": " + detail : task.Title),
                task.Status == DeferredStatus.Paused ? Loc.Get("S.Deferred.Status.Paused") : Loc.Get("S.Deferred.Kind." + task.Kind),
                () =>
                {
                    if (chatId is not null)
                    {
                        SettingsOverlay.Visibility = Visibility.Collapsed;
                        OpenChat(chatId);
                    }
                },
                warning: !outcome.Success,
                title: Loc.Get(outcome.Success ? "S.Deferred.Toast.Done" : "S.Deferred.Toast.Failed"),
                silent: played);
        }

        // ───────────────────────── снимок открытых программ ─────────────────────────

        /// <summary>
        /// Пока ждёт задача «вернуть программы», открытые программы записываются раз в несколько
        /// минут: программу могут закрыть раньше выключения, а сбой питания до выключения не дойдёт.
        /// </summary>
        /// <remarks>
        /// Сразу при запуске снимок не пишется намеренно: задача «при следующем включении» как раз
        /// сейчас вернёт программы прошлого сеанса, а запись поверх стёрла бы их список.
        /// </remarks>
        private void UpdateProgramsSnapshot()
        {
            var needed = NeedsProgramsSnapshot();
            if (!needed)
            {
                _programsSnapshot?.Stop();
                return;
            }

            if (_programsSnapshot is null)
            {
                _programsSnapshot = new DispatcherTimer(DispatcherPriority.Background) { Interval = ProgramsSnapshotEvery };
                _programsSnapshot.Tick += (_, _) => SaveProgramsSnapshot(atShutdown: false);
            }

            if (!_programsSnapshot.IsEnabled)
            {
                _programsSnapshot.Start();
            }
        }

        private bool NeedsProgramsSnapshot() =>
            _services?.Deferred.Snapshot().Any(task => task.Kind == DeferredKind.RestorePrograms && task.Status == DeferredStatus.Pending) == true;

        /// <summary>Записывает открытые программы. При выключении Windows — сразу, иначе — в фоне.</summary>
        private void SaveProgramsSnapshot(bool atShutdown)
        {
            if (_services is null || !NeedsProgramsSnapshot())
            {
                return;
            }

            var root = _services.Deferred.Root;
            if (atShutdown)
            {
                SessionSnapshot.Save(root, OpenWindows.Capture(), atShutdown: true, DateTime.UtcNow);
                return;
            }

            Detached.Run(Task.Run(() => SessionSnapshot.Save(root, OpenWindows.Capture(), atShutdown: false, DateTime.UtcNow)), "programs_snapshot");
        }

        // ───────────────────────── вкладка «Отложенные» ─────────────────────────

        /// <summary>Открывает «Автоматизацию» на вкладке «Отложенные» — с отметки задачи в ленте.</summary>
        internal void OpenDeferredTab()
        {
            if (_services is null)
            {
                return;
            }

            OpenSettings(SettingsUi.NavAutomation);
            AutomationPage.Attach(_services);
            AutomationPage.ShowDeferredTab();
        }

        /// <summary>
        /// Где задача сейчас — для отметки под ответом: ближайший срок у ждущей, иначе чем кончилась.
        /// Null — задачи больше нет в списке.
        /// </summary>
        internal string? DeferredStateText(string id)
        {
            if (_services?.Deferred.Peek(id) is not { } task)
            {
                return null;
            }

            return task.Status == DeferredStatus.Pending && (task.SnoozedUntilUtc ?? task.NextDueUtc) is { } due
                ? DeferredText.Moment(due, DeferredClock.Now())
                : task.Status == DeferredStatus.Pending ? DeferredText.When(task, DeferredClock.Now()) : Loc.Get("S.Deferred.Status." + task.Status);
        }

        private void RefreshDeferredPanel()
        {
            if (BuiltPage<SettingsAutomationPage>() is { IsVisible: true } automation)
            {
                automation.RefreshDeferred();
            }
        }
    }
}
