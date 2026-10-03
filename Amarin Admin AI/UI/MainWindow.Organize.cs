using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Amarin.Core;
using Path = System.Windows.Shapes.Path;

namespace Amarin.UI
{
    /// <summary>
    /// Упорядочивание списка чатов (D5): папки, теги, архив, сортировка, множественный выбор с
    /// пакетными действиями и ширина боковой панели.
    /// </summary>
    /// <remarks>
    /// Раскладку считает чистая <see cref="ChatListLayout"/>, здесь — только её отрисовка и меню.
    /// Заголовки папок и архива — кнопки, но их <c>Tag</c> не строка: по строке
    /// <see cref="FindChatRow"/> узнаёт строку чата, и щелчок по папке открывал бы «чат».
    /// Чаты перетаскиваются по разделам мышью (<see cref="ChatListDrag"/>), поэтому колонка не
    /// листается зажатой кнопкой: два жеста на одном нажатии спорили бы.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly HashSet<string> _selectedChats = new(StringComparer.Ordinal);
        private string? _selectionAnchor;
        private bool _archiveExpanded;
        private string? _tagFilter;
        private ChatListDrag? _chatDrag;

        /// <summary>У строк списка есть разделы, куда бросать: в выдаче поиска их нет.</summary>
        private bool _chatListDroppable;

        private void InitializeChatDrag()
        {
            _chatDrag = new ChatListDrag(
                ChatListPanel,
                SideBarScrollViewer,
                ChatDragLayer,
                () => _chatListDroppable && !_sidebarCollapsed,
                id => _selectedChats.Count > 1 && _selectedChats.Contains(id) ? SelectedChats() : [id],
                DropChats);
            _chatDrag.Ended += () =>
            {
                if (_chatDrag.RefreshPending)
                {
                    _chatDrag.RefreshPending = false;
                    RefreshChatList();
                }
            };
        }

        /// <summary>Чаты брошены в раздел боковой панели — см. <see cref="ChatDrop"/>.</summary>
        private void DropChats(IReadOnlyList<string> ids, ChatDropTarget target)
        {
            if (_services is null)
            {
                return;
            }

            var pinned = _services.ChatStore.List()
                .Where(entry => entry.IsPinned)
                .Select(entry => entry.Id)
                .ToHashSet(StringComparer.Ordinal);
            var plan = ChatDrop.Plan(
                ids.Select(id => new ChatDropSource(id, pinned.Contains(id), _services.Organizer.PlacementOf(id))),
                target);
            if (plan.IsEmpty)
            {
                return;
            }

            foreach (var id in plan.Unpin)
            {
                _services.ChatStore.SetPinned(id, false);
            }

            foreach (var id in plan.Pin)
            {
                _services.ChatStore.SetPinned(id, true);
            }

            if (plan.Unarchive.Count > 0)
            {
                _services.Organizer.SetArchived(plan.Unarchive, false);
            }

            if (plan.Archive.Count > 0)
            {
                _services.Organizer.SetArchived(plan.Archive, true);
            }

            if (plan.Move.Count > 0)
            {
                _services.Organizer.MoveToFolder(plan.Move, plan.FolderId);
            }

            // Брошенное в свёрнутую папку пропало бы из вида — папка раскрывается и показывает его.
            if (plan.FolderId is { } folder)
            {
                _services.Organizer.SetCollapsed(folder, false);
            }

            _selectedChats.Clear();
            _selectionAnchor = null;
            RefreshChatList();
            UpdateBatchBar();
        }

        private ChatOrganizer.State? _organizeSnapshot;
        private ChatOrganizer? _organizeSnapshotSource;
        private int _organizeSnapshotVersion;

