using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Сводка переписки: её дописывает модель «быстрая» после каждого ответа, по ней же идёт
    /// поиск по содержимому чатов.
    /// </summary>
    /// <remarks>
    /// Устроено по образцу генерации заголовка (<c>MainWindow.GenerateTitleAsync</c>) и с теми
    /// же тремя оговорками: задача брошенная, чат за время её работы мог уйти в фон, а провал
    /// сводки — не повод ронять ход или показывать человеку ошибку.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private CancellationTokenSource? _chatSearchCts;

        /// <summary>Ответ закрыт — дописываем сводку тем, что в нём было сказано.</summary>
        private void MaybeUpdateSummary(ChatSession session)
        {
            if (_services is null || session.Messages.Count == 0)
            {
                return;
            }

            // Без ключа запрос всё равно не состоится. Провал сводки тихий, и человек ничего бы
            // не заметил, — но это был бы неудачный запрос в сеть на каждый ответ.
            if (string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                return;
            }

            var previous = session.Summary;
            var exchange = ChatSummaryGenerator.BuildExchange(
                string.IsNullOrWhiteSpace(previous) ? ColdStart(session) : LastExchange(session));
            if (exchange.Length == 0)
            {
                return;
            }

            // Цену припишем этому ответу: он её и вызвал.
            var assistantId = session.Messages
                .LastOrDefault(item => item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))?.Id;

            Detached.Run(UpdateSummaryAsync(session.Id, previous, exchange, assistantId), "update_summary");
        }

        /// <summary>
        /// Сводки ещё нет: подаём хвост переписки целиком, иначе первая же сводка окажется
        /// пересказом одного последнего ответа, а не чата.
        /// </summary>
        private static List<ChatDisplayMessage> ColdStart(ChatSession session) =>
            [.. session.Messages.TakeLast(ChatSummary.ColdStartMessages)];

        /// <summary>
        /// Сводка есть: подаём только то, чего она ещё не видела, — от последнего вопроса
        /// человека и до конца.
        /// </summary>
        private static List<ChatDisplayMessage> LastExchange(ChatSession session)
        {
            var from = session.Messages.Count - 1;
            for (var i = session.Messages.Count - 1; i >= 0; i--)
            {
                if (session.Messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    from = i;
                    break;
                }
            }

            return [.. session.Messages.Skip(from)];
        }

        private async Task UpdateSummaryAsync(
            string sessionId,
            string? previous,
            string exchange,
            string? assistantId)
        {
            if (_services is null)
            {
                return;
            }

            try
            {
                var draft = await _services.Summaries.UpdateAsync(previous, exchange).ConfigureAwait(true);
                if (draft.Summary is null && draft.Cost is null)
                {
                    return;
                }

                Ui(() =>
                {
                    // Чат мог уйти в фон, пока сводка писалась, — ищем его и там.
                    var session = string.Equals(_session.Id, sessionId, StringComparison.Ordinal)
                        ? _session
                        : FindTurn(sessionId)?.Session;
                    if (session is null)
                    {
                        return;
                    }

                    if (draft.Summary is not null)
                    {
                        session.Summary = draft.Summary;
                    }

                    // Деньги записываем даже тогда, когда сводка не получилась: потрачены они
                    // всё равно, и молчание об этом было бы враньём в счёте.
                    var repriced = BookSummaryCost(session, assistantId, draft.Cost);

                    Persist(session);

                    // Только этот ответ, а не вся лента: сводка дописывается после каждого
                    // ответа, и перерисовка чата целиком заодно сбрасывала бы лупу.
                    if (repriced && string.Equals(_session.Id, sessionId, StringComparison.Ordinal))
                    {
                        RefreshMessageView(assistantId);
                    }
                });
            }
            catch
            {
                // Сводка — удобство: её провал человеку не показывают.
            }
        }

        /// <summary>
        /// Приписывает цену сводки ответу, который её вызвал. Возвращает <c>true</c>, если счёт
        /// уже закрытого сообщения пришлось поправить, — тогда его нужно перерисовать.
        /// </summary>
        private static bool BookSummaryCost(ChatSession session, string? assistantId, VeniceCost? cost)
        {
            if (cost is not { HasData: true } || string.IsNullOrEmpty(assistantId))
            {
                return false;
            }

            var message = session.Messages.FirstOrDefault(
                item => string.Equals(item.Id, assistantId, StringComparison.Ordinal));
            if (message is null || message.SummaryCost is not null)
            {
                return false;
            }

            message.SummaryCost = cost;

            // Cost присваивается там, где движок закрывает счёт: null надёжно значит «ещё не
            // закрыт», и прибавлять туда нельзя — сложит он сам.
            if (message.Cost is null)
            {
                return false;
            }

            message.Cost = message.Cost.Add(cost);
            return true;
        }

        /// <summary>
        /// Пересобирает сводку чата с нуля по всей переписке. Вызывается с вкладки журнала,
        /// когда сводка устарела или вышла неудачной.
        /// </summary>
        internal async Task<bool> RebuildSummaryAsync(string chatId, CancellationToken cancellationToken)
        {
            if (_services is null || string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                return false;
            }

            // Открытый чат живёт в памяти, и его копия на диске может отставать на целый ответ.
            var session = string.Equals(_session.Id, chatId, StringComparison.Ordinal)
                ? _session
                : FindTurn(chatId)?.Session ?? _services.ChatStore.TryLoad(chatId);
            if (session is null)
            {
                return false;
            }

            var exchange = ChatSummaryGenerator.BuildExchange(
                session.Messages.TakeLast(ChatSummary.RebuildMessages));
            if (exchange.Length == 0)
            {
                return false;
            }

            var draft = await _services.Summaries
                .UpdateAsync(previous: null, exchange, cancellationToken)
                .ConfigureAwait(true);
            if (draft.Summary is null)
            {
                return false;
            }

            session.Summary = draft.Summary;
            BookSummaryCost(
                session,
                session.Messages.LastOrDefault(item =>
                    item.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))?.Id,
                draft.Cost);
            Persist(session);
            return true;
        }

        /// <summary>
        /// Сводки всех чатов для поиска. Берутся из индекса: читать по файлу на чат ради одной
        /// строки значило бы сотню чтений с диска на каждый поиск.
        /// </summary>
        private List<(string ChatId, string Summary)> SummariesForSearch()
        {
            if (_services is null)
            {
                return [];
            }

            var list = new List<(string, string)>();
            foreach (var item in _services.ChatStore.List())
            {
                // Открытый чат в индексе может отставать на последний ответ.
                var summary = string.Equals(item.Id, _session.Id, StringComparison.Ordinal)
                    ? _session.Summary ?? item.Summary
                    : item.Summary;
                if (!string.IsNullOrWhiteSpace(summary))
                {
                    list.Add((item.Id, summary));
                }
            }

            return list;
        }

        /// <summary>Что показывать в списке, пока поиск по содержимому не дал результата.</summary>
        private enum ContentSearchState
        {
            /// <summary>Режим поиска по заголовкам: списку подсказки не нужны.</summary>
            Off,

            /// <summary>Запрос набран, но не отправлен — ждём Enter.</summary>
            Prompt,

            /// <summary>Запрос ушёл модели.</summary>
            Working,

            /// <summary>Модель ответила.</summary>
            Done,

            /// <summary>Искать не по чему: ни у одного чата нет сводки.</summary>
            Empty,

            /// <summary>Ключа Venice нет — спрашивать некого.</summary>
            NoKey,

            /// <summary>Запрос не удался.</summary>
            Failed
        }

        private bool _searchByContent;
        private ContentSearchState _contentSearchState = ContentSearchState.Off;
        private List<string>? _contentSearchIds;

        /// <summary>
        /// На экране лежит ответ модели, а не обычная выдача. Одного <c>_searchByContent</c> мало:
        /// при пустом запросе режим включён, но список — это все чаты, и группы по датам им нужны.
        /// </summary>
        private bool IsContentSearchResult =>
            _searchByContent && _contentSearchState == ContentSearchState.Done;

        /// <summary>
        /// Чаты для боковой панели. В режиме «по чатам» порядок задаёт модель — он и есть
        /// порядок близости к запросу, поэтому обычная сортировка по дате здесь не применяется.
        /// </summary>
        private IReadOnlyList<ChatIndexEntry> ChatListItems(string query)
        {
            if (_services is null)
            {
                return [];
            }

            if (!_searchByContent || string.IsNullOrWhiteSpace(query))
            {
                return _services.ChatStore.Search(query);
            }

            if (_contentSearchIds is null)
            {
                return [];
            }

            var byId = _services.ChatStore.List().ToDictionary(item => item.Id, StringComparer.Ordinal);
            var found = new List<ChatIndexEntry>(_contentSearchIds.Count);
            foreach (var id in _contentSearchIds)
            {
                if (byId.TryGetValue(id, out var entry))
                {
                    found.Add(entry);
                }
            }

            return found;
        }

        /// <summary>Подпись под пустым списком. «Ничего не найдено» — только когда искали.</summary>
        private string ChatListEmptyText() => _contentSearchState switch
        {
            ContentSearchState.Prompt => Loc.Get("S.Search.PressEnter"),
            ContentSearchState.Working => Loc.Get("S.Search.Working"),
            ContentSearchState.Empty => Loc.Get("S.Search.NoSummaries"),
            ContentSearchState.NoKey => Loc.Get("S.Turn.NoApiKey"),
            ContentSearchState.Failed => Loc.Get("S.Search.Failed"),
            _ => Loc.Get("S.Common.NothingFound")
        };

        private void SearchModeTitles_Click(object sender, RoutedEventArgs e) => SetSearchMode(byContent: false);

        private void SearchModeContent_Click(object sender, RoutedEventArgs e) => SetSearchMode(byContent: true);

        private void SetSearchMode(bool byContent)
        {
            // Кнопки — ToggleButton, и нажатие уже переставило галку. Возвращаем её на место:
            // выбран ровно один режим, повторное нажатие по текущему ничего не выключает.
            SearchModeTitles.IsChecked = !byContent;
            SearchModeContent.IsChecked = byContent;

            if (_searchByContent == byContent)
            {
                return;
            }

            _searchByContent = byContent;
            ResetContentSearch();
            RefreshChatList();

            if (byContent)
            {
                SearchBox.Focus();
            }
        }

        /// <summary>Снимает результат прошлого поиска и гасит незавершённый запрос.</summary>
        private void ResetContentSearch()
        {
            _chatSearchCts?.Cancel();
            _chatSearchCts?.Dispose();
            _chatSearchCts = null;
            _contentSearchIds = null;
            _contentSearchState = _searchByContent && !string.IsNullOrWhiteSpace(SearchBox.Text)
                ? ContentSearchState.Prompt
                : ContentSearchState.Off;
        }

        /// <summary>
        /// Показывает переключатель режимов ровно тогда, когда есть что искать, и сбрасывает
        /// результат: он относился к прошлому запросу.
        /// </summary>
        private void SyncSearchModeRow()
        {
            if (SearchModeRow is null)
            {
                return;
            }

            SearchModeRow.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !_searchByContent)
            {
                return;
            }

            e.Handled = true;
            Detached.Run(RunContentSearchAsync(SearchBox.Text), "run_content_search");
        }

        private async Task RunContentSearchAsync(string query)
        {
            if (_services is null || string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            var chats = SummariesForSearch();
            if (chats.Count == 0 || string.IsNullOrWhiteSpace(_services.Options.ApiKey))
            {
                // Ни «ничего не найдено», ни «поиск не удался»: искать было либо не по чему,
                // либо некого спросить, и человеку полезнее знать, что именно из двух.
                _contentSearchIds = [];
                _contentSearchState = string.IsNullOrWhiteSpace(_services.Options.ApiKey)
                    ? ContentSearchState.NoKey
                    : ContentSearchState.Empty;
                RefreshChatList();
                return;
            }

            _chatSearchCts?.Cancel();
            _chatSearchCts?.Dispose();
            var cts = new CancellationTokenSource();
            _chatSearchCts = cts;

            _contentSearchIds = null;
            _contentSearchState = ContentSearchState.Working;
            RefreshChatList();

            try
            {
                var result = await _services.Summaries
                    .SearchAsync(query, chats, cts.Token)
                    .ConfigureAwait(true);

                // Пока модель думала, человек мог сменить запрос или режим — тот поиск уже
                // не про то, что сейчас на экране.
                if (!ReferenceEquals(_chatSearchCts, cts))
                {
                    return;
                }

                _contentSearchIds = [.. result.ChatIds];
                _contentSearchState = ContentSearchState.Done;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                if (!ReferenceEquals(_chatSearchCts, cts))
                {
                    return;
                }

                _contentSearchIds = [];
                _contentSearchState = ContentSearchState.Failed;
            }

            RefreshChatList();
        }
    }
}
