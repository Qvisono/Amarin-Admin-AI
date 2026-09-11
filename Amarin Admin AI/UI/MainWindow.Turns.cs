using System.Runtime.Versioning;
using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Реестр идущих ходов: по одному на чат, несколько одновременно.
    /// </summary>
    /// <remarks>
    /// Раньше ход был ровно один и жил полями окна (<c>_busy</c>, <c>_turnCts</c>), а переключение
    /// чата его отменяло. Реестр живёт в окне, а не отдельным сервисом: и все точки старта, и все
    /// точки финиша — методы окна, так что сервис пришлось бы связывать двусторонним интерфейсом
    /// с единственным потребителем. Словарь читается и меняется только с потока диспетчера.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>
        /// Сколько ответов может идти одновременно.
        /// </summary>
        /// <remarks>
        /// Не настройка и не «сколько угодно»: каждый ход способен поднять до четырёх агентов, а
        /// <see cref="AgentSlotLimiter.MaxAgents"/> — общий на программу, и каждый агент гоняет
        /// PowerShell по этой же машине. При неограниченном числе ходов очередь подтверждений
        /// наполнялась бы вопросами из чатов, о которых человек уже забыл.
        /// </remarks>
        internal const int MaxParallelTurns = 3;

        private readonly Dictionary<string, RunningTurn> _turns = [];

        /// <summary>Чаты, которые ждут отложенной записи на диск.</summary>
        private readonly Dictionary<string, ChatSession> _dirtySessions = [];

        /// <summary>Идёт ли ход в этом чате. В одном чате больше одного хода не бывает.</summary>
        internal bool IsBusy(string sessionId) => _turns.ContainsKey(sessionId);

        /// <summary>Занята ли программа целиком — обновлением, сменой профиля.</summary>
        internal bool AnyTurnRunning => _turns.Count > 0;

        internal RunningTurn? FindTurn(string sessionId) =>
            _turns.TryGetValue(sessionId, out var turn) ? turn : null;

        private bool IsVisibleTurn(RunningTurn turn) =>
            string.Equals(turn.SessionId, _session.Id, StringComparison.Ordinal);

        /// <summary>
        /// Общий каркас хода: проверки, регистрация, работа движка, снятие с учёта.
        /// </summary>
        private async Task RunTurnAsync(
            ChatSession session,
            TurnKind kind,
            Func<ChatSession, IChatTurnObserver, CancellationToken, Task> work)
        {
            if (_services is null || IsBusy(session.Id))
            {
                return;
            }

            if (_turns.Count >= MaxParallelTurns)
            {
                ShowTurnLimitNotice();
                return;
            }

            var turn = new RunningTurn
            {
                Session = session,
                Cancellation = new CancellationTokenSource(),
                Kind = kind,
                StartedAt = DateTime.Now
            };

            _turns[session.Id] = turn;
            UpdateComposerChrome();
            RefreshChatList();

            var router = new ChatTurnRouter(
                turn,
                saved => Ui(() => Persist(saved)),
                saved => Ui(() => SchedulePersist(saved)),
                this);

            try
            {
                await work(session, router, turn.Cancellation.Token);
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
        }

        /// <summary>Снимает ход с учёта. Единственное место, через которое проходит любой конец.</summary>
        private void FinishTurn(RunningTurn turn)
        {
            if (!_turns.TryGetValue(turn.SessionId, out var registered) || !ReferenceEquals(registered, turn))
            {
                return;
            }

            _turns.Remove(turn.SessionId);
            turn.Finished = true;

            try
            {
                turn.Cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Уже освобождён отменой — ничего страшного.
            }

            if (IsVisibleTurn(turn))
            {
                StopVisibleRendering();
            }

            // Venice штампует остаток на заголовках каждого ответа, так что к этому моменту
            // клиент держит цифру, которую оставил после себя ход — любой, в том числе фоновый.
            _balance?.Show(_services?.Venice.LastBalance);

            UpdateComposerChrome();
            RefreshChatList();

            // Фокус — только за видимым ходом. Иначе фоновый ответ выдернул бы каретку из
            // сообщения, которое человек в это время печатает в другом чате.
            if (IsVisibleTurn(turn) && IsForeground())
            {
                FocusMessageInput();
            }
        }

        private void CancelTurn(string sessionId)
        {
            if (!_turns.TryGetValue(sessionId, out var turn))
            {
                return;
            }

            try
            {
                turn.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }

            _services?.Confirmations.CancelForSession(sessionId);
        }

        /// <summary>Остановить ход открытого чата — это делает кнопка «стоп» в сообщении.</summary>
        private void CancelTurn() => CancelTurn(_session.Id);

        private void CancelAllTurns()
        {
            foreach (var sessionId in _turns.Keys.ToArray())
            {
                CancelTurn(sessionId);
            }

            _services?.Confirmations.CancelAll();
        }

        /// <summary>Кнопки композера по состоянию открытого чата.</summary>
        private void UpdateComposerChrome()
        {
            var busy = IsBusy(_session.Id);
            _compact?.SetBusy(busy);
            SendButton.IsEnabled = !busy;

            // «Новый чат» и список больше не гаснут: открыть другой разговор и писать в нём
            // можно, пока этот отвечает, — ради этого всё и затевалось.
            NewChatButton.IsEnabled = true;
            ChatListPanel.IsEnabled = true;
        }

        private void ShowTurnLimitNotice() => ShowComposerNotice(
            Loc.Format("S.Turn.LimitReached", MaxParallelTurns));

        /// <summary>
        /// Короткое сообщение под полем ввода. Модалка здесь была бы перебором: это не ошибка,
        /// а «сейчас нельзя, попробуйте через минуту».
        /// </summary>
        private void ShowComposerNotice(string text)
        {
            AttachmentsWarning.Text = text;
            AttachmentsWarning.Visibility = Visibility.Visible;
            AttachmentsHost.Visibility = Visibility.Visible;
        }

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

        /// <summary>Кладёт переданный текст в композер, но не отправляет: решает человек.</summary>
        private void TakeHandoffPrompt()
        {
            var requests = SingleInstanceHandoff.TryTakeAll(AppPaths.Root);
            var prompt = requests
                .Select(item => item.Prompt)
                .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));

            if (prompt is null)
            {
                return;
            }

            MessageTextBox.Text = prompt;
            MessageTextBox.CaretIndex = MessageTextBox.Text.Length;
            FocusMessageInput();
        }

        private void Persist(ChatSession session)
        {
            if (_services is null || string.IsNullOrWhiteSpace(session.Id) || session.Messages.Count == 0)
            {
                return;
            }

            _dirtySessions.Remove(session.Id);

            try
            {
                _services.ChatStore.Save(session);
            }
            catch (InvalidOperationException)
            {
                // Движок правит списки сообщения из параллельных задач инструментов, и
                // JsonSerializer на меняющейся коллекции бросает. Отложим на следующий тик —
                // финальное сохранение в конце хода всё равно авторитетно.
                SchedulePersist(session);
                return;
            }

            RefreshChatList();
        }

        private void PersistCurrent() => Persist(_session);

        private void SchedulePersist(ChatSession session)
        {
            _dirtySessions[session.Id] = session;
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Пишет все накопившиеся чаты одним проходом, с потока диспетчера.</summary>
        private void FlushPendingPersists()
        {
            _saveTimer.Stop();
            foreach (var session in _dirtySessions.Values.ToArray())
            {
                Persist(session);
            }
        }
    }
}