        /// <summary>
        /// Снимок раскладки для списка чатов. Пока раскладка не менялась, окно держит свою копию:
        /// снимок — это копия каждого назначения, а список обновляется по несколько раз в секунду.
        /// Копию окно только читает, поэтому делить её между обновлениями безопасно.
        /// </summary>
        private ChatOrganizer.State OrganizeSnapshot()
        {
            var organizer = _services!.Organizer;

            // Версию — до снимка: изменение между ними оставит версию старой, и следующий
            // вызов снимет заново, а не потеряет правку.
            var version = organizer.Version;
            if (_organizeSnapshot is null ||
                !ReferenceEquals(_organizeSnapshotSource, organizer) ||
                _organizeSnapshotVersion != version)
            {
                _organizeSnapshot = organizer.Snapshot();
                _organizeSnapshotSource = organizer;
                _organizeSnapshotVersion = version;
            }

            return _organizeSnapshot;
        }

        /// <summary>Смена профиля: выбор, фильтр и раскрытый архив относились к прежнему.</summary>
        private void ResetChatListView()
        {
            _selectedChats.Clear();
            _selectionAnchor = null;
            _tagFilter = null;
            _archiveExpanded = false;
            InvalidateChatListSignature();
        }

        // ───────────────────────── Отрисовка ─────────────────────────

        /// <param name="droppable">
        /// Раскладка по разделам, а не выдача поиска: каждому элементу проставляется раздел, куда
        /// упадёт брошенный на него чат.
        /// </param>
        private void RenderChatListNodes(IReadOnlyList<ChatListNode> nodes, bool droppable)
        {
            _chatListDroppable = droppable;

            // Раздел текущей группы и раздел раскрытой папки или архива — для вложенных строк.
            ChatDropTarget? section = null;
            ChatDropTarget? nested = null;
            for (var i = 0; i < nodes.Count; i++)
            {
                // Чаты раскрытой папки идут сразу за её заголовком, и карточку папки замыкает
                // последний из них — чей сосед снизу уже не вложенный чат.
                var nextNested = i + 1 < nodes.Count && nodes[i + 1] is ChatListChat { Nested: true };
                FrameworkElement element;
                switch (nodes[i])
                {
                    case ChatListGroup group:
                        var header = new TextBlock
                        {
                            Style = (Style)ChatListPanel.FindResource("GroupHeader"),
                            Text = Loc.Get(group.TitleKey)
                        };
                        if (i == 0)
                        {
                            // Верхний заголовок стоит прямо под поиском — ему нужно меньше воздуха.
                            header.Margin = new Thickness(14, 6, 6, 4);
                        }

                        section = droppable ? SectionOf(group.TitleKey) : null;
                        ChatRowState.SetDropTarget(header, section);
                        element = header;
                        break;
                    case ChatListFolder folder:
                        nested = droppable ? ChatDropTarget.Folder(folder.Folder.Id) : null;
                        element = Banded(BuildFolderHeader(folder), folder.Folder.Collapsed, nextNested);
                        ChatRowState.SetDropTarget(element, nested);
                        break;
                    case ChatListArchive archive:
                        nested = droppable ? ChatDropTarget.Archive : null;
                        element = Banded(BuildArchiveHeader(archive), !archive.Expanded, nextNested);
                        ChatRowState.SetDropTarget(element, nested);
                        break;
                    case ChatListChat chat:
                        var row = BuildChatRow(chat);
                        if (chat.Nested)
                        {
                            ChatRowState.SetBand(row, nextNested ? FolderBand.Middle : FolderBand.Bottom);
                        }

                        ChatRowState.SetDropTarget(row, chat.Nested ? nested : section);
                        element = row;
                        break;
                    default:
                        continue;
                }

                ChatListPanel.Children.Add(element);
            }
        }

        /// <summary>Раздел группы по её заголовку. Над папками заголовок общий — бросать туда некуда.</summary>
        private static ChatDropTarget? SectionOf(string titleKey) => titleKey switch
        {
            "S.ChatList.Pinned" => ChatDropTarget.Pinned,
            "S.ChatList.Folders" => null,
            _ => ChatDropTarget.Loose
        };

