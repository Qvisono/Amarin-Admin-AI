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
    /// Перетаскивания в папку нет намеренно: в колонке чатов зажатая кнопка уже листает список
    /// (<see cref="SmoothScroll.DragScroll"/>), и два жеста на одном нажатии спорили бы.
    /// </remarks>
    public partial class MainWindow
    {
        private readonly HashSet<string> _selectedChats = new(StringComparer.Ordinal);
        private string? _selectionAnchor;
        private bool _archiveExpanded;
        private string? _tagFilter;

        /// <summary>Смена профиля: выбор, фильтр и раскрытый архив относились к прежнему.</summary>
        private void ResetChatListView()
        {
            _selectedChats.Clear();
            _selectionAnchor = null;
            _tagFilter = null;
            _archiveExpanded = false;
            _chatListSignature = "";
        }

        // ───────────────────────── Отрисовка ─────────────────────────

        private void RenderChatListNodes(IReadOnlyList<ChatListNode> nodes)
        {
            var first = true;
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ChatListGroup group:
                        var header = new TextBlock
                        {
                            Style = (Style)ChatListPanel.FindResource("GroupHeader"),
                            Text = Loc.Get(group.TitleKey)
                        };
                        if (first)
                        {
                            // Верхний заголовок стоит прямо под поиском — ему нужно меньше воздуха.
                            header.Margin = new Thickness(14, 6, 6, 4);
                        }

                        ChatListPanel.Children.Add(header);
                        break;
                    case ChatListFolder folder:
                        ChatListPanel.Children.Add(BuildFolderHeader(folder));
                        break;
                    case ChatListArchive archive:
                        ChatListPanel.Children.Add(BuildArchiveHeader(archive));
                        break;
                    case ChatListChat chat:
                        ChatListPanel.Children.Add(BuildChatRow(chat));
                        break;
                }

                first = false;
            }
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
            var button = BuildGroupButton(folder.Name, node.Count, open: !folder.Collapsed, glyph: FolderGlyph);
            button.Tag = folder;
            button.Click += (_, e) =>
            {
                e.Handled = true;
                _services?.Organizer.SetCollapsed(folder.Id, !folder.Collapsed);
                RefreshChatList();
            };

            var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenu") };
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.RenameFolder"), () => RenameFolder(folder)));
            menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.DeleteFolder"), () => Detached.Run(DeleteFolderAsync(folder), "delete_folder"), danger: true));
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

        private const string FolderGlyph = "M1,3 L1,12 L14,12 L14,4.5 L7.5,4.5 L6,3 Z";
        private const string ArchiveGlyph = "M1,1 L13,1 L13,4 L1,4 Z M2,4 L2,12 L12,12 L12,4 M5,7 L9,7";

        /// <summary>Заголовок сворачиваемой группы: шеврон, значок, имя и число чатов справа.</summary>
        private Button BuildGroupButton(string name, int count, bool open, string glyph)
        {
            var chevron = new Path
            {
                Data = Geometry.Parse("M1,1 L5,5 L1,9"),
                Width = 6,
                Height = 9,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Margin = new Thickness(2, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(open ? 90 : 0)
            };
            chevron.SetResourceReference(Shape.StrokeProperty, "Text.Faint");

            var icon = new Path
            {
                Data = Geometry.Parse(glyph),
                Width = 12,
                Height = 11,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1.2,
                StrokeLineJoin = PenLineJoin.Round,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(Shape.StrokeProperty, "Text.Dim");

            var label = new TextBlock
            {
                Text = name,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

            var number = new TextBlock
            {
                Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                FontSize = 10.5,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            number.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(icon, 1);
            Grid.SetColumn(label, 2);
            Grid.SetColumn(number, 3);
            grid.Children.Add(chevron);
            grid.Children.Add(icon);
            grid.Children.Add(label);
            grid.Children.Add(number);

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

        private string _tagFilterSignature = "";

        private void RefreshTagFilterRow(ChatOrganizer.State organize)
        {
            var signature = string.Join("|", organize.Tags.Select(tag => tag.Id + "~" + tag.Name + "~" + tag.Color)) + "#" + _tagFilter;
            if (signature == _tagFilterSignature)
            {
                return;
            }

            _tagFilterSignature = signature;
            TagFilterRow.Children.Clear();
            if (organize.Tags.Count > 0)
            {
                TagFilterRow.Children.Add(BuildTagChip(null, Loc.Get("S.ChatList.AllTags"), null));
                foreach (var tag in organize.Tags)
                {
                    TagFilterRow.Children.Add(BuildTagChip(tag.Id, tag.Name, tag.Color));
                }
            }

            TagFilterRow.Visibility = organize.Tags.Count > 0 && !_sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
        }

        private ToggleButton BuildTagChip(string? tagId, string name, string? color)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (color is not null)
            {
                var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Shape.FillProperty, color);
                content.Children.Add(dot);
            }

            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 110, TextTrimming = TextTrimming.CharacterEllipsis });

            var chip = new ToggleButton
            {
                Content = content,
                IsChecked = _tagFilter == tagId,
                Style = (Style)ChatListPanel.FindResource("TagChip")
            };
            System.Windows.Automation.AutomationProperties.SetName(chip, name);
            chip.Click += (_, _) =>
            {
                // Повторный щелчок по выбранному тегу снимает фильтр — как «Все».
                _tagFilter = _tagFilter == tagId ? null : tagId;
                RefreshChatList();
            };

            if (tagId is not null)
            {
                var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenu") };
                menu.Items.Add(MenuItemFor(Loc.Get("S.ChatList.DeleteTag"), () => Detached.Run(DeleteTagAsync(tagId, name), "delete_tag"), danger: true));
                chip.ContextMenu = menu;
            }

            return chip;
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
                }));
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
        private MenuItem CheckItem(string text, bool check, string? color, Action invoke)
        {
            var mark = new TextBlock
            {
                Text = check ? "✓" : "",
                Width = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, "Accent.Fill");

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(mark);
            if (color is not null)
            {
                var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                dot.SetResourceReference(Shape.FillProperty, color);
                header.Children.Add(dot);
            }

            header.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 240, TextTrimming = TextTrimming.CharacterEllipsis });
            var item = MenuItemFor("", invoke);
            item.Header = header;
            System.Windows.Automation.AutomationProperties.SetName(item, check ? $"{text} ✓" : text);
            return item;
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
                Loc.Get("S.ChatList.NewFolderDesc"),
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
                });
        }

        private void RenameFolder(ChatFolder folder)
        {
            OpenNameDialog(
                Loc.Get("S.ChatList.RenameFolder"),
                Loc.Get("S.ChatList.NewFolderDesc"),
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
                Loc.Get("S.ChatList.DeleteFolder"),
                Loc.Format("S.ChatList.DeleteFolderText", folder.Name),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"));
            if (!confirmed || _services is null)
            {
                return;
            }

            _services.Organizer.DeleteFolder(folder.Id);
            RefreshChatList();
        }

        private void CreateTag(IReadOnlyList<string>? applyTo)
        {
            OpenNameDialog(
                Loc.Get("S.ChatList.NewTag"),
                Loc.Get("S.ChatList.NewTagDesc"),
                "",
                name =>
                {
                    if (_services is null)
                    {
                        return;
                    }

                    // Цвета идут по кругу: у соседних тегов они различаются без выбора вручную.
                    var used = _services.Organizer.Snapshot().Tags.Count;
                    var tag = _services.Organizer.CreateTag(name, ChatOrganizer.TagColors[used % ChatOrganizer.TagColors.Length]);
                    if (applyTo is { Count: > 0 })
                    {
                        _services.Organizer.ToggleTag(applyTo, tag.Id);
                    }

                    RefreshChatList();
                });
        }

        private async Task DeleteTagAsync(string tagId, string name)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Get("S.ChatList.DeleteTag"),
                Loc.Format("S.ChatList.DeleteTagText", name),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"));
            if (!confirmed || _services is null)
            {
                return;
            }

            _services.Organizer.DeleteTag(tagId);
            RefreshChatList();
        }

        // ───────────────────────── Меню списка ─────────────────────────

        private void ListOptionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var menu = NewMenu(ListOptionsButton);
            var sort = _services.Settings.ChatSort;
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
