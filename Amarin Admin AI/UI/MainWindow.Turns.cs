using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Ходы чатов в окне: старт, конец, отмена — и то, что из этого рисуется.
    /// </summary>
    /// <remarks>
    /// Состояние и решения — в <see cref="TurnRegistry"/> (Core): какие ходы идут, сколько можно
    /// сразу, у каких чатов метка «ответ готов», чья подпись под композером. Окно рисует по нему и
    /// подаёт нажатия; до 1.30.0 всё это было его полями, и проверить их можно было только
    /// оконным тестом через отражение.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private TurnRegistry? _turnRegistry;
        private ChatPersistQueue? _persistQueue;

        /// <summary>Идущие ходы, метки «ответ готов», подписи под композером.</summary>
        internal TurnRegistry Turns => _turnRegistry ??= new TurnRegistry(
            () => _services?.Confirmations,
            () => _services?.PlanReviews);

        /// <summary>
        /// Отложенная запись чатов идущих ходов. Срабатывание таймера возвращается фоновым
        /// приоритетом — как у прежнего <c>DispatcherTimer</c>: запись не вклинивается перед
        /// отрисовкой и вводом.
        /// </summary>
        private ChatPersistQueue PersistQueue => _persistQueue ??= new ChatPersistQueue(
            () => _services?.ChatStore,
            RefreshChatList,
            action => Dispatcher.InvokeAsync(action, DispatcherPriority.Background));

        /// <summary>Зажечь метку «ответ готов» — только если чат сейчас не на экране.</summary>
        /// <remarks>
        /// Набор входит в <see cref="BuildChatListSignature"/> — иначе панель сочла бы, что ничего не
        /// изменилось, и не перерисовалась бы вовсе.
        /// </remarks>
        private void MarkAttention(RunningTurn turn)
        {
            if (IsVisibleTurn(turn))
            {
                return;
            }

            if (Turns.MarkAttention(turn.SessionId))
            {
                RefreshChatList();
            }
        }

        /// <summary>
        /// Забыть метку удалённого чата. Без этого его идентификатор остался бы в наборе до
        /// перезапуска: список чатов фильтруется поиском, и вычистить набор по нему нельзя.
        /// </summary>
        private void ForgetAttention(string sessionId) => Turns.ForgetAttention(sessionId);

        /// <summary>Листаются ли варианты ответа в открытом чате: пока он отвечает — нет.</summary>
        private readonly VariantGate _variantGate = new();

        /// <summary>Идёт ли ход в этом чате. В одном чате больше одного хода не бывает.</summary>
        internal bool IsBusy(string sessionId) => Turns.IsBusy(sessionId);

        /// <summary>Занята ли программа целиком — обновлением, сменой профиля.</summary>
        internal bool AnyTurnRunning => Turns.AnyRunning;

        internal RunningTurn? FindTurn(string sessionId) => Turns.Find(sessionId);

        private bool IsVisibleTurn(RunningTurn turn) =>
            string.Equals(turn.SessionId, _session.Id, StringComparison.Ordinal);

        /// <summary>
        /// Общий каркас хода: проверки, регистрация, работа движка, снятие с учёта.
        /// </summary>
        internal Task RunTurnAsync(
            ChatSession session,
            TurnKind kind,
            Func<ChatSession, IChatTurnObserver, CancellationToken, Task> work) =>
            TryRunTurnAsync(session, kind, work, ShowTurnLimitNotice);

        /// <summary>
        /// Тот же каркас, но отказ — ответом, а не окном: отложенная задача при занятом чате или без
        /// свободного места просто подождёт, человеку об этом знать незачем.
        /// </summary>
        /// <returns>False — ход не начался: в чате уже идёт ход или заняты все места.</returns>
        internal async Task<bool> TryRunTurnAsync(
            ChatSession session,
            TurnKind kind,
            Func<ChatSession, IChatTurnObserver, CancellationToken, Task> work,
            Action? limitReached = null)
        {
            if (_services is null)
            {
                return false;
            }

            var start = Turns.TryStart(session, kind, DateTime.Now);
            if (start.Refusal == TurnRefusal.LimitReached)
            {
                limitReached?.Invoke();
                return false;
            }

            if (start.Turn is not { } turn)
            {
                return false;
            }

            UpdateComposerChrome();
            RefreshChatList();

            var router = new ChatTurnRouter(
                turn,
                saved => Ui(() => Persist(saved)),
                saved => Ui(() => SchedulePersist(saved)),
                this);

            try
            {
                // Цель чата, «только чтение» и счётчик лимитов — на весь ход (см. TurnScopes).
                using (TurnScopes.Enter(session, _services?.Machines.Find(session.TargetMachineId)))
                {
                    await work(session, router, turn.Cancellation.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Отмена — обычный конец хода, не авария.
            }
            catch (Exception ex)
            {
                ((IChatTurnUi)this).TurnError(turn, ex.Message);
            }
            finally
            {
                FinishTurn(turn);
            }

            return true;
        }

        /// <summary>
        /// Снимает ход с учёта (<see cref="TurnRegistry.Finish"/>) и приводит окно в порядок.
        /// Единственное место, через которое проходит любой конец.
        /// </summary>
        internal void FinishTurn(RunningTurn turn)
        {
            var hadNotice = Turns.NoticeFor(turn.SessionId) is not null;
            if (!Turns.Finish(turn))
            {
                return;
            }

            if (hadNotice)
            {
                UpdateAttachmentWarning();
            }

            if (IsVisibleTurn(turn))
            {
                StopVisibleRendering();
            }

            // Venice штампует остаток на заголовках каждого ответа, так что к этому моменту
            // книга уже знает цифру, которую оставил после себя ход — любой, в том числе фоновый.
            // У остальных ключей её спрашивают отдельно, но не чаще, чем раз в несколько минут.
            _balance?.Refresh();
            Detached.Run(RefreshStaleBalancesAsync(), "balances");

            UpdateComposerChrome();
            RefreshChatList();

            // Фокус — только за видимым ходом. Иначе фоновый ответ выдернул бы каретку из
            // сообщения, которое человек в это время печатает в другом чате.
            if (IsVisibleTurn(turn) && IsForeground())
            {
                FocusMessageInput();
            }
        }

        private void CancelTurn(string sessionId) => Turns.Cancel(sessionId);

        private void CancelAllTurns() => Turns.CancelAll();

        /// <summary>
        /// Заполненность контекста открытого чата. Считается на месте, а не хранится: число
        /// меняется и от ответа модели, и от смены модели под тем же разговором.
        /// </summary>
        private void RefreshContextRing()
        {
            if (_services is null)
            {
                return;
            }

            // «Авто» — не модель, и Find по нему ничего не находит: кольцо показывало прочерк
            // в каждом чате, где выбрано «Авто». Потолок берём по обеим моделям, между которыми
            // маршрутизатор и выбирает.
            var modelId = CurrentModelId();
            var candidates = VeniceModelCatalog.IsAuto(modelId)
                ? _services.Chat.AutoCandidateModelIds().Select(_services.Models.Find).ToList()
                : [_services.Models.Find(modelId)];

            var usage = ContextGauge.Measure(
                _session,
                _services.Chat.CurrentSystemPrompt(_session),
                candidates);
            _context?.Show(usage);
            UpdateCompactHint(usage);
            ScheduleCostEstimate(usage, candidates);
        }

        /// <summary>Кнопки композера по состоянию открытого чата.</summary>
        private void UpdateComposerChrome()
        {
            var busy = IsBusy(_session.Id);
            RefreshContextRing();
            UpdateChatCostChip();
            UpdateTrayState();
            _compact?.SetBusy(busy);
            _variantGate.IsOpen = !busy;

            // Остаётся живым, пока чат отвечает: вторую строку не отклоняем, а ставим в очередь и
            // вплетаем в контекст на следующей границе раунда.
            SendButton.IsEnabled = true;

            // «Новый чат» и список больше не гаснут: открыть другой разговор и писать в нём
            // можно, пока этот отвечает, — ради этого всё и затевалось.
            NewChatButton.IsEnabled = true;
            ChatListPanel.IsEnabled = true;
        }

        private void ShowTurnLimitNotice() => ShowComposerNotice(
            Loc.Format("S.Turn.LimitReached", TurnRegistry.MaxParallel));

        /// <summary>
        /// Короткое сообщение под полем ввода. Модалка здесь была бы перебором: это не ошибка,
        /// а «сейчас нельзя, попробуйте через минуту».
        /// </summary>
        private void ShowComposerNotice(string text)
        {
            Turns.ShowNotice(_session.Id, text);
            UpdateAttachmentWarning();
        }

        /// <summary>Снимает подпись того чата, которому она принадлежала.</summary>
        private void ClearComposerNotice(string sessionId)
        {
            if (Turns.ClearNotice(sessionId))
            {
                UpdateAttachmentWarning();
            }
        }

        /// <summary>
        /// Ход забрал дописанное сообщение — значит, модель его увидела, и обещание «учту»
        /// исполнено. Держать подпись дальше значило бы врать: она висела бы до конца ответа.
        /// </summary>
        void IChatTurnUi.TurnQueuedTaken(RunningTurn turn) => UiAsync(() =>
        {
            if (!turn.HasQueued)
            {
                ClearComposerNotice(turn.SessionId);
            }
        });

        /// <summary>
        /// Второй запуск программы попросил показаться. Поднимаем окно и забираем то, что он
        /// принёс с командной строки.
        /// </summary>
        /// <remarks>
        /// <c>SetForegroundWindow</c> из чужого процесса подчинён foreground-lock, и Windows
        /// вправе отказать. Тогда мигаем кнопкой в панели задач — сигнал «я здесь» уже есть
        /// и работает всегда. Деградируем, а не падаем.
        /// </remarks>
        internal void ActivateFromSecondInstance() => Ui(() =>
        {
            // Окно спрятано, пока доводится обновление: вернуть его, а если файл уже
            // подменяется — дать подмене закончиться и поднять новую версию (см. Updates).
            if (!ReviveFromBackgroundExit())
            {
                return;
            }

            // Спрятано в трей (G1) — показать, как по щелчку на значке.
            if (!IsVisible)
            {
                ShowFromTray();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            if (!Activate())
            {
                TaskbarFlash.Flash(this);
            }

            TakeHandoffPrompt();
        });

        /// <summary>
        /// Забирает переданное вторым запуском: текст — в поле ввода, отправка — только если
        /// тот запуск был с <c>--send</c>.
        /// </summary>
        private void TakeHandoffPrompt()
        {
            var requests = SingleInstanceHandoff.TryTakeAll(AppPaths.Root);

            // Сначала действие (новый чат, открыть чат, Проводник), потом текст: иначе текст
            // лёг бы в поле прежнего чата, а действие тут же увело бы от него.
            if (HandoffRequest.LatestAction(requests) is { } action)
            {
                RunStartupAction(action.Action, action.ChatId, action.AskPath);
            }

            if (HandoffRequest.Latest(requests) is { } request)
            {
                PlaceIncomingPrompt(request.Prompt, request.Send);
            }
        }

        /// <summary>
        /// Запрос извне — из командной строки или от второго запуска. Один путь на оба случая,
        /// чтобы одна и та же команда вела себя одинаково, открыто окно или нет.
        /// </summary>
        private void PlaceIncomingPrompt(string? prompt, bool send)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                FocusMessageInput();
                return;
            }

            MessageTextBox.Text = prompt;
            MessageTextBox.CaretIndex = MessageTextBox.Text.Length;
            if (send)
            {
                Detached.Run(SendAsync(), "send");
                return;
            }

            FocusMessageInput();
        }

        private void Persist(ChatSession session) => PersistQueue.Persist(session);

        private void PersistCurrent() => Persist(_session);

        private void SchedulePersist(ChatSession session) => PersistQueue.Schedule(session);

        /// <summary>Пишет все накопившиеся чаты одним проходом, с потока диспетчера.</summary>
        private void FlushPendingPersists() => PersistQueue.Flush();
    }
}