        /// <summary>
        /// Раскрытая папка с чатами — верх карточки; свёрнутая и пустая — карточка из одной
        /// строки. Голой строкой папка не бывает: тогда раскрытие меняло бы высоту заголовка.
        /// </summary>
        private static Button Banded(Button header, bool collapsed, bool hasRows)
        {
            ChatRowState.SetBand(header, !collapsed && hasRows ? FolderBand.Top : FolderBand.Single);
            return header;
        }

        private Button BuildChatRow(ChatListChat chat)
        {
            var button = new Button
            {
                Content = DisplayTitle(chat.Entry.Title),
                Tag = chat.Entry.Id,
                Style = (Style)ChatListPanel.FindResource("ChatItem")
            };

            // Закрепление живёт только в описи, а меню действий читает его со строки.
            ChatRowState.SetIsPinned(button, chat.Entry.IsPinned);

            // Подсказка: полное название (в строке оно обрезано) и цена чата целиком (E3).
            var tip = DisplayTitle(chat.Entry.Title) + "\n" + ChatFormat.DateTimeShort(chat.Entry.UpdatedAt, ActiveDateFormat);
            if (chat.Entry.TotalCost > 0m)
            {
                tip += " · " + ChatFormat.Cost(new VeniceCost { Usd = chat.Entry.TotalCost, HasData = true });
            }

            button.ToolTip = tip;

            // Ссылкой на ресурс, а не кистью: точки обязаны перекраситься вместе с темой.
            DependencyProperty[] slots = [ChatRowState.Tag1Property, ChatRowState.Tag2Property, ChatRowState.Tag3Property];
            for (var i = 0; i < Math.Min(slots.Length, chat.Tags.Count); i++)
            {
                button.SetResourceReference(slots[i], chat.Tags[i].Color);
            }

            ApplyChatRowState(button, chat.Entry.Id);
            return button;
        }

        private Button BuildFolderHeader(ChatListFolder node)
        {
            var folder = node.Folder;
            var open = !folder.Collapsed;
            var button = BuildGroupButton(folder.Name, node.Count, open, open ? FolderOpenGlyph : FolderGlyph);
            button.Tag = folder;
            button.Click += (_, e) =>
            {
                e.Handled = true;
                _services?.Organizer.SetCollapsed(folder.Id, !folder.Collapsed);
                RefreshChatList();
            };

            var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenu") };
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.RenameFolder"), () => RenameFolder(folder), icon: "Icon.Menu.Rename"));
            menu.Items.Add(MenuItemFor(
                Loc.Get("S.ChatList.DeleteFolder"),
                () => Detached.Run(DeleteFolderAsync(folder), "delete_folder"),
                danger: true,
                icon: "Icon.Menu.Delete"));
            button.ContextMenu = menu;
            return button;
        }

        private Button BuildArchiveHeader(ChatListArchive node)
        {
            var button = BuildGroupButton(Loc.Get("S.ChatList.ArchiveGroup"), node.Count, node.Expanded, ArchiveGlyph);
            button.Tag = node;
            button.Click += (_, e) =>
            {
                e.Handled = true;
                _archiveExpanded = !_archiveExpanded;
                RefreshChatList();
            };
            return button;
        }

        // Значки в точках, без растяжения: открытая и закрытая папка обязаны совпадать по
        // размеру и месту, иначе при раскрытии имя папки дёргалось бы вбок.
        private const string FolderGlyph = "M1.5,2.5 L5.5,2.5 L7,4 L13.5,4 L13.5,11.5 L1.5,11.5 Z";
        private const string FolderOpenGlyph = "M1.5,11.5 L1.5,2.5 L5.5,2.5 L7,4 L12.5,4 L12.5,6 M1.5,11.5 L3.5,6 L14.5,6 L12.5,11.5 Z";
        private const string ArchiveGlyph = "M1.5,2 L13.5,2 L13.5,5 L1.5,5 Z M2.5,5 L2.5,11.5 L12.5,11.5 L12.5,5 M5.5,8 L9.5,8";

