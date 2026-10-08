using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Выбор чатов шире Ctrl/Shift+щелчка (1.32.0): рамкой по колонке, Ctrl+A, Shift+Del, Esc,
    /// Ctrl+щелчок по заголовку папки и меню по правому щелчку для всего выбора.
    /// </summary>
    /// <remarks>
    /// Что выбрано, держит <see cref="ChatSelection"/>; какие чаты стоят за заголовками папок и
    /// архива — <see cref="ChatListMembersNow"/>, собранный по раскладке списка тем же
    /// фильтром. Поэтому рамка по свёрнутой папке выбирает и её невидимые чаты, а Ctrl+A при
    /// фильтре по тегу — только то, что фильтр показывает: удалить то, чего не видно, нельзя.
    /// </remarks>
    public partial class MainWindow
    {
        private ChatListMarquee? _marquee;

        /// <summary>Что остаётся выбранным под рамкой: прежний выбор с Ctrl, ничего — без него.</summary>
        private ChatSelectionBasis? _sweepBasis;

        /// <summary>Выбор до рамки — его возвращает Esc, с Ctrl рамка шла или без.</summary>
        private ChatSelectionBasis? _beforeSweep;

        /// <summary>Как посчитать чаты за заголовками того, что сейчас показывает список.</summary>
        private Func<ChatListMembers>? _chatListMembersSource;

        private ChatListMembers? _chatListMembers;

        /// <summary>Чаты за заголовками того, что сейчас показывает список.</summary>
        /// <remarks>
        /// Считаются по первому вопросу после пересборки, а не при каждой: без выбора их не
        /// спрашивает никто, а пересборок списка за сеанс сотни, и первая из них — на запуске.
        /// Источник держит снимки той пересборки (строки, раскладку, фильтр), поэтому поздний
        /// счёт видит ровно то, что показано.
        /// </remarks>
        private ChatListMembers ChatListMembersNow =>
            _chatListMembers ??= _chatListMembersSource?.Invoke() ?? ChatListMembers.Empty;

        private void SetChatListMembers(Func<ChatListMembers>? source)
        {
            _chatListMembersSource = source;
            _chatListMembers = null;
        }

        /// <summary>Список просили пересобрать посреди рамки — это случится, когда она кончится.</summary>
        private bool _chatListRefreshPending;

        private void InitializeChatSelection()
        {
            // Саму рамку заводит первое нажатие в колонке: в конструкторе окна она стоила около
            // 7 мс холодного запуска (компиляция и загрузка её кода), а нужна, только когда за неё
            // возьмутся.
            SideBarScrollViewer.PreviewMouseLeftButtonDown += SideBarScrollViewer_FirstPress;

            // Своё меню по правому щелчку: для строки — её меню «⋯», для выбранного — всего выбора.
            ChatListPanel.AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler(ChatListPanel_ContextMenuOpening));
            SideBarScrollViewer.ContextMenuOpening += SideBarScrollViewer_ContextMenuOpening;
        }

        /// <summary>Рамка выделения колонки чатов; создаётся при первом обращении.</summary>
        private ChatListMarquee Marquee => _marquee ??= CreateMarquee();

        private void SideBarScrollViewer_FirstPress(object sender, MouseButtonEventArgs e)
        {
            SideBarScrollViewer.PreviewMouseLeftButtonDown -= SideBarScrollViewer_FirstPress;

            // Свой обработчик рамка вешает на то же событие, но в уже идущий маршрут он не попадает:
            // это нажатие ей передаётся вручную.
            Marquee.OnPress(e);
        }

        private ChatListMarquee CreateMarquee()
        {
            // Выдача поиска по тексту — карточки находок, а не строки чатов: выбирать в ней нечего.
            var marquee = new ChatListMarquee(
                SideBarScrollViewer,
                ChatListPanel,
                ChatMarqueeLayer,
                () => !_sidebarCollapsed && _chatDrag?.IsDragging != true && _textSearchShown is null);

            marquee.Began += additive =>
            {
                // Строки посреди раскрытия папки дали бы рамке неверные места.
                FinishChatListMotion();
                _beforeSweep = _selection.BeginSweep(additive: true);
                _sweepBasis = additive ? _beforeSweep : ChatSelectionBasis.Empty;
            };
            marquee.Swept += rows =>
            {
                var (chats, folders) = Expand(rows);
                _selection.Sweep(_sweepBasis ?? ChatSelectionBasis.Empty, chats, folders);
                RefreshChatRowStates();
            };
            marquee.Ended += () => FinishSweep(focus: true);
            marquee.Cancelled += () =>
            {
                _selection.Sweep(_beforeSweep ?? ChatSelectionBasis.Empty, [], []);
                RefreshChatRowStates();
                FinishSweep(focus: false);
            };
            marquee.ClickedEmpty += () =>
            {
                FocusChatList();
                ClearChatSelection();
            };

            return marquee;
        }

        /// <summary>Рамка кончилась: отложенная посреди жеста перерисовка списка идёт сейчас.</summary>
        private void FinishSweep(bool focus)
        {
            _sweepBasis = null;
            _beforeSweep = null;
            if (focus)
            {
                FocusChatList();
            }

            if (_chatListRefreshPending)
            {
                _chatListRefreshPending = false;
                RefreshChatList();
            }
        }

        /// <summary>
        /// Клавиатуру — колонке: после рамки Ctrl+A и Shift+Del про чаты, а набранный текст окно
        /// по-прежнему отдаёт полю ввода (<see cref="ShouldKeepKeyboardFocus"/>).
        /// </summary>
        private void FocusChatList()
        {
            if (!SideBarScrollViewer.IsKeyboardFocusWithin)
            {
                _ = SideBarScrollViewer.Focus();
            }
        }

        /// <summary>Накрытые строки — в чаты и папки: папка — это все её чаты, архив — все его.</summary>
        private (List<string> Chats, List<string> Folders) Expand(IReadOnlyList<FrameworkElement> rows)
        {
            var chats = new List<string>();
            var folders = new List<string>();
            foreach (var row in rows)
            {
                switch (row.Tag)
                {
                    case string id:
                        chats.Add(id);
                        break;
                    case ChatFolder folder:
                        folders.Add(folder.Id);
                        chats.AddRange(ChatListMembersNow.Of(folder.Id));
                        break;
                    case ChatListArchive:
                        chats.AddRange(ChatListMembersNow.Archived);
                        break;
                }
            }

            return (chats, folders);
        }

        /// <summary>Папки, выбранные целиком: «Удалить» уносит их вместе с чатами.</summary>
        private IReadOnlyList<string> SelectedFolders() => _selection.WholeFolders(ChatListMembersNow);

        /// <summary>
        /// Заголовок папки выбран, когда папка выбрана целиком; заголовок архива — когда выбраны все
        /// его чаты. Зовётся вместе с признаками строк.
        /// </summary>
        private void RefreshHeaderSelection()
        {
            // Без выбора заголовки просто гаснут: состав папок для этого не нужен, а считать его
            // на каждой пересборке списка (и на запуске) было бы лишней работой.
            var any = !_selection.IsEmpty;
            var whole = any ? SelectedFolders().ToHashSet(StringComparer.Ordinal) : null;
            var archiveSelected = any && ChatListMembersNow.Archived is { Count: > 0 } archive && archive.All(_selection.Contains);
            foreach (var child in ChatListPanel.Children)
            {
                switch (child)
                {
                    case Button { Tag: ChatFolder folder } header:
                        Flip(header, ChatRowState.IsSelectedProperty, whole?.Contains(folder.Id) == true);
                        break;
                    case Button { Tag: ChatListArchive } header:
                        Flip(header, ChatRowState.IsSelectedProperty, archiveSelected);
                        break;
                }
            }
        }

        /// <summary>Ctrl+щелчок по заголовку папки: выбрать её целиком или снять.</summary>
        private void ToggleFolderSelection(string folderId)
        {
            _selection.ToggleFolder(folderId, ChatListMembersNow.Of(folderId));
            RefreshChatRowStates();
        }

        /// <summary>Ctrl+щелчок по заголовку архива: выбрать все его чаты или снять.</summary>
        private void ToggleArchiveSelection()
        {
            var archived = ChatListMembersNow.Archived;
            if (archived.Count > 0 && archived.All(_selection.Contains))
            {
                _selection.Remove(archived);
            }
            else
            {
                _selection.Sweep(_selection.BeginSweep(additive: true), archived, []);
            }

            RefreshChatRowStates();
        }

        // ───────────────────────── клавиши ─────────────────────────

        /// <summary>Ctrl+A: все чаты списка — с папками и архивом, свёрнутые тоже.</summary>
        /// <remarks>
        /// В поле с текстом и в блоке кода сочетание остаётся их «выделить всё»: действие
        /// отказывается, и клавиша уходит полю. В пустом поле ввода выделять нечего — там Ctrl+A
        /// про чаты.
        /// </remarks>
        private bool TrySelectAllChats()
        {
            if (!ChatListKeysAvailable() || _sidebarCollapsed || FocusedTextSelectsAll())
            {
                return false;
            }

            _selection.SelectAll(ChatListMembersNow);
            RefreshChatRowStates();
            return true;
        }

        /// <summary>Shift+Del: выбранные чаты и папки, а без выбора — открытый чат. С вопросом.</summary>
        /// <remarks>В поле с выделенным текстом Shift+Delete — «вырезать», и действие отказывается.</remarks>
        private bool TryDeleteChatsByKey()
        {
            if (_services is null || !ChatListKeysAvailable() || FocusedTextHasSelection())
            {
                return false;
            }

            if (!_selection.IsEmpty)
            {
                Detached.Run(DeleteChatsAsync(SelectedChats(), SelectedFolders()), "delete_chats");
                return true;
            }

            // Новый, ещё не сохранённый чат удалять нечем: его нет ни на диске, ни в списке.
            if (!_services.ChatStore.List().Any(entry => entry.Id == _session.Id))
            {
                return false;
            }

            Detached.Run(DeleteChatsAsync([_session.Id], []), "delete_chat");
            return true;
        }

        /// <summary>Esc снимает выбор — раньше остановки ответа: снять выбор просят чаще.</summary>
        private bool TryClearSelectionByEscape(KeyEventArgs e)
        {
            if (e.Key != Key.Escape || Keyboard.Modifiers != ModifierKeys.None ||
                _selection.IsEmpty || !ChatListKeysAvailable() || FocusedTextSelectsAll())
            {
                return false;
            }

            ClearChatSelection();
            return true;
        }

        /// <summary>
        /// Клавиатура у основного окна: поверх не открыт ни один слой (настройки, диалог,
        /// уведомление, блокировка), а фокус — в колонке чатов, ленте или поле ввода.
        /// </summary>
        /// <remarks>
        /// Слои — по видимости детей <c>ScaledRoot</c>, а не списком имён: диалог, заведённый
        /// рядом потом, попадёт сюда сам. Первый ребёнок — само окно с колонкой и лентой.
        /// </remarks>
        private bool ChatListKeysAvailable()
        {
            if (_services is null || IsLocked || ChatBlocked ||
                ScaledRoot.Children.OfType<UIElement>().Skip(1).Any(layer => layer.Visibility == Visibility.Visible))
            {
                return false;
            }

            return Keyboard.FocusedElement is not DependencyObject focused ||
                   ReferenceEquals(focused, this) ||
                   IsInside(SidebarBorder, focused) ||
                   IsInside(Chat, focused);
        }

        private static bool FocusedTextSelectsAll() => TextOwnsSelectAll(Keyboard.FocusedElement);

        private static bool FocusedTextHasSelection() => TextOwnsCut(Keyboard.FocusedElement);

        /// <summary>
        /// Ctrl+A принадлежит полю с фокусом: в нём есть текст или это блок кода. Пустое поле
        /// ввода — не в счёт: выделять в нём нечего, и Ctrl+A там про чаты.
        /// </summary>
        internal static bool TextOwnsSelectAll(object? focused) => focused switch
        {
            TextBox { IsReadOnly: false } box => box.Text.Length > 0,
            RichTextBox { IsReadOnly: false } => true,
            PasswordBox => true,
            RichTextBox box => CodeBlockView.IsCode(box),
            _ => false
        };

        /// <summary>Shift+Delete принадлежит полю с фокусом: в нём выделен текст — это «вырезать».</summary>
        internal static bool TextOwnsCut(object? focused) => focused switch
        {
            TextBox { IsReadOnly: false } box => box.SelectionLength > 0,
            RichTextBox { IsReadOnly: false } box => !box.Selection.IsEmpty,
            PasswordBox => true,
            _ => false
        };

        // ───────────────────────── меню по правому щелчку ─────────────────────────

        /// <summary>
        /// Правый щелчок (или клавиша меню) по строке: на выбранной — меню всего выбора, на
        /// невыбранной — меню этой строки, а выбор снимается, как в Проводнике.
        /// </summary>
        /// <remarks>
        /// У заголовка папки своё меню (переименовать, удалить папку) — оно и откроется, если папка
        /// не выбрана: событие тогда не помечается обработанным.
        /// </remarks>
        private void ChatListPanel_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (_services is null || e.OriginalSource is not DependencyObject origin || RowOf(origin) is not { } row)
            {
                return;
            }

            var placement = e.CursorLeft < 0 || e.CursorTop < 0 ? PlacementMode.Bottom : PlacementMode.MousePoint;
            switch (row.Tag)
            {
                case string id:
                    e.Handled = true;
                    OpenChatActionsMenu(row, id, placement);
                    break;
                case ChatFolder or ChatListArchive when ChatRowState.GetIsSelected(row):
                    e.Handled = true;
                    OpenSelectionMenu(row, placement);
                    break;
            }
        }

        /// <summary>
        /// Клавиша меню (или Shift+F10), когда клавиатура у самой колонки, а не у строки: меню всего
        /// выбора, то же, что по правому щелчку на выбранной строке.
        /// </summary>
        /// <remarks>
        /// Полосы с кнопками над списком нет (1.32.0), и без этого с клавиатуры до действий над
        /// выбором было бы не добраться: после рамки и Ctrl+A фокус стоит на колонке. Правый щелчок
        /// по пустому месту меню не открывает — у пустого места своего меню нет, как в Проводнике.
        /// </remarks>
        private void SideBarScrollViewer_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var fromKeyboard = e.CursorLeft < 0 && e.CursorTop < 0;
            if (e.Handled || !fromKeyboard || _selection.IsEmpty || _sidebarCollapsed)
            {
                return;
            }

            e.Handled = true;

            // Под первой выбранной строкой, что видна; выбранное целиком за краем — посреди колонки.
            var anchor = ChatListPanel.Children.OfType<Button>()
                .FirstOrDefault(row => ChatRowState.GetIsSelected(row) && InSidebarView(row));
            if (anchor is null)
            {
                OpenSelectionMenu(SideBarScrollViewer, PlacementMode.Center);
                return;
            }

            OpenSelectionMenu(anchor, PlacementMode.Bottom);
        }

        private bool InSidebarView(FrameworkElement row)
        {
            var top = row.TranslatePoint(default, SideBarScrollViewer).Y;
            return top >= 0 && top + row.ActualHeight <= SideBarScrollViewer.ViewportHeight;
        }

        /// <summary>Прямой ребёнок списка, внутри которого щёлкнули: строка чата или заголовок.</summary>
        private Button? RowOf(DependencyObject? node)
        {
            while (node is not null && !ReferenceEquals(node, ChatListPanel))
            {
                if (node is Button button && ReferenceEquals(VisualTreeHelper.GetParent(button), ChatListPanel))
                {
                    return button;
                }

                node = node is Visual
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return null;
        }
    }
}
