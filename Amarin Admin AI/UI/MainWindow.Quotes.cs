using System.Globalization;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// «Ответить» на фрагмент ответа модели: плашка у выделения, пункт меню и горячая клавиша,
    /// цитаты над полем ввода, подсказка «@» и переход от цитаты к её источнику.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Цитата прикрепляется к сообщению так же, как вложение, и живёт по тем же правилам: её
    /// снимает <see cref="ClearPendingAttachments"/> при уходе из чата, её учитывает
    /// <see cref="RefreshAttachments"/>, когда решает, показывать ли полосу и сворачивать ли
    /// компактное поле. Отличие одно — ссылку на неё можно вписать в текст (<c>@N</c>).
    /// </para>
    /// <para>
    /// Обработчиков на ленте два, и оба висят на всей панели сообщений, а не на каждом боксе:
    /// ленту в сотни ответов не приходится обвешивать подписками, а на потоке живого ответа не
    /// добавляется ни одной операции.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private readonly List<MessageQuote> _pendingQuotes = [];

        /// <summary>
        /// Цитата, под которую открыта плашка. Снята в момент открытия: вьюшку ответа могут
        /// пересобрать (приехала цена сводки), и выделение исчезнет раньше, чем по плашке щёлкнут.
        /// </summary>
        private QuoteDraft? _pillDraft;

        private ChatMessageHost? _pillHost;

        private List<MessageQuote> _suggestItems = [];
        private int _suggestIndex;
        private int _suggestStart = -1;

        private void InitializeQuotes()
        {
            PopupManager.Register(ReplyPill);
            PopupManager.Register(QuoteSuggestPopup);

            // Без handledEventsToo: конец перетаскивания под лупой ChatZoom помечает
            // обработанным — это панорама, а не выделение, и плашке там делать нечего.
            MessagesPanel.AddHandler(
                PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(MessagesPanel_PreviewMouseLeftButtonUp));
            MessagesPanel.AddHandler(
                ContextMenuOpeningEvent,
                new ContextMenuEventHandler(MessagesPanel_ContextMenuOpening));

            MessageTextBox.TextChanged += (_, _) => UpdateQuoteSuggest();
            MessageTextBox.SelectionChanged += (_, _) => UpdateQuoteSuggest();
            MessageTextBox.LostKeyboardFocus += (_, _) => CloseQuoteSuggest();
        }

        // ───────────────────────── Выделение в ответе ─────────────────────────

        private void MessagesPanel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // Выделение ещё не закончено: бокс дописывает его в своём обработчике того же
            // отпускания, и прочитать его можно только следом.
            var origin = e.OriginalSource as DependencyObject;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => EvaluateSelection(origin));
        }

        /// <summary>Открывает плашку «Ответить», если в ответе модели что-то выделено.</summary>
        private void EvaluateSelection(DependencyObject? origin)
        {
            // Мышь захвачена боксом всё время, пока тянется выделение, поэтому отпускание
            // приходит от него самого, даже если кнопку отпустили над соседним сообщением.
            if (QuoteSelection.FindOwner(origin) is not { Quotable: true } owner ||
                owner.Box.Selection.IsEmpty ||
                !CanQuote(owner.Host.Message) ||
                QuoteSelection.Capture(owner.Box, owner.Host.Id) is not { } draft)
            {
                HideReplyPill();
                return;
            }

            _pillDraft = draft;
            _pillHost = owner.Host;
            ReplyPillHint.Text = ReplyGestureText();

            // Переоткрытие ставит плашку под новым положением мыши: уже открытая осталась бы
            // у прошлого выделения.
            ReplyPill.IsOpen = false;
            ReplyPill.IsOpen = true;
        }

        private void ReplyPillButton_Click(object sender, RoutedEventArgs e)
        {
            var draft = _pillDraft;
            HideReplyPill();
            if (draft is not null)
            {
                AttachQuote(draft);
            }
        }

        private void HideReplyPill()
        {
            _pillDraft = null;
            _pillHost = null;
            if (ReplyPill is { IsOpen: true })
            {
                ReplyPill.IsOpen = false;
            }
        }

        /// <summary>Сообщение пересобирают — плашка его выделения больше ни к чему не относится.</summary>
        private void CloseReplyPillFor(ChatMessageHost host)
        {
            if (ReferenceEquals(host, _pillHost))
            {
                HideReplyPill();
            }
        }

        /// <summary>
        /// Можно ли цитировать это сообщение.
        /// </summary>
        /// <remarks>
        /// Живой ответ нельзя: его документ заменяется на каждой перерисовке потока, и
        /// выделение в нём не доживает до щелчка. Упавший — тоже: его текст — это текст ошибки,
        /// а не слова модели.
        /// </remarks>
        private bool CanQuote(ChatDisplayMessage message)
        {
            if (!message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
                message.Status is AssistantStatus.Streaming or AssistantStatus.Error)
            {
                return false;
            }

            return FindTurn(_session.Id) is not { Finished: false } live ||
                   !string.Equals(live.AssistantId, message.Id, StringComparison.Ordinal);
        }

        private string ReplyGestureText()
        {
            var gesture = HotkeyMap.Gesture(_services?.Settings.Hotkeys, HotkeyMap.ReplyToSelection);
            return HotkeyMap.Display(gesture);
        }

        /// <summary>Горячая клавиша «Ответить»: работает, только если выделено в ответе модели.</summary>
        private bool TryReplyToSelection()
        {
            if (Keyboard.FocusedElement is not DependencyObject focused ||
                QuoteSelection.FindOwner(focused) is not { Quotable: true } owner ||
                owner.Box.Selection.IsEmpty ||
                !CanQuote(owner.Host.Message) ||
                QuoteSelection.Capture(owner.Box, owner.Host.Id) is not { } draft)
            {
                return false;
            }

            HideReplyPill();
            AttachQuote(draft);
            return true;
        }

        /// <summary>
        /// Своё меню по правому клику в ленте — в стиле программы, с «Копировать» и «Ответить».
        /// </summary>
        /// <remarks>
        /// Прежде здесь открывалось системное меню текстового поля: серое, с недоступными
        /// «Вырезать» и «Вставить». Чтобы событие вообще дошло до ленты, у боксов стоит
        /// локальный <c>ContextMenu = null</c> (<c>ChatMessageViews.CreateReadOnlyBox</c>).
        /// Меню собирается по требованию — держать по готовому меню на каждом боксе было бы
        /// сотней лишних объектов на длинной переписке.
        /// </remarks>
        private void MessagesPanel_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (QuoteSelection.FindOwner(e.OriginalSource as DependencyObject) is not { } owner ||
                owner.Box.Selection.IsEmpty)
            {
                return;
            }

            e.Handled = true;
            HideReplyPill();

            var box = owner.Box;
            var menu = new ContextMenu
            {
                Style = (Style)FindResource("AppContextMenu"),
                PlacementTarget = box
            };

            // Меню с клавиатуры (Shift+F10, клавиша меню) встаёт у конца выделения, а не там,
            // где случайно оказалась мышь.
            if (e.CursorLeft < 0 || e.CursorTop < 0)
            {
                var anchor = box.Selection.End.GetCharacterRect(LogicalDirection.Backward);
                menu.Placement = PlacementMode.Bottom;
                if (!anchor.IsEmpty)
                {
                    menu.PlacementRectangle = anchor;
                }
            }
            else
            {
                menu.Placement = PlacementMode.MousePoint;
            }

            menu.Items.Add(MenuItemFor(Loc.Get("S.Common.Copy"), () => ApplicationCommands.Copy.Execute(null, box)));

            if (owner.Quotable &&
                CanQuote(owner.Host.Message) &&
                QuoteSelection.Capture(box, owner.Host.Id) is { } draft)
            {
                menu.Items.Add(MenuItemFor(Loc.Get("S.Quote.Reply"), () => AttachQuote(draft)));
            }

            menu.IsOpen = true;
        }

        // ───────────────────────── Цитаты над полем ввода ─────────────────────────

        private void AttachQuote(QuoteDraft draft)
        {
            if (!ChatQuotes.IsDuplicate(_pendingQuotes, draft.SourceId, draft.Text))
            {
                if (_pendingQuotes.Count >= ChatQuotes.MaxQuotes)
                {
                    _attachmentNotes.Clear();
                    _attachmentNotes.Add(Loc.Format("S.Quote.TooMany", ChatQuotes.MaxQuotes));
                    RefreshAttachments();
                    return;
                }

                _pendingQuotes.Add(new MessageQuote
                {
                    Number = ChatQuotes.NextNumber(_pendingQuotes),
                    SourceMessageId = draft.SourceId,
                    Text = draft.Text,
                    Context = draft.Context
                });
                RefreshAttachments();
            }

            // Цитата прикреплена ради ответа на неё — сразу в поле, печатать.
            _compact?.Unfold();
            FocusMessageInput();
        }

        private void RemovePendingQuote(MessageQuote quote)
        {
            _pendingQuotes.Remove(quote);
            _attachmentNotes.Clear();
            RefreshAttachments();
        }

        /// <summary>
        /// Перестраивает строки цитат и подпись поля ввода. Видимость всей полосы решает
        /// <see cref="RefreshAttachments"/>.
        /// </summary>
        private void RefreshQuoteRows()
        {
            QuotesPanel.Children.Clear();
            var format = ActiveDateFormat;
            foreach (var quote in _pendingQuotes)
            {
                var kind = ChatQuotes.Classify(_session.Messages, _session.Messages.Count, quote.SourceMessageId, out var source);
                QuotesPanel.Children.Add(QuoteViews.ComposerRow(
                    this,
                    quote,
                    QuoteViews.SourceLabel(kind, source, format),
                    () => InsertQuoteReference(quote.Number),
                    () => RemovePendingQuote(quote)));
            }

            var any = _pendingQuotes.Count > 0;
            QuotesPanel.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

            // С цитатой пустое поле просит ответа на неё, а не «спросите что-нибудь».
            ComposerPlaceholder.SetResourceReference(
                TextBlock.TextProperty,
                any ? "S.Composer.PlaceholderQuote" : "S.Composer.Placeholder");

            if (!any)
            {
                CloseQuoteSuggest();
            }
        }

        /// <summary>Вписывает «@N» туда, где стоит каретка.</summary>
        /// <remarks>
        /// Через <see cref="TextBox.SelectedText"/>, а не переписыванием всего текста: так
        /// вставка остаётся в истории отмены, и Ctrl+Z убирает её одну.
        /// </remarks>
        private void InsertQuoteReference(int number)
        {
            var box = MessageTextBox;
            box.Focus();
            var start = box.SelectionStart;
            var text = box.Text ?? "";
            var token = ReferenceToken(text, start, start + box.SelectionLength, number);
            box.SelectedText = token;
            box.Select(start + token.Length, 0);
        }

        /// <summary>
        /// «@N» с пробелами ровно там, где их не хватает: ссылка, прилипшая к слову, ссылкой
        /// уже не считается (см. <see cref="ChatQuotes.References"/>).
        /// </summary>
        internal static string ReferenceToken(string text, int start, int end, int number)
        {
            var token = "@" + number.ToString(CultureInfo.InvariantCulture);
            if (start > 0 && !char.IsWhiteSpace(text[start - 1]) && text[start - 1] is not ('(' or '[' or '«' or '"'))
            {
                token = " " + token;
            }

            if (end >= text.Length || !char.IsWhiteSpace(text[end]))
            {
                token += " ";
            }

            return token;
        }

        // ───────────────────────── Подсказка «@» ─────────────────────────

        private void UpdateQuoteSuggest()
        {
            var box = MessageTextBox;
            if (_pendingQuotes.Count == 0 ||
                box.SelectionLength > 0 ||
                !ChatQuotes.TryGetTypingReference(box.Text, box.CaretIndex, out var start, out var digits))
            {
                CloseQuoteSuggest();
                return;
            }

            var items = _pendingQuotes
                .Where(quote => quote.Number.ToString(CultureInfo.InvariantCulture)
                    .StartsWith(digits, StringComparison.Ordinal))
                .ToList();
            if (items.Count == 0)
            {
                CloseQuoteSuggest();
                return;
            }

            // Тот же список — выбор стрелками остаётся на месте; другой — встаёт на первый.
            if (start != _suggestStart || !items.SequenceEqual(_suggestItems))
            {
                _suggestIndex = 0;
            }

            _suggestStart = start;
            _suggestItems = items;
            RenderQuoteSuggest();

            var anchor = box.GetRectFromCharacterIndex(start);
            QuoteSuggestPopup.PlacementRectangle = anchor.IsEmpty ? Rect.Empty : anchor;
            if (!QuoteSuggestPopup.IsOpen)
            {
                QuoteSuggestPopup.IsOpen = true;
            }
        }

        private void RenderQuoteSuggest()
        {
            QuoteSuggestList.Children.Clear();
            var format = ActiveDateFormat;
            for (var i = 0; i < _suggestItems.Count; i++)
            {
                var quote = _suggestItems[i];
                var kind = ChatQuotes.Classify(_session.Messages, _session.Messages.Count, quote.SourceMessageId, out var source);
                QuoteSuggestList.Children.Add(QuoteViews.SuggestRow(
                    this,
                    quote,
                    QuoteViews.SourceLabel(kind, source, format),
                    i == _suggestIndex,
                    () => CommitQuoteSuggest(quote)));
            }
        }

        private void CloseQuoteSuggest()
        {
            _suggestStart = -1;
            _suggestItems = [];
            if (QuoteSuggestPopup is { IsOpen: true })
            {
                QuoteSuggestPopup.IsOpen = false;
            }
        }

        /// <summary>
        /// Стрелки, Enter и Tab, пока открыта подсказка «@». Зовётся из
        /// <c>MessageTextBox_PreviewKeyDown</c> раньше отправки — иначе Enter, выбирающий
        /// цитату, отправлял бы недописанное сообщение.
        /// </summary>
        private bool TryHandleQuoteSuggestKey(KeyEventArgs e)
        {
            if (!QuoteSuggestPopup.IsOpen || _suggestItems.Count == 0 ||
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) != 0)
            {
                return false;
            }

            switch (e.Key)
            {
                case Key.Down:
                    _suggestIndex = (_suggestIndex + 1) % _suggestItems.Count;
                    RenderQuoteSuggest();
                    return true;
                case Key.Up:
                    _suggestIndex = (_suggestIndex - 1 + _suggestItems.Count) % _suggestItems.Count;
                    RenderQuoteSuggest();
                    return true;
                case Key.Enter:
                case Key.Tab:
                    CommitQuoteSuggest(_suggestItems[Math.Clamp(_suggestIndex, 0, _suggestItems.Count - 1)]);
                    return true;
                default:
                    return false;
            }
        }

        private void CommitQuoteSuggest(MessageQuote quote)
        {
            var box = MessageTextBox;
            var start = _suggestStart;
            var caret = box.CaretIndex;
            if (start < 0 || start > caret)
            {
                CloseQuoteSuggest();
                return;
            }

            box.Focus();

            // Набранное «@2» заменяется целиком, пробел перед «@» уже стоит.
            var text = box.Text ?? "";
            var token = "@" + quote.Number.ToString(CultureInfo.InvariantCulture);
            if (caret >= text.Length || !char.IsWhiteSpace(text[caret]))
            {
                token += " ";
            }

            box.Select(start, caret - start);
            box.SelectedText = token;
            box.Select(start + token.Length, 0);
            CloseQuoteSuggest();
        }

        // ───────────────────────── Переход к источнику ─────────────────────────

        /// <summary>Показывает в ленте фрагмент, на который ссылается цитата, и выделяет его.</summary>
        private void ShowQuoteSource(ChatSession session, MessageQuote quote)
        {
            if (!ReferenceEquals(session, _session) ||
                !_messageViews.TryGetValue(quote.SourceMessageId, out var host))
            {
                return;
            }

            ScrollToMessage(quote.SourceMessageId);

            // После BringIntoView сообщения (оно идёт на Loaded): сначала в кадр встаёт ответ,
            // затем внутри него — сам фрагмент.
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => RevealQuote(host, quote));
        }

        private static void RevealQuote(ChatMessageHost host, MessageQuote quote)
        {
            if (!host.IsMaterialized ||
                QuoteSelection.FindQuotableBox(host) is not { } box ||
                QuoteSelection.Locate(box.Document, quote.Text) is not { } range)
            {
                return;
            }

            box.Focus();
            box.Selection.Select(range.Start, range.End);

            var rect = range.Start.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty)
            {
                // С запасом сверху и снизу: строка, прижатая к самой кромке, читается хуже.
                box.BringIntoView(new Rect(rect.X, rect.Y - 60, Math.Max(1, rect.Width), rect.Height + 120));
            }
        }
    }
}