        /// <summary>
        /// Заголовок сворачиваемой группы: значок, имя, справа число чатов (у свёрнутой) и шеврон.
        /// Значок стоит там, где у чатов начинается название, а название чата в папке — под именем
        /// папки: так видно, что чат лежит внутри.
        /// </summary>
        private Button BuildGroupButton(string name, int count, bool open, string glyph)
        {
            var icon = new Path
            {
                Data = Glyphs.Get(glyph),
                Width = 15,
                Height = 13,
                Stretch = Stretch.None,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(Shape.StrokeProperty, open ? "Text.Muted" : "Text.Dim");

            var label = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

            // Число — только у свёрнутой: у раскрытой чаты и так перед глазами.
            var number = new TextBlock
            {
                Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = open ? Visibility.Collapsed : Visibility.Visible
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            var chevron = new Path
            {
                Data = Glyphs.Get("M1,1 L5,5 L9,1"),
                Width = 10,
                Height = 6,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(open ? 180 : 0)
            };
            chevron.SetResourceReference(Shape.StrokeProperty, "Text.Faint");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(label, 1);
            Grid.SetColumn(number, 2);
            Grid.SetColumn(chevron, 3);
            grid.Children.Add(icon);
            grid.Children.Add(label);
            grid.Children.Add(number);
            grid.Children.Add(chevron);

            var button = new Button
            {
                Content = grid,
                Style = (Style)ChatListPanel.FindResource("FolderHeader")
            };
            System.Windows.Automation.AutomationProperties.SetName(button, $"{name} ({count})");
            System.Windows.Automation.AutomationProperties.SetHelpText(
                button,
                Loc.Get(open ? "S.ChatList.Collapse" : "S.ChatList.Expand"));
            return button;
        }

        // ───────────────────────── Фильтр по тегу ─────────────────────────

        /// <summary>
        /// Пилюля включённого фильтра под поиском и точка на кнопке ⇅. Сам фильтр ставится в меню
        /// кнопки; здесь только видно, что он есть, и снимается он одним щелчком. Во время поиска
        /// пилюли нет: выдача поиска фильтр не учитывает, и пилюля врала бы.
        /// </summary>
        private void RefreshTagFilterPill(ChatOrganizer.State organize, string query)
        {
            var tag = _tagFilter is null ? null : organize.Tags.FirstOrDefault(item => item.Id == _tagFilter);
            ListFilterDot.Visibility = tag is null ? Visibility.Collapsed : Visibility.Visible;
            if (tag is null || _sidebarCollapsed || query.Trim().Length > 0)
            {
                TagFilterPill.Visibility = Visibility.Collapsed;
                return;
            }

            TagFilterDot.SetResourceReference(Shape.FillProperty, tag.Color);
            TagFilterName.Text = tag.Name;
            TagFilterPill.Visibility = Visibility.Visible;
        }

        private void SetTagFilter(string? tagId)
        {
            _tagFilter = tagId;
            RefreshChatList();
        }

        private void TagFilterClear_Click(object sender, RoutedEventArgs e) => SetTagFilter(null);

        /// <summary>Подпись раздела в меню: не пункт, а заголовок над группой пунктов.</summary>
        private MenuItem MenuSection(string text)
        {
            var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
            return new MenuItem
            {
                Header = label,
                Height = 24,
                Style = (Style)FindResource("AppMenuItem"),
                IsHitTestVisible = false,
                Focusable = false
            };
        }

        // ───────────────────────── Выбор ─────────────────────────

        private void ToggleChatSelection(string id)
        {
            if (!_selectedChats.Add(id))
            {
                _selectedChats.Remove(id);
            }

            _selectionAnchor = id;
            RefreshChatRowStates();
            UpdateBatchBar();
        }

        /// <summary>Shift+щелчок: всё между якорем и этой строкой в том порядке, что на экране.</summary>
        private void SelectChatRange(string id)
        {
            var visible = ChatListPanel.Children.OfType<Button>()
                .Select(button => button.Tag as string)
                .OfType<string>()
                .ToList();
            var anchor = _selectionAnchor ?? _session.Id;
            var from = visible.IndexOf(anchor);
            var to = visible.IndexOf(id);
            if (from < 0 || to < 0)
            {
                ToggleChatSelection(id);
                return;
            }

            _selectedChats.Clear();
            foreach (var chat in visible.Skip(Math.Min(from, to)).Take(Math.Abs(to - from) + 1))
            {
                _selectedChats.Add(chat);
            }

            RefreshChatRowStates();
            UpdateBatchBar();
        }

        private void ClearChatSelection()
        {
            if (_selectedChats.Count == 0)
            {
                return;
            }

            _selectedChats.Clear();
            _selectionAnchor = null;
            RefreshChatRowStates();
            UpdateBatchBar();
        }

        private void UpdateBatchBar()
        {
            // Выбранный чат мог исчезнуть (удалён из другого места, сменился профиль).
            if (_services is not null && _selectedChats.Count > 0)
            {
                var alive = _services.ChatStore.List().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
                _selectedChats.RemoveWhere(id => !alive.Contains(id));
            }

            if (_selectedChats.Count == 0 || _sidebarCollapsed)
            {
                BatchBar.Visibility = Visibility.Collapsed;
                return;
            }

            BatchCount.Text = Loc.Format("S.ChatList.Selected", _selectedChats.Count);
            var allArchived = _services is not null &&
                              _selectedChats.All(id => _services.Organizer.PlacementOf(id).Archived);
            var archiveKey = allArchived ? "S.ChatList.Unarchive" : "S.ChatList.Archive";
            BatchArchiveButton.SetResourceReference(ToolTipProperty, archiveKey);
            System.Windows.Automation.AutomationProperties.SetName(BatchArchiveButton, Loc.Get(archiveKey));
            BatchBar.Visibility = Visibility.Visible;
        }

        private IReadOnlyList<string> SelectedChats() => [.. _selectedChats];

        private void BatchFolderButton_Click(object sender, RoutedEventArgs e) => OpenFolderPicker(BatchFolderButton, SelectedChats());

        private void BatchTagButton_Click(object sender, RoutedEventArgs e) => OpenTagPicker(BatchTagButton, SelectedChats());

        private void BatchArchiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || _selectedChats.Count == 0)
            {
                return;
            }

            var allArchived = _selectedChats.All(id => _services.Organizer.PlacementOf(id).Archived);
            ArchiveChats(SelectedChats(), !allArchived);
        }

