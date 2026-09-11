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
    /// <para>
    /// «По одному на чат» осталось в силе и после того, как в идущий ход разрешили дописывать:
    /// новое сообщение не заводит второй <see cref="RunningTurn"/>, а встаёт в очередь этого же
    /// (<see cref="RunningTurn.Enqueue"/>) и вливается в контекст на границе раунда. Стенограмма
    /// <c>ApiMessages</c> от этого остаётся линейной — два хода вперемешку дали бы историю,
    /// которую не прочитают ни человек, ни модель.
    /// </para>
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

        /// <summary>
        /// Короткая подпись под композером — по чату, в котором её вызвали.
        /// </summary>
        /// <remarks>
        /// Подпись одна на окно, а чатов много: «отправлено, учту» из одного разговора висела над
        /// всеми остальными и не гасла, потому что гасить её было некому. Здесь она привязана к
        /// чату, показывается только в нём и снимается в тот момент, когда ход забрал сообщение.
        /// </remarks>
        private readonly Dictionary<string, string> _composerNotices = [];

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
            RescueQueued(turn);
            ClearComposerNotice(turn.SessionId);

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

        /// <summary>
        /// Дописанное, до чего ход не дожил, кладётся в стенограмму прямо здесь.
        /// </summary>
        /// <remarks>
        /// Пузырь такого сообщения человек уже видит: он рисуется в момент отправки. Если ход
        /// оборвали (отмена, сбой сети) раньше, чем движок забрал строку, она пропала бы только
        /// из контекста — на экране осталась бы, и следующий ответ выглядел бы так, будто модель
        /// её прочитала и пропустила мимо ушей.
        /// </remarks>
        private static void RescueQueued(RunningTurn turn)
        {
            while (turn.TryTakeQueued(out var text))
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                turn.Session.ApiMessages.Add(new ChatMessage
                {
                    Role = "user",
                    Content = ChatContent.Text(text.Trim())
                });
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
                // Ход мог завершиться сам и освободить CancellationTokenSource.
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

            _context?.Show(ContextGauge.Measure(
                _session,
                _services.Chat.CurrentSystemPrompt(),
                _services.Models.Find(CurrentModelId())));
        }

        /// <summary>Кнопки композера по состоянию открытого чата.</summary>
        private void UpdateComposerChrome()
        {
            var busy = IsBusy(_session.Id);
            RefreshContextRing();
            _compact?.SetBusy(busy);

            // Stays live while the chat answers: a second line is no longer refused, it is queued
            // and folded into the context at the next round boundary.
            SendButton.IsEnabled = true;

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
            _composerNotices[_session.Id] = text;
            UpdateAttachmentWarning();
        }

        /// <summary>Снимает подпись того чата, которому она принадлежала.</summary>
        private void ClearComposerNotice(string sessionId)
        {
            if (_composerNotices.Remove(sessionId))
            {
                UpdateAttachmentWarning();
            }
        }

        /// <summary>
        /// Ход забрал дописанное сообщение — значит, модель его увидела, и обещание «учту»
        /// исполнено. Держать подпись дальше значило бы врать: она висела бы до конца ответа.
        /// </summary>
        void IChatTurnUi.TurnQueuedTaken(RunningTurn turn) => Ui(() =>
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
