using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// Everything the model has done to this computer, in one list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opened from the chat and scoped to it, because that is the question people actually ask —
    /// "what did it just do?" — but the scope switch widens it to every conversation on disk,
    /// which is the only honest answer to "what has it ever done?".
    /// </para>
    /// <para>
    /// Nothing here is cached between openings. Reading every chat costs a few hundred
    /// milliseconds on a large history, and a cache would have to be invalidated by any running
    /// turn, any rolled back message and any deleted chat — three ways to show a lie about what
    /// the program did to someone's machine.
    /// </para>
    /// </remarks>
    public partial class MainWindow
    {
        private enum JournalTab
        {
            Actions,
            Snapshots,
            Summaries
        }

        private JournalTab _journalTab = JournalTab.Actions;
        private bool _journalAllChats;

        /// <summary>Rows with their search keys, rebuilt on every load and filtered in place.</summary>
        private List<(JournalRow Row, string Key)> _journalRows = [];

        /// <summary>Guards against a slow "all chats" read landing after the overlay was closed.</summary>
        private int _journalLoadToken;

        /// <summary>The row whose details are on screen; null while the list is showing.</summary>
        private JournalRow? _journalDetail;

        /// <summary>Cancels an explanation still in flight when the user navigates away from it.</summary>
        private CancellationTokenSource? _journalAsk;

        private void OpenJournal()
        {
            JournalOverlay.Visibility = Visibility.Visible;
            Chat.IsHitTestVisible = false;
            ShowJournalList();
            LoadJournal();

            // Focus goes to the overlay, not the search box: the window-level typing sink hands
            // every keystroke to the composer otherwise, and Escape would never arrive here.
            Dispatcher.BeginInvoke(() => JournalOverlay.Focus(), DispatcherPriority.Input);
        }

        private void CloseJournal()
        {
            // Any load still in flight belongs to a journal that no longer exists.
            _journalLoadToken++;

            CancelJournalAsk();
            JournalOverlay.Visibility = Visibility.Collapsed;
            ShowJournalList();
            _journalRows = [];
            JournalList.ItemsSource = null;

            Chat.IsHitTestVisible =
                ConfirmationOverlay.Visibility != Visibility.Visible &&
                DomainOverlay.Visibility != Visibility.Visible;
            FocusMessageInput();
        }

        private void LoadJournal()
        {
            JournalScopePanel.Visibility = _journalTab == JournalTab.Actions
                ? Visibility.Visible
                : Visibility.Collapsed;
            JournalSearchBox.Visibility = Visibility.Visible;

            var token = ++_journalLoadToken;

            if (_journalTab == JournalTab.Snapshots)
            {
                LoadSnapshotsAsync(token);
                return;
            }

            if (_journalTab == JournalTab.Summaries)
            {
                LoadSummaries(token);
                return;
            }

            // Only the widest scope needs the store on disk; the open chat is already in memory,
            // and while a turn is running it is the only copy that is current — reading it back
            // off disk would show a frozen version.
            if (!_journalAllChats || _services is null)
            {
                Publish(token, ActionJournal.FromSession(_session), showChat: false);
                return;
            }

            LoadAllChatsAsync(token);
        }

        private async void LoadAllChatsAsync(int token)
        {
            if (_services is null)
            {
                return;
            }

            ShowJournalBusy();

            var store = _services.ChatStore;
            var open = _session;
            IReadOnlyList<JournalEntry> entries;
            try
            {
                entries = await Task.Run(() =>
                {
                    var collected = ActionJournal.Collect(store).ToList();

                    // The chat on screen may hold a turn that has not been saved yet, so its
                    // entries are taken from memory and the stale copy from disk dropped.
                    collected.RemoveAll(entry => entry.ChatId == open.Id);
                    collected.AddRange(ActionJournal.FromSession(open));
                    collected.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
                    return (IReadOnlyList<JournalEntry>)collected;
                }).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowJournalError(Loc.Get("S.Journal.ReadFailed"));
                return;
            }

            Publish(token, entries, showChat: true);
        }

        private async void LoadSnapshotsAsync(int token)
        {
            ShowJournalBusy();

            IReadOnlyList<SnapshotEntry> snapshots;
            try
            {
                snapshots = await Task.Run(RollbackSnapshots.List).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowJournalError(Loc.Get("S.Journal.ReadFailed"));
                return;
            }

            if (token != _journalLoadToken)
            {
                return;
            }

            _journalRows = snapshots
                .Select(snapshot => (JournalView.ToRow(snapshot), JournalView.SearchKey(snapshot)))
                .ToList();
            ApplyJournalFilter();
        }

        /// <summary>
        /// Сводки читаются из индекса и потому синхронно: там уже лежит всё, что нужно строке,
        /// и ходить за этим в файлы переписок не приходится.
        /// </summary>
        private void LoadSummaries(int token)
        {
            if (_services is null || token != _journalLoadToken)
            {
                return;
            }

            var rows = new List<(JournalRow, string)>();
            foreach (var item in _services.ChatStore.List())
            {
                // Открытый чат в индексе может отставать на последний ответ: сводку ему только
                // что дописали, а на диск он ляжет следующим сохранением.
                var text = string.Equals(item.Id, _session.Id, StringComparison.Ordinal)
                    ? _session.Summary ?? item.Summary
                    : item.Summary;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var entry = new ChatSummaryEntry(
                    item.Id, DisplayTitle(item.Title), text, item.UpdatedAt);
                rows.Add((JournalView.ToRow(entry), JournalView.SearchKey(entry)));
            }

            _journalRows = rows;
            ApplyJournalFilter();
        }

        private void Publish(int token, IReadOnlyList<JournalEntry> entries, bool showChat)
        {
            if (token != _journalLoadToken)
            {
                return;
            }

            _journalRows = entries
                .Select(entry => (JournalView.ToRow(entry, showChat), JournalView.SearchKey(entry)))
                .ToList();
            ApplyJournalFilter();
        }

        private void ApplyJournalFilter()
        {
            var query = JournalSearch.Text.Trim().ToLowerInvariant();
            var rows = query.Length == 0
                ? _journalRows.Select(item => item.Row).ToList()
                : _journalRows.Where(item => item.Key.Contains(query, StringComparison.Ordinal))
                    .Select(item => item.Row)
                    .ToList();

            JournalList.ItemsSource = rows;
            JournalSearchPlaceholder.Visibility = JournalSearch.Text.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (rows.Count > 0)
            {
                JournalEmpty.Visibility = Visibility.Collapsed;
                JournalFooter.Text = query.Length == 0
                    ? Loc.Format("S.Journal.Count", rows.Count)
                    : Loc.Format("S.Journal.CountFiltered", rows.Count, _journalRows.Count);
                return;
            }

            JournalEmpty.Visibility = Visibility.Visible;
            JournalEmpty.Text = Loc.Get(EmptyKey(query.Length > 0));
            JournalFooter.Text = "";
        }

        private string EmptyKey(bool filtered)
        {
            if (filtered)
            {
                return "S.Journal.NoMatches";
            }

            return _journalTab switch
            {
                JournalTab.Snapshots => "S.Journal.NoSnapshots",
                JournalTab.Summaries => "S.Journal.NoSummaries",
                _ => _journalAllChats ? "S.Journal.NoActionsAnywhere" : "S.Journal.NoActionsHere"
            };
        }

        private void ShowJournalBusy()
        {
            JournalList.ItemsSource = null;
            JournalEmpty.Visibility = Visibility.Visible;
            JournalEmpty.Text = Loc.Get("S.Journal.Loading");
            JournalFooter.Text = "";
        }

        private void ShowJournalError(string message)
        {
            _journalRows = [];
            JournalList.ItemsSource = null;
            JournalEmpty.Visibility = Visibility.Visible;
            JournalEmpty.Text = message;
            JournalFooter.Text = "";
        }

        private void SetJournalTab(JournalTab tab)
        {
            _journalTab = tab;
            JournalActionsTab.IsChecked = tab == JournalTab.Actions;
            JournalSnapshotsTab.IsChecked = tab == JournalTab.Snapshots;
            JournalSummariesTab.IsChecked = tab == JournalTab.Summaries;
            LoadJournal();
        }

        private void SetJournalScope(bool allChats)
        {
            _journalAllChats = allChats;
            JournalScopeChat.IsChecked = !allChats;
            JournalScopeAll.IsChecked = allChats;
            LoadJournal();
        }

        private void ShowJournalList()
        {
            CancelJournalAsk();
            _journalDetail = null;

            JournalDetails.Visibility = Visibility.Collapsed;
            JournalList.Visibility = Visibility.Visible;
            JournalBackButton.Visibility = Visibility.Collapsed;
            JournalTabsRow.Visibility = Visibility.Visible;
            JournalSearchBox.Visibility = Visibility.Visible;
            JournalFooter.Visibility = Visibility.Visible;

            // The empty note belongs to the list, and only the filter knows whether it shows;
            // re-running it is also what repopulates the list after a trip to the details screen.
            ApplyJournalFilter();
        }

        private void ShowJournalDetails(JournalRow row)
        {
            CancelJournalAsk();
            _journalDetail = row;

            JournalDetailTitle.Text = row.Title;
            JournalDetailMeta.Text = row.Entry is { } entry
                ? JournalView.BuildMeta(entry)
                : row.Snapshot is { } snapshot ? JournalView.BuildMeta(snapshot) : row.Timestamp;

            // A restore point is already fully described by the two lines above; there is nothing
            // for a model to add, so the button that would promise an explanation is not offered.
            JournalAskButton.Visibility = row.Entry is null ? Visibility.Collapsed : Visibility.Visible;
            JournalAskButton.IsEnabled = true;
            JournalOpenChatButton.Visibility =
                string.IsNullOrEmpty(row.ChatId) ? Visibility.Collapsed : Visibility.Visible;

            // Пересобрать можно только сводку, и только когда ясно, какого она чата.
            JournalRebuildSummaryButton.Visibility =
                row.Summary is null ? Visibility.Collapsed : Visibility.Visible;
            JournalRebuildSummaryButton.IsEnabled = true;
            JournalRebuildSummaryButton.Content = Loc.Get("S.Journal.Summary.Rebuild");

            JournalAnswerBox.Visibility = Visibility.Collapsed;
            JournalAnswerText.Text = "";

            JournalArgumentsHost.Content = row.Entry is { } withArgs
                ? CodeBlockView.Create(this, PrettyJson(withArgs.ArgumentsJson), "json")
                : null;
            JournalResultHost.Content = CodeBlockView.Create(this, ResultOf(row), null);

            JournalEmpty.Visibility = Visibility.Collapsed;
            JournalList.Visibility = Visibility.Collapsed;
            JournalTabsRow.Visibility = Visibility.Collapsed;
            JournalSearchBox.Visibility = Visibility.Collapsed;
            JournalFooter.Visibility = Visibility.Collapsed;
            JournalBackButton.Visibility = Visibility.Visible;
            JournalDetails.Visibility = Visibility.Visible;
            JournalDetails.ScrollToTop();
        }

        private static string ResultOf(JournalRow row)
        {
            if (row.Summary is { } summary)
            {
                return summary.Text;
            }

            if (row.Snapshot is { } snapshot)
            {
                return snapshot.Path;
            }

            if (row.Entry is not { } entry)
            {
                return "";
            }

            var text = string.IsNullOrWhiteSpace(entry.ResultText) ? entry.ResultPreview : entry.ResultText;
            return string.IsNullOrWhiteSpace(text) ? Loc.Get("S.Journal.NoResult") : text;
        }

        /// <summary>
        /// Re-indents the model's arguments. They arrive as whatever the model emitted — usually
        /// one long line — and the point of the details screen is to be readable.
        /// </summary>
        private static string PrettyJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "";
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(json);
                return System.Text.Json.JsonSerializer.Serialize(
                    document.RootElement,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });
            }
            catch (System.Text.Json.JsonException)
            {
                // Models send malformed JSON often enough that ToolArguments exists for it. Here
                // the raw text is still the honest answer to "what was passed".
                return json;
            }
        }

        private void CancelJournalAsk()
        {
            try
            {
                _journalAsk?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _journalAsk?.Dispose();
            _journalAsk = null;
        }

        private async void AskAboutEntryAsync(JournalEntry entry)
        {
            if (_services is null)
            {
                return;
            }

            CancelJournalAsk();
            var cancellation = new CancellationTokenSource();
            _journalAsk = cancellation;

            JournalAskButton.IsEnabled = false;
            JournalAnswerBox.Visibility = Visibility.Visible;
            JournalAnswerText.Text = Loc.Get("S.Journal.Asking");

            string answer;
            try
            {
                answer = await JournalExplainer.ExplainAsync(
                        _services.Venice,
                        _services.Settings.AgentFastModelId,
                        entry,
                        cancellation.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // The user may have gone back to the list, or opened another row, while this was out.
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_journalAsk, cancellation))
            {
                return;
            }

            JournalAnswerText.Text = answer;
            JournalAskButton.IsEnabled = true;
        }

        // ── Handlers ──────────────────────────────────────────────────────────────────

        private void JournalButton_Click(object sender, RoutedEventArgs e) => OpenJournal();

        private void JournalCloseButton_Click(object sender, RoutedEventArgs e) => CloseJournal();

        private void JournalActionsTab_Click(object sender, RoutedEventArgs e) =>
            SetJournalTab(JournalTab.Actions);

        private void JournalSnapshotsTab_Click(object sender, RoutedEventArgs e) =>
            SetJournalTab(JournalTab.Snapshots);

        private void JournalSummariesTab_Click(object sender, RoutedEventArgs e) =>
            SetJournalTab(JournalTab.Summaries);

        private void JournalScopeChat_Click(object sender, RoutedEventArgs e) => SetJournalScope(false);

        private void JournalScopeAll_Click(object sender, RoutedEventArgs e) => SetJournalScope(true);

        private void JournalSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (JournalOverlay.Visibility == Visibility.Visible)
            {
                ApplyJournalFilter();
            }
        }

        private void JournalRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: JournalRow row })
            {
                ShowJournalDetails(row);
            }
        }

        private void JournalBackButton_Click(object sender, RoutedEventArgs e) => ShowJournalList();

        private void JournalOpenChatButton_Click(object sender, RoutedEventArgs e)
        {
            var chatId = _journalDetail?.ChatId;
            CloseJournal();

            if (!string.IsNullOrEmpty(chatId) && chatId != _session.Id)
            {
                OpenChat(chatId);
            }
        }

        private void JournalAskButton_Click(object sender, RoutedEventArgs e)
        {
            if (_journalDetail?.Entry is { } entry)
            {
                AskAboutEntryAsync(entry);
            }
        }

        private async void JournalRebuildSummaryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_journalDetail?.Summary is not { } summary)
            {
                return;
            }

            JournalRebuildSummaryButton.IsEnabled = false;
            JournalRebuildSummaryButton.Content = Loc.Get("S.Journal.Summary.Rebuilding");

            var ok = await RebuildSummaryAsync(summary.ChatId, CancellationToken.None).ConfigureAwait(true);

            // Пока модель пересобирала, человек мог уйти со страницы подробностей на другую
            // строку или закрыть журнал — тогда трогать кнопку уже нельзя, она не про этот чат.
            if (_journalDetail?.Summary is not { } current ||
                !string.Equals(current.ChatId, summary.ChatId, StringComparison.Ordinal))
            {
                return;
            }

            JournalRebuildSummaryButton.IsEnabled = true;
            JournalRebuildSummaryButton.Content = Loc.Get("S.Journal.Summary.Rebuild");

            if (!ok)
            {
                JournalAnswerBox.Visibility = Visibility.Visible;
                JournalAnswerText.Text = Loc.Get("S.Journal.Summary.Failed");
                return;
            }

            // Строка в списке под нами держит прежний текст: перечитываем её вместе со сводкой.
            var chatId = summary.ChatId;
            LoadJournal();
            var updated = _journalRows
                .Select(item => item.Row)
                .FirstOrDefault(item => string.Equals(item.Summary?.ChatId, chatId, StringComparison.Ordinal));
            if (updated is not null)
            {
                ShowJournalDetails(updated);
            }
            else
            {
                ShowJournalList();
            }
        }

        private void Journal_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    if (_journalDetail is null)
                    {
                        CloseJournal();
                    }
                    else
                    {
                        ShowJournalList();
                    }

                    e.Handled = true;
                    break;

                // Typing anywhere on the card goes to the search box, the way Ctrl+K dialogs
                // behave: hunting for the field first is a step nobody wants.
                case Key.Down or Key.Up or Key.PageDown or Key.PageUp:
                    break;

                default:
                    if (!JournalSearch.IsKeyboardFocusWithin && IsTypingKey(e.Key))
                    {
                        JournalSearch.Focus();
                        JournalSearch.CaretIndex = JournalSearch.Text.Length;
                    }

                    break;
            }
        }

        private static bool IsTypingKey(Key key) =>
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Control) &&
            !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) &&
            (key is >= Key.A and <= Key.Z ||
             key is >= Key.D0 and <= Key.D9 ||
             key is >= Key.NumPad0 and <= Key.NumPad9 ||
             key is Key.Back or Key.Space or Key.OemMinus or Key.OemPeriod or Key.OemComma);
    }
}