        private void BatchDeleteButton_Click(object sender, RoutedEventArgs e) =>
            Detached.Run(DeleteChatsAsync(SelectedChats()), "delete_chats");

        private void BatchClearButton_Click(object sender, RoutedEventArgs e) => ClearChatSelection();

        // ───────────────────────── Действия ─────────────────────────

        private void ArchiveChats(IReadOnlyList<string> ids, bool archived)
        {
            if (_services is null)
            {
                return;
            }

            _services.Organizer.SetArchived(ids, archived);

            // Убранное в архив уходит из вида — снимать с него выбор честнее, чем держать
            // выбранными строки, которых не видно.
            if (archived && !_archiveExpanded)
            {
                _selectedChats.ExceptWith(ids);
            }

            RefreshChatList();
        }

        /// <summary>Меню «в папку»: папки с отметкой, «без папки» и новая папка.</summary>
        private void OpenFolderPicker(FrameworkElement anchor, IReadOnlyList<string> ids)
        {
            if (_services is null || ids.Count == 0)
            {
                return;
            }

            var organize = _services.Organizer.Snapshot();
            string? FolderOf(string id) => organize.Chats.TryGetValue(id, out var placement) ? placement.FolderId : null;
            var common = ids.Select(FolderOf).Distinct().ToList();
            var current = common.Count == 1 ? common[0] : "\0mixed";

            var menu = NewMenu(anchor);
            foreach (var folder in organize.Folders)
            {
                var id = folder.Id;
                menu.Items.Add(CheckItem(folder.Name, current == id, null, () => MoveChats(ids, id)));
            }

            menu.Items.Add(CheckItem(Loc.Get("S.ChatList.NoFolder"), current is null, null, () => MoveChats(ids, null)));
            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.NewFolder") + "…", () => CreateFolder(ids)));
            menu.IsOpen = true;
        }

