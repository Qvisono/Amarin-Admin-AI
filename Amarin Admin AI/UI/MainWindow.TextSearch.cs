using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Поиск по тексту всех чатов (D1): выдача находок в боковой панели и переход к сообщению.
    /// </summary>
    public partial class MainWindow
    {
        private CancellationTokenSource? _textIndexBuild;

        /// <summary>Сверяет индекс с описью в фоне — на запуске и после смены профиля.</summary>
        private void StartTextIndexBuild()
        {
            if (_services is null)
            {
                return;
            }

            _textIndexBuild?.Cancel();
            var cancel = _textIndexBuild = new CancellationTokenSource();
            var store = _services.ChatStore;
            var index = _services.TextIndex;
            var drafts = _services.Drafts;
            Detached.Run(Task.Run(() =>
            {
                try
                {
                    index.Build(store.List(), store.TryLoad, cancel.Token);

                    // Тем же проходом — дата начала и цена у записей описи прежних версий: по ним
                    // сортирует список (D5). Один раз за жизнь профиля.
                    if (store.BackfillIndex(cancel.Token) > 0)
                    {
                        Ui(RefreshChatList);
                    }

                    // Черновики чатов, удалённых мимо окна (из другого запуска, из архива), — вон.
                    drafts.Prune(store.List().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal));
                }
                catch (OperationCanceledException)
                {
                    // Сменили профиль посреди сборки — новая уже идёт.
                }
            }), "text_index_build");
        }

        /// <summary>Что сейчас нарисовано выдачей поиска по тексту: запрос, версия индекса, находки.</summary>
        private (string Query, long Version, IReadOnlyList<TextSearchHit> Hits)? _textSearchShown;

        private CancellationTokenSource? _textSearchCancel;

        /// <summary>
        /// Выдача поиска по тексту. Ищет на рабочем потоке, рисует, когда нашлось.
        /// </summary>
        /// <remarks>
        /// До 1.30.0 поиск шёл здесь же, на потоке окна, и на каждую перерисовку списка — а её
        /// зовут и набранная буква, и соседний чат, пока отвечает, по нескольку раз в секунду. На
        /// сотнях переписок это задерживало ввод. Теперь: выдача, верная для этого запроса и этой
        /// версии индекса, не ищется заново; иначе поиск уходит в фон, прежний отменяется, а
        /// нарисованное остаётся на месте, пока новое не готово.
        /// </remarks>
        private void RenderTextSearch(string query)
        {
            // Панель занята выдачей: следующая отрисовка списка обязана его пересобрать.
            _chatListSignature.Clear();
            var index = _services!.TextIndex;
            var needle = query.Trim();
            var version = index.Version;
            if (_textSearchShown is { } shown && shown.Query == needle && shown.Version == version)
            {
                return;
            }

            _textSearchCancel?.Cancel();
            if (needle.Length < 2)
            {
                _textSearchCancel = null;
                ShowTextHits(query, []);
                _textSearchShown = (needle, version, []);
                return;
            }

            var cancel = _textSearchCancel = new CancellationTokenSource();
            Detached.Run(SearchTextAsync(index, query, needle, version, cancel), "text_search");
        }

        private async Task SearchTextAsync(
            ChatTextIndex index,
            string query,
            string needle,
            long version,
            CancellationTokenSource cancel)
        {
            IReadOnlyList<TextSearchHit> hits;
            try
            {
                hits = await Task.Run(() => index.Search(needle, cancel.Token), cancel.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Пока искали, человек мог дописать запрос, выйти из режима или уйти со списка.
            if (!ReferenceEquals(_textSearchCancel, cancel) || !_searchByText || SearchBox.Text.Trim() != needle)
            {
                return;
            }

            // Те же находки — та же выдача: перестраивать двести карточек незачем.
            if (_textSearchShown is { } shown && shown.Query == needle && shown.Hits.SequenceEqual(hits))
            {
                _textSearchShown = (needle, version, shown.Hits);
                return;
            }

            ShowTextHits(query, hits);
            _textSearchShown = (needle, version, hits);
        }

        private void ShowTextHits(string query, IReadOnlyList<TextSearchHit> hits)
        {
            ChatListPanel.Children.Clear();
            if (hits.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = query.Trim().Length < 2 ? Loc.Get("S.Search.TextTooShort") : Loc.Get("S.Common.NothingFound"),
                    FontSize = 11.5,
                    Margin = new Thickness(12, 8, 8, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
                ChatListPanel.Children.Add(empty);
                return;
            }

            var needle = query.Trim();
            foreach (var hit in hits)
            {
                ChatListPanel.Children.Add(BuildTextHit(hit, needle));
            }

            if (hits.Count >= ChatTextIndex.Limit)
            {
                var more = new TextBlock
                {
                    Text = Loc.Format("S.Search.TextLimit", ChatTextIndex.Limit),
                    FontSize = 11,
                    Margin = new Thickness(12, 6, 8, 8),
                    TextWrapping = TextWrapping.Wrap
                };
                more.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
                ChatListPanel.Children.Add(more);
            }
        }

        private Button BuildTextHit(TextSearchHit hit, string needle)
        {
            var title = new TextBlock
            {
                Text = DisplayTitle(hit.Title),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

            var date = new TextBlock
            {
                Text = hit.Created == default ? "" : ChatFormat.DateTimeShort(hit.Created, ActiveDateFormat),
                FontSize = 10.5,
                Margin = new Thickness(0, 1, 0, 0)
            };
            date.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            // Совпадение — жирным и фоном из палитры; всё остальное — приглушённым.
            var snippet = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), MaxHeight = 48 };
            snippet.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
            var start = Math.Clamp(hit.MatchStart, 0, hit.Snippet.Length);
            var length = Math.Clamp(hit.MatchLength, 0, hit.Snippet.Length - start);
            snippet.Inlines.Add(new Run(hit.Snippet[..start]));
            var match = new Run(hit.Snippet.Substring(start, length)) { FontWeight = FontWeights.SemiBold };
            match.SetResourceReference(TextElement.BackgroundProperty, "Status.WarningSurface");
            match.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
            snippet.Inlines.Add(match);
            snippet.Inlines.Add(new Run(hit.Snippet[(start + length)..]));

            var body = new StackPanel();
            body.Children.Add(title);
            body.Children.Add(date);
            body.Children.Add(snippet);

            var button = new Button
            {
                Content = body,
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(4, 0, 4, 4),
                Template = TextHitTemplate()
            };
            System.Windows.Automation.AutomationProperties.SetName(button, DisplayTitle(hit.Title) + ": " + hit.Snippet);
            button.Click += (_, _) => OpenChatAtText(hit.ChatId, hit.MessageId, needle);
            return button;
        }

        private static ControlTemplate? _textHitTemplate;

        /// <summary>Карточка находки — тот же подсвет под курсором, что у строк чатов.</summary>
        private static ControlTemplate TextHitTemplate()
        {
            if (_textHitTemplate is not null)
            {
                return _textHitTemplate;
            }

            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "Bg");
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            border.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 7));
            border.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = border;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Bg.Hover"), "Bg"));
            template.Triggers.Add(hover);
            return _textHitTemplate = template;
        }

        /// <summary>
        /// Открывает чат и ставит поиск внутри него на найденное сообщение: человек видит и
        /// совпадение, и панель, чтобы листать остальные.
        /// </summary>
        private void OpenChatAtText(string chatId, string messageId, string needle)
        {
            if (chatId != _session.Id)
            {
                OpenChat(chatId);
            }

            if (chatId != _session.Id)
            {
                return;
            }

            // После раскладки ленты: хосты сообщений только что поставлены.
            Dispatcher.BeginInvoke(() =>
            {
                FindBar.Visibility = Visibility.Visible;
                FindBar.SetQuery(needle);
                _findHits = ChatFind.Find(_session.Messages, needle);
                var target = _findHits.ToList().FindIndex(hit => hit.MessageId == messageId && !hit.InTools);
                _findIndex = target >= 0 ? target : _findHits.Count - 1;
                FindBar.ShowCount(_findHits.Count == 0 ? 0 : _findIndex + 1, _findHits.Count);
                if (_findIndex >= 0)
                {
                    GoToHit(_findHits[_findIndex], needle);
                }
            }, DispatcherPriority.Loaded);
        }
    }
}
