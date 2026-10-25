using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Быстрый вопрос (1.37.0): глобальная клавиша, маленькое окно поверх всех, ответ и «Открыть в
    /// чате».
    /// </summary>
    /// <remarks>
    /// Вопрос идёт обычным ходом чата (<see cref="TurnKind.QuickAsk"/>) во временной сессии: та
    /// же модель или «Авто», те же инструменты и шлюз, что у чата, — но ни файла, ни строки в
    /// списке, пока человек не нажмёт «Открыть в чате». Тогда сессия становится обычным чатом,
    /// записывается и открывается тем же объектом: движок, если ещё отвечает, пишет в него же.
    /// </remarks>
    public partial class MainWindow
    {
        private QuickAskWindow? _quickAsk;

        /// <summary>Сессия текущего быстрого вопроса; null — вопроса нет или его открыли в чате.</summary>
        private ChatSession? _quickSession;

        /// <summary>Окно быстрого вопроса, если его уже создавали, — для тестов.</summary>
        internal QuickAskWindow? QuickAsk => _quickAsk;

        /// <summary>
        /// Глобальная клавиша: показать окно, а если оно уже впереди — спрятать. Под блокировкой
        /// профиля окно не открывается: вопрос обошёл бы пароль — вместо него встаёт главное окно.
        /// </summary>
        internal void OpenQuickAsk()
        {
            if (_services is null)
            {
                return;
            }

            if (IsLocked)
            {
                ShowFromTray();
                return;
            }

            var window = EnsureQuickAsk();
            if (window.IsVisible && window.IsActive)
            {
                HideQuickAsk();
                return;
            }

            // Монитор — того окна, что сейчас впереди: вопрос задают поверх своей работы.
            window.Present(_services.Settings.UiScalePercent, ToastScreen.ForegroundWorkArea());
        }

        private QuickAskWindow EnsureQuickAsk()
        {
            if (_quickAsk is not null)
            {
                return _quickAsk;
            }

            var window = new QuickAskWindow
            {
                PendingText = () => _quickSession is { } session ? FindTurn(session.Id)?.PendingText : null,
                WaitingForApproval = () => _quickSession is { } session && _services is { } services &&
                                           services.Confirmations.TryPeek(out var request) &&
                                           string.Equals(request.SessionId, session.Id, StringComparison.Ordinal)
            };
            window.Asked += text => Detached.Run(AskQuickAsync(text), "quick_ask");
            window.OpenInChatRequested += OpenQuickInChat;
            window.HideByUser += HideQuickAsk;
            Closed += (_, _) => window.Shutdown();
            _quickAsk = window;
            return window;
        }

        /// <summary>
        /// Задаёт вопрос. Прежний, не открытый в чате, уходит насовсем — как в строке поиска,
        /// новый вопрос заменяет старый.
        /// </summary>
        internal async Task AskQuickAsync(string text)
        {
            if (_services is not { } services || string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            DiscardQuickSession();
            var window = EnsureQuickAsk();
            if (!HasUsableKey())
            {
                window.ShowStatus(Loc.Get("S.QuickAsk.NoKey"), danger: true);
                return;
            }

            var session = services.ChatStore.CreateNew(CurrentModelId());
            session.IsTemporary = true;
            ApplyChatReasoningDefaults(session);
            _quickSession = session;
            window.ShowWaiting();

            await TryRunTurnAsync(
                session,
                TurnKind.QuickAsk,
                (chat, observer, token) => services.Chat.RunTurnAsync(chat, text, null, null, null, observer, token),
                () => window.ShowStatus(Loc.Get("S.QuickAsk.Busy"), danger: true));
        }

        /// <summary>Ответ быстрого вопроса закрыт — окно показывает его набело.</summary>
        private void QuickAskCompleted(RunningTurn turn, ChatDisplayMessage assistant)
        {
            if (_quickAsk is null || _quickSession?.Id != turn.SessionId)
            {
                return;
            }

            var model = VeniceModelCatalog.GetDisplayName(assistant.ResolvedModelId ?? assistant.RequestedModelId ?? "");
            var cost = ChatFormat.Cost(assistant.Cost);
            var meta = string.Join(" · ", new[] { model, cost }.Where(part => !string.IsNullOrWhiteSpace(part)));
            _quickAsk.ShowFinal(assistant.Text, meta);
        }

        /// <summary>Ошибка хода быстрого вопроса — строкой в его окне, а не модалкой главного.</summary>
        private bool QuickAskFailed(RunningTurn turn, string message)
        {
            if (_quickAsk is null || _quickSession?.Id != turn.SessionId)
            {
                return false;
            }

            _quickAsk.ShowStatus(message, danger: true);
            return true;
        }

        /// <summary>
        /// «Открыть в чате»: временная сессия становится обычным чатом, записывается и открывается
        /// в главном окне тем же объектом.
        /// </summary>
        internal void OpenQuickInChat()
        {
            if (_services is null || _quickSession is not { } session)
            {
                return;
            }

            _quickSession = null;
            session.IsTemporary = false;
            _services.ChatStore.Save(session);
            _quickAsk?.Reset();
            ShowFromTray();
            ShowChat(session);

            // Заголовок быстрому вопросу не заказывали, пока он был временным.
            if (session.Messages.FirstOrDefault(message => message.Role == "user") is { } first)
            {
                MaybeStartTitle(session, first);
            }
        }

        /// <summary>Прячет окно; вопрос, не открытый в чате, уходит вместе с ходом.</summary>
        internal void HideQuickAsk()
        {
            DiscardQuickSession();
            _quickAsk?.Reset();
        }

        private void DiscardQuickSession()
        {
            if (_quickSession is not { } session)
            {
                return;
            }

            _quickSession = null;
            DiscardTemporary(session.Id);
        }
    }
}