        /// <summary>Меню тегов: отметка — тег есть у всех выбранных; щелчок ставит или снимает.</summary>
        private void OpenTagPicker(FrameworkElement anchor, IReadOnlyList<string> ids)
        {
            if (_services is null || ids.Count == 0)
            {
                return;
            }

            var organize = _services.Organizer.Snapshot();
            var menu = NewMenu(anchor);
            foreach (var tag in organize.Tags)
            {
                var tagId = tag.Id;
                var all = ids.All(id => organize.Chats.TryGetValue(id, out var placement) && placement.Tags.Contains(tagId));
                menu.Items.Add(CheckItem(tag.Name, all, tag.Color, () =>
                {
                    _services?.Organizer.ToggleTag(ids, tagId);
                    RefreshChatList();
                }, tag));
            }

            if (organize.Tags.Count > 0)
            {
                menu.Items.Add(Divider());
            }

            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.NewTag") + "…", () => CreateTag(ids)));
            menu.IsOpen = true;
        }

        private ContextMenu NewMenu(FrameworkElement anchor) => new()
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            Style = (Style)FindResource("AppContextMenu")
        };

        /// <summary>Пункт с местом под галочку слева и, для тегов, точкой цвета.</summary>
        /// <param name="tag">Тег пункта: справа у него кнопка «⋯» — изменить или удалить.</param>
        private MenuItem CheckItem(string text, bool check, string? color, Action invoke, ChatTag? tag = null)
        {
            var mark = new TextBlock
            {
                Text = check ? "✓" : "",
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(mark);
            if (color is not null)
            {
                var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Shape.FillProperty, color);
                Grid.SetColumn(dot, 1);
                header.Children.Add(dot);
            }

            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(label, 2);
            header.Children.Add(label);

            var item = MenuItemFor("", invoke);
            item.Header = header;
            if (tag is not null)
            {
                var more = TagMoreButton(tag);
                Grid.SetColumn(more, 3);
                header.Children.Add(more);
            }

            System.Windows.Automation.AutomationProperties.SetName(item, check ? $"{text} ✓" : text);
            return item;
        }

        /// <summary>
        /// «⋯» у тега в меню: изменить или удалить, не уходя из списка. Прежде удаление пряталось
        /// за «Изменить тег…» → тег → «Удалить», хотя новый тег заводился одним пунктом.
        /// </summary>
        /// <remarks>
        /// Кнопка гасит своё нажатие сама, поэтому щелчок по ней не ставит фильтр и не вешает тег
        /// на чат — это делает только щелчок по строке.
        /// </remarks>
        private Button TagMoreButton(ChatTag tag)
        {
            var glyph = new TextBlock
            {
                Text = "⋯",
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 2)
            };
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");

            var button = new Button
            {
                Content = glyph,
                Width = 24,
                Height = 22,
                Margin = new Thickness(12, 0, -4, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                // Стиль живёт в ресурсах боковой панели, не окна, — ищем от списка чатов.
                Style = (Style)ChatListPanel.FindResource("SidebarIconButton")
            };
            button.SetResourceReference(ToolTipProperty, "S.ChatList.TagActions");
            System.Windows.Automation.AutomationProperties.SetName(button, Loc.Format("S.ChatList.TagActionsFor", tag.Name));
            button.Click += (_, e) =>
            {
                e.Handled = true;
                var owner = FindParentMenu(button);
                var anchor = owner?.PlacementTarget as FrameworkElement ?? ListOptionsButton;
                if (owner is not null)
                {
                    owner.IsOpen = false;
                }

                OpenTagActions(tag, anchor);
            };
            return button;
        }

        private static ContextMenu? FindParentMenu(DependencyObject start)
        {
            for (var node = start; node is not null; node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
            {
                if (node is ContextMenu menu)
                {
                    return menu;
                }
            }

            return null;
        }

        /// <summary>Действия с тегом: изменить (имя и цвет) или удалить.</summary>
        private void OpenTagActions(ChatTag tag, FrameworkElement anchor)
        {
            var menu = NewMenu(anchor);
            menu.Placement = PlacementMode.MousePoint;
            var id = tag.Id;
            var name = tag.Name;
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.EditTag") + "…", () => OpenTagDialog(tag, null)));
            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.DeleteTag"), () => Detached.Run(DeleteTagAsync(id, name), "delete_tag"), danger: true));
            menu.IsOpen = true;
        }

        private void MoveChats(IReadOnlyList<string> ids, string? folderId)
        {
            if (_services is null)
            {
                return;
            }

            _services.Organizer.MoveToFolder(ids, folderId);
            if (folderId is not null)
            {
                // Чат положили в свёрнутую папку — раскрыть её, иначе он просто пропал бы из вида.
                _services.Organizer.SetCollapsed(folderId, false);
            }

            RefreshChatList();
        }

        private void CreateFolder(IReadOnlyList<string>? moveHere)
        {
            OpenNameDialog(
                Loc.Get("S.ChatList.NewFolder"),
                "",
                name =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    var folder = _services.Organizer.CreateFolder(name);
                    if (moveHere is { Count: > 0 })
                    {
                        _services.Organizer.MoveToFolder(moveHere, folder.Id);
                    }

                    RefreshChatList();
                },
                Loc.Get("S.Common.Create"),
                Loc.Get("S.Common.NamePlaceholder"));
        }

        private void RenameFolder(ChatFolder folder)
        {
            OpenNameDialog(
                Loc.Get("S.ChatList.RenameFolder"),
                folder.Name,
                name =>
                {
                    _services?.Organizer.RenameFolder(folder.Id, name);
                    RefreshChatList();
                });
        }

        private async Task DeleteFolderAsync(ChatFolder folder)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Format("S.ChatList.DeleteFolderTitle", folder.Name),
                Loc.Get("S.ChatList.DeleteFolderText"),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (!confirmed || _services is null)
            {
                return;
            }

            _services.Organizer.DeleteFolder(folder.Id);
            RefreshChatList();
        }

        private void CreateTag(IReadOnlyList<string>? applyTo) => OpenTagDialog(null, applyTo);

        private string _tagDialogColor = ChatOrganizer.TagColors[0];

        /// <summary>
        /// Тег: название и цвет; у существующего ещё и «Удалить». Прежде цвет назначался сам по
        /// кругу, а поменять ни его, ни имя было нельзя — только удалить тег и завести заново.
        /// </summary>
        private void OpenTagDialog(ChatTag? existing, IReadOnlyList<string>? applyTo)
        {
            if (_services is null)
            {
                return;
            }

            // Новому тегу — следующий цвет по кругу: соседние теги различаются и без выбора.
            var used = _services.Organizer.Snapshot().Tags.Count;
            var color = existing?.Color ?? ChatOrganizer.TagColors[used % ChatOrganizer.TagColors.Length];
            OpenNameDialog(
                Loc.Get(existing is null ? "S.ChatList.NewTag" : "S.ChatList.EditTag"),
                existing?.Name ?? "",
                name =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    if (existing is not null)
                    {
                        _services.Organizer.UpdateTag(existing.Id, name, _tagDialogColor);
                    }
                    else
                    {
                        var tag = _services.Organizer.CreateTag(name, _tagDialogColor);
                        if (applyTo is { Count: > 0 })
                        {
                            _services.Organizer.ToggleTag(applyTo, tag.Id);
                        }
                    }

                    RefreshChatList();
                },
                Loc.Get(existing is null ? "S.Common.Create" : "S.Common.Save"),
                Loc.Get("S.Common.NamePlaceholder"));

            ShowTagColors(color);
            NameColorRow.Visibility = Visibility.Visible;
            if (existing is not null)
            {
                var id = existing.Id;
                var name = existing.Name;
                NameDeleteButton.Visibility = Visibility.Visible;
                _nameDialogDelete = () => Detached.Run(DeleteTagAsync(id, name), "delete_tag");
            }
        }

        private void ShowTagColors(string selected)
        {
            _tagDialogColor = selected;
            NameColors.Children.Clear();
            for (var i = 0; i < ChatOrganizer.TagColors.Length; i++)
            {
                var color = ChatOrganizer.TagColors[i];
                var dot = new Ellipse { Width = 18, Height = 18 };
                dot.SetResourceReference(Shape.FillProperty, color);
                var swatch = new RadioButton
                {
                    Style = (Style)FindResource("TagSwatch"),
                    GroupName = "TagDialogColor",
                    Content = dot,
                    IsChecked = color == selected
                };
                System.Windows.Automation.AutomationProperties.SetName(swatch, Loc.Format("S.ChatList.TagColorItem", i + 1));
                swatch.Checked += (_, _) => _tagDialogColor = color;
                NameColors.Children.Add(swatch);
            }
        }

        private async Task DeleteTagAsync(string tagId, string name)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Format("S.ChatList.DeleteTagTitle", name),
                Loc.Get("S.ChatList.DeleteTagText"),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (!confirmed || _services is null)
            {
                return;
            }

            _services.Organizer.DeleteTag(tagId);
            RefreshChatList();
        }

        // ───────────────────────── Меню списка ─────────────────────────

        /// <summary>
        /// Меню ⇅: сортировка, фильтр по тегу и заведение папок и тегов. Фильтр — здесь, а не
        /// рядом пилюль под поиском: теги нужны не каждый раз, а место в колонке — всегда.
        /// </summary>
        private void ListOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var menu = NewMenu(ListOptionsButton);
            var sort = _services.Settings.ChatSort;
            menu.Items.Add(MenuSection(Loc.Get("S.ChatList.SortSection")));
            foreach (var (value, key) in new[]
                     {
                         (ChatSort.Updated, "S.ChatList.SortUpdated"),
                         (ChatSort.Created, "S.ChatList.SortCreated"),
                         (ChatSort.Title, "S.ChatList.SortTitle"),
                         (ChatSort.Cost, "S.ChatList.SortCost")
                     })
            {
                menu.Items.Add(CheckItem(Loc.Get(key), sort == value, null, () => SetChatSort(value)));
            }

            var tags = _services.Organizer.Snapshot().Tags;
            if (tags.Count > 0)
            {
                menu.Items.Add(Divider());
                menu.Items.Add(MenuSection(Loc.Get("S.ChatList.FilterSection")));
                menu.Items.Add(CheckItem(Loc.Get("S.ChatList.AllTags"), _tagFilter is null, null, () => SetTagFilter(null)));
                foreach (var tag in tags)
                {
                    var id = tag.Id;
                    menu.Items.Add(CheckItem(tag.Name, _tagFilter == id, tag.Color, () => SetTagFilter(id), tag));
                }
            }

            menu.Items.Add(Divider());
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.NewFolder") + "…", () => CreateFolder(null)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.NewTag") + "…", () => CreateTag(null)));

            menu.IsOpen = true;
        }

        private void SetChatSort(ChatSort sort)
        {
            if (_services is null || _services.Settings.ChatSort == sort)
            {
                return;
            }

            _services.Settings.ChatSort = sort;
            _services.SettingsStore.Save(_services.Settings);
            RefreshChatList();
        }

        // ───────────────────────── Ширина панели ─────────────────────────

        private void SidebarGrip_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_sidebarCollapsed)
            {
                return;
            }

            var width = Math.Clamp(SidebarColumn.ActualWidth + e.HorizontalChange, SidebarWidths.Min, SidebarWidths.Max);
            SidebarColumn.Width = new GridLength(width);
        }

        /// <summary>Сохраняется по отпусканию, а не на каждый сдвиг: иначе запись файла на каждый пиксель.</summary>
        private void SidebarGrip_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_services is null || _sidebarCollapsed)
            {
                return;
            }

            _services.Settings.SidebarWidth = Math.Round(SidebarColumn.ActualWidth);
            _services.SettingsStore.Save(_services.Settings);
        }
    }
}
