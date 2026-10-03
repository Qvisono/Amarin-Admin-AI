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
        /// <summary>Выбранные строки и якорь Shift+щелчка (см. <see cref="ChatSelection"/>).</summary>
        private readonly ChatSelection _selection = new();
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
                _selection.DragSet,
                DropChats);
            _chatDrag.Ended += () =>
            {
                if (_chatDrag.RefreshPending)
                {
                    _chatDrag.RefreshPending = false;
                    RefreshChatList();
                }
            };
            _chatDrag.Starting += FinishChatListMotion;
        }

        /// <summary>Чаты брошены в раздел боковой панели — см. <see cref="ChatDrop.Apply"/>.</summary>
        private void DropChats(IReadOnlyList<string> ids, ChatDropTarget target)
        {
            if (_services is null || !ChatDrop.Apply(_services.ChatStore, _services.Organizer, ids, target))
            {
                return;
            }

            _selection.Reset();
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
            _selection.Reset();
            _tagFilter = null;
            _archiveExpanded = false;
            InvalidateChatListSignature();
        }

        // ───────────────────────── Отрисовка ─────────────────────────

        /// <summary>Снимок раскладки, которая сейчас нарисована, и её элементы — по номеру строки.</summary>
        private ChatListShown _shown = ChatListShown.Empty;

        private FrameworkElement[] _shownElements = [];

        /// <summary>Строки, которые сейчас вырастают (раскрытие) или сворачиваются (свёртывание).</summary>
        private readonly List<FrameworkElement> _growingRows = [];

        private readonly List<FrameworkElement> _leavingRows = [];

        /// <summary>Заголовок папки и полоса, которую он получит, когда его строки досвернутся.</summary>
        private readonly List<(Button Header, FolderBand Band)> _foldingHeaders = [];

        /// <param name="droppable">
        /// Раскладка по разделам, а не выдача поиска: каждому элементу проставляется раздел, куда
        /// упадёт брошенный на него чат.
        /// </param>
        /// <remarks>
        /// Прежняя раскладка сверяется с новой (<see cref="ChatListPatch"/>): строка, у которой не
        /// поменялось ничего из того, из чего она строится, остаётся тем же элементом, а строятся
        /// только новые и изменившиеся. До 1.30.0 панель сносилась и собиралась целиком — щелчок по
        /// папке в списке из трёхсот чатов стоил 120 мс работы потока окна.
        /// </remarks>
        private void RenderChatListNodes(IReadOnlyList<ChatListNode> nodes, bool droppable)
        {
            _chatListDroppable = droppable;
            FinishChatListMotion();

            // Видимая панель и включённые анимации: иначе строки встают и пропадают сразу.
            var animate = UiMotion.Enabled && ChatListPanel.IsVisible;
            var shown = ChatListPatch.Snapshot(nodes);
            var slots = ChatListPatch.Plan(_shown, shown);
            var elements = new FrameworkElement[nodes.Count];
            var desired = new List<UIElement>(slots.Count);
            var oldOuter = new Dictionary<FrameworkElement, double>();
            foreach (var slot in slots)
            {
                if (slot.NewIndex < 0)
                {
                    if (animate)
                    {
                        // Строка уходит: в Tag больше нет id, и выбор, Ctrl+Tab и перетаскивание
                        // её не видят, пока она досворачивается.
                        var leaving = _shownElements[slot.OldIndex];
                        leaving.Tag = null;
                        leaving.IsHitTestVisible = false;
                        desired.Add(leaving);
                        _leavingRows.Add(leaving);
                    }

                    continue;
                }

                var element = slot.OldIndex >= 0 ? _shownElements[slot.OldIndex] : BuildNode(nodes[slot.NewIndex]);
                if (slot.OldIndex >= 0 && ChatListPatch.IsOpen(nodes[slot.NewIndex]) is not null)
                {
                    oldOuter[element] = OuterHeight(element);
                }

                elements[slot.NewIndex] = element;
                desired.Add(element);
                if (animate && slot.Motion == ChatListMotion.Enter)
                {
                    _growingRows.Add(element);
                }
            }

            PlaceNodes(nodes, elements, droppable, animate);
            SyncChildren(ChatListPanel, desired);
            _shown = shown;
            _shownElements = elements;

            if (_growingRows.Count > 0 || _leavingRows.Count > 0)
            {
                StartChatListMotion(desired, oldOuter);
            }
        }

        private FrameworkElement BuildNode(ChatListNode node) => node switch
        {
            ChatListGroup => new TextBlock { Style = (Style)ChatListPanel.FindResource("GroupHeader") },
            ChatListFolder folder => BuildFolderHeader(folder),
            ChatListArchive archive => BuildArchiveHeader(archive),
            ChatListChat chat => BuildChatRow(chat),
            _ => new TextBlock()
        };

        /// <summary>
        /// Всё, что зависит от места строки в списке: подпись группы, полоса карточки папки,
        /// раздел для броска, раскрыт ли заголовок и признаки строки. Ставится каждой строке, и
        /// новой, и оставленной, — поэтому этого нет в штампе строки.
        /// </summary>
        private void PlaceNodes(IReadOnlyList<ChatListNode> nodes, FrameworkElement[] elements, bool droppable, bool animate)
        {
            // Раздел текущей группы и раздел раскрытой папки или архива — для вложенных строк.
            ChatDropTarget? section = null;
            ChatDropTarget? nested = null;
            for (var i = 0; i < nodes.Count; i++)
            {
                // Чаты раскрытой папки идут сразу за её заголовком, и карточку папки замыкает
                // последний из них — чей сосед снизу уже не вложенный чат.
                var nextNested = i + 1 < nodes.Count && nodes[i + 1] is ChatListChat { Nested: true };
                var element = elements[i];
                switch (nodes[i])
                {
                    case ChatListGroup group when element is TextBlock header:
                        header.Text = Loc.Get(group.TitleKey);

                        // Верхний заголовок стоит прямо под поиском — ему нужно меньше воздуха.
                        if (i == 0)
                        {
                            header.Margin = new Thickness(14, 6, 6, 4);
                        }
                        else
                        {
                            header.ClearValue(MarginProperty);
                        }

                        section = droppable ? SectionOf(group.TitleKey) : null;
                        ChatRowState.SetDropTarget(header, section);
                        break;
                    case ChatListFolder folder when element is Button header:
                        nested = droppable ? ChatDropTarget.Folder(folder.Folder.Id) : null;
                        header.Tag = folder.Folder;
                        ShowGroupOpen(header, !folder.Folder.Collapsed, FolderOpenGlyph, FolderGlyph, animate);
                        Banded(header, folder.Folder.Collapsed, nextNested);
                        ChatRowState.SetDropTarget(header, nested);
                        break;
                    case ChatListArchive archive when element is Button header:
                        nested = droppable ? ChatDropTarget.Archive : null;
                        header.Tag = archive;
                        ShowGroupOpen(header, archive.Expanded, ArchiveGlyph, ArchiveGlyph, animate);
                        Banded(header, !archive.Expanded, nextNested);
                        ChatRowState.SetDropTarget(header, nested);
                        break;
                    case ChatListChat chat when element is Button row:
                        ChatRowState.SetBand(row, chat.Nested ? (nextNested ? FolderBand.Middle : FolderBand.Bottom) : FolderBand.None);
                        ChatRowState.SetDropTarget(row, chat.Nested ? nested : section);
                        ApplyChatRowState(row, chat.Entry.Id);
                        break;
                }
            }
        }

        /// <summary>
        /// Приводит детей панели к нужному порядку, не трогая тех, кто уже стоит на месте:
        /// оставленная строка не отрывается от дерева и не теряет шаблона.
        /// </summary>
        private static void SyncChildren(Panel panel, IReadOnlyList<UIElement> desired)
        {
            var children = panel.Children;
            for (var i = 0; i < desired.Count; i++)
            {
                var element = desired[i];
                if (i < children.Count && ReferenceEquals(children[i], element))
                {
                    continue;
                }

                var at = children.IndexOf(element);
                if (at >= 0)
                {
                    children.RemoveAt(at);
                }

                children.Insert(i, element);
            }

            if (children.Count > desired.Count)
            {
                children.RemoveRange(desired.Count, children.Count - desired.Count);
            }
        }

        /// <summary>Высота строки вместе с полями — столько она занимает в колонке.</summary>
        private static double OuterHeight(FrameworkElement element) =>
            element.ActualHeight + element.Margin.Top + element.Margin.Bottom;

        /// <summary>
        /// Раскрытие и свёртывание папки: её строки вырастают из-под заголовка или уходят под него.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Колонка не прыгает ни в начале, ни в конце: раскрытая папка — заголовок «верх карточки»
        /// (35 + поле 3) и строки, свёрнутая — карточка в одну строку (38 + поля 3 и 3). Разницу
        /// в начале раскрытия держит последняя строка, начиная не с нуля, а с неё; в конце
        /// свёртывания — так же, и только потом заголовок становится карточкой в одну строку.
        /// </para>
        /// <para>
        /// Конечная высота строки известна лишь после раскладки — её задают триггеры шаблона по
        /// полосе карточки, — поэтому панель раскладывается здесь же, до первого кадра: строки
        /// встают в свою высоту, высота читается, и анимация начинается с начальной.
        /// </para>
        /// </remarks>
        private void StartChatListMotion(List<UIElement> desired, Dictionary<FrameworkElement, double> oldOuter)
        {
            // Уходящие строки стоят сразу за заголовком своей папки: на время свёртывания он
            // остаётся верхом карточки, иначе карточка закрылась бы раньше, чем ушли строки.
            for (var i = 0; i < desired.Count; i++)
            {
                if (desired[i] is Button header && header.Tag is ChatFolder or ChatListArchive &&
                    i + 1 < desired.Count && _leavingRows.Contains((FrameworkElement)desired[i + 1]))
                {
                    _foldingHeaders.Add((header, ChatRowState.GetBand(header)));
                    ChatRowState.SetBand(header, FolderBand.Top);
                }
            }

            ChatListPanel.UpdateLayout();

            foreach (var run in Runs(desired, _growingRows))
            {
                // Разница высот заголовка до и после — её и держит последняя строка в начале.
                var header = run.Header;
                var start = header is not null && oldOuter.TryGetValue(header, out var before)
                    ? before - OuterHeight(header) - run.Rows.Sum(row => row.Margin.Top + row.Margin.Bottom)
                    : 0;
                for (var i = 0; i < run.Rows.Count; i++)
                {
                    UiMotion.Grow(run.Rows[i], i == run.Rows.Count - 1 ? Math.Max(0, start) : 0);
                }
            }

            foreach (var run in Runs(desired, _leavingRows))
            {
                var header = run.Header;
                var folding = _foldingHeaders.FirstOrDefault(entry => ReferenceEquals(entry.Header, header));
                var end = 0.0;
                if (header is not null && folding.Header is not null)
                {
                    // Сколько займёт свёрнутая карточка против раскрытого заголовка — столько и
                    // останется от последней строки к концу.
                    end = Math.Max(0, CollapsedHeaderOuter() - OuterHeight(header) - run.Rows.Sum(row => row.Margin.Top + row.Margin.Bottom));
                }

                var left = run.Rows.Count;
                for (var i = 0; i < run.Rows.Count; i++)
                {
                    var row = run.Rows[i];
                    UiMotion.Shrink(row, i == run.Rows.Count - 1 ? end : 0, () =>
                    {
                        if (--left == 0)
                        {
                            FinishFolding(header, run.Rows);
                        }
                    });
                }
            }
        }

        /// <summary>
        /// Высота свёрнутой карточки папки вместе с полями — из сеттеров стиля <c>FolderHeader</c>
        /// (38 + 3 + 3), а не числом здесь: поменяют стиль — не разойдётся.
        /// </summary>
        private double CollapsedHeaderOuter()
        {
            var height = 38.0;
            var margin = new Thickness(6, 3, 6, 3);
            if (ChatListPanel.TryFindResource("FolderHeader") is Style style)
            {
                foreach (var setter in style.Setters.OfType<Setter>())
                {
                    if (setter.Property == HeightProperty && setter.Value is double value)
                    {
                        height = value;
                    }
                    else if (setter.Property == MarginProperty && setter.Value is Thickness thickness)
                    {
                        margin = thickness;
                    }
                }
            }

            return height + margin.Top + margin.Bottom;
        }

        /// <summary>Подряд идущие строки из набора и заголовок над ними.</summary>
        private static List<(Button? Header, List<FrameworkElement> Rows)> Runs(List<UIElement> desired, List<FrameworkElement> set)
        {
            var runs = new List<(Button? Header, List<FrameworkElement> Rows)>();
            for (var i = 0; i < desired.Count; i++)
            {
                if (desired[i] is not FrameworkElement element || !set.Contains(element))
                {
                    continue;
                }

                if (runs.Count > 0 && i > 0 && runs[^1].Rows.Count > 0 && ReferenceEquals(runs[^1].Rows[^1], desired[i - 1]))
                {
                    runs[^1].Rows.Add(element);
                    continue;
                }

                runs.Add((i > 0 ? desired[i - 1] as Button : null, [element]));
            }

            return runs;
        }

        /// <summary>Строки свёрнутой папки досвернулись: убрать их, а заголовок сделать карточкой в одну строку.</summary>
        private void FinishFolding(Button? header, List<FrameworkElement> rows)
        {
            foreach (var row in rows)
            {
                _leavingRows.Remove(row);
                ChatListPanel.Children.Remove(row);
            }

            var index = _foldingHeaders.FindIndex(entry => ReferenceEquals(entry.Header, header));
            if (index >= 0)
            {
                ChatRowState.SetBand(_foldingHeaders[index].Header, _foldingHeaders[index].Band);
                _foldingHeaders.RemoveAt(index);
            }
        }

        /// <summary>
        /// Доводит раскрытие и свёртывание до конца сразу: новая раскладка списка или начало
        /// перетаскивания не должны застать строки посреди хода.
        /// </summary>
        private void FinishChatListMotion()
        {
            foreach (var row in _growingRows)
            {
                UiMotion.Settle(row);
            }

            _growingRows.Clear();
            foreach (var row in _leavingRows)
            {
                UiMotion.Settle(row);
                ChatListPanel.Children.Remove(row);
            }

            _leavingRows.Clear();
            foreach (var (header, band) in _foldingHeaders)
            {
                ChatRowState.SetBand(header, band);
            }

            _foldingHeaders.Clear();
        }

        /// <summary>
        /// Нарисованное забыто: следующая раскладка построит каждую строку заново. Для смены языка,
        /// профиля и выдачи поиска по тексту, которая заняла панель своими карточками.
        /// </summary>
        private void ForgetChatListRows()
        {
            FinishChatListMotion();
            _shown = ChatListShown.Empty;
            _shownElements = [];
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
        private static void Banded(Button header, bool collapsed, bool hasRows) =>
            ChatRowState.SetBand(header, !collapsed && hasRows ? FolderBand.Top : FolderBand.Single);

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

        /// <remarks>
        /// Заголовок переживает обновления списка (<see cref="ChatListPatch"/>), а папка в его
        /// <c>Tag</c> каждый раз свежая. Поэтому щелчок и меню берут её оттуда в момент нажатия:
        /// папка, пойманная при постройке, помнила бы, свёрнута ли она была тогда.
        /// </remarks>
        private Button BuildFolderHeader(ChatListFolder node)
        {
            var folder = node.Folder;
            var open = !folder.Collapsed;
            var button = BuildGroupButton(folder.Name, node.Count, open, open ? FolderOpenGlyph : FolderGlyph);
            button.Tag = folder;
            button.Click += (_, e) =>
            {
                e.Handled = true;
                if (button.Tag is ChatFolder current)
                {
                    _services?.Organizer.SetCollapsed(current.Id, !current.Collapsed);
                    RefreshChatList();
                }
            };

            var menu = new ContextMenu { Style = (Style)FindResource("AppContextMenu") };
            menu.Items.Add(MenuItemFor(
                Loc.Get("S.ChatList.RenameFolder"),
                () => RenameFolder(button.Tag as ChatFolder ?? folder),
                icon: "Icon.Menu.Rename"));
            menu.Items.Add(MenuItemFor(
                Loc.Get("S.ChatList.DeleteFolder"),
                () => Detached.Run(DeleteFolderAsync(button.Tag as ChatFolder ?? folder), "delete_folder"),
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

        /// <summary>
        /// Раскрыт ли заголовок: значок, число у свёрнутого, шеврон и подсказка для экранного
        /// диктора. Зовётся на каждом обновлении — заголовок теперь живёт дольше одной раскладки.
        /// </summary>
        private static void ShowGroupOpen(Button header, bool open, string openGlyph, string closedGlyph, bool animate)
        {
            if (header.Content is not Grid grid || grid.Children.Count < 4 ||
                grid.Children[0] is not Path icon || grid.Children[2] is not TextBlock number || grid.Children[3] is not Path chevron)
            {
                return;
            }

            icon.Data = Glyphs.Get(open ? openGlyph : closedGlyph);
            icon.SetResourceReference(Shape.StrokeProperty, open ? "Text.Muted" : "Text.Dim");
            number.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
            if (chevron.RenderTransform is RotateTransform turn)
            {
                UiMotion.Turn(turn, open ? 180 : 0, animate);
            }

            System.Windows.Automation.AutomationProperties.SetHelpText(
                header,
                Loc.Get(open ? "S.ChatList.Collapse" : "S.ChatList.Expand"));
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
            _selection.Toggle(id);
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
            _selection.SelectRange(id, visible, _session.Id);
            RefreshChatRowStates();
            UpdateBatchBar();
        }

        private void ClearChatSelection()
        {
            if (!_selection.Clear())
            {
                return;
            }

            RefreshChatRowStates();
            UpdateBatchBar();
        }

        private void UpdateBatchBar()
        {
            // Выбранный чат мог исчезнуть (удалён из другого места, сменился профиль).
            if (_services is not null && _selection.Count > 0)
            {
                _selection.KeepOnly(_services.ChatStore.List().Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal));
            }

            if (_selection.Count == 0 || _sidebarCollapsed)
            {
                BatchBar.Visibility = Visibility.Collapsed;
                return;
            }

            BatchCount.Text = Loc.Format("S.ChatList.Selected", _selection.Count);
            var allArchived = _services is not null &&
                              _selection.Items.All(id => _services.Organizer.PlacementOf(id).Archived);
            var archiveKey = allArchived ? "S.ChatList.Unarchive" : "S.ChatList.Archive";
            BatchArchiveButton.SetResourceReference(ToolTipProperty, archiveKey);
            System.Windows.Automation.AutomationProperties.SetName(BatchArchiveButton, Loc.Get(archiveKey));
            BatchBar.Visibility = Visibility.Visible;
        }

        private IReadOnlyList<string> SelectedChats() => _selection.Items;

        private void BatchFolderButton_Click(object sender, RoutedEventArgs e) => OpenFolderPicker(BatchFolderButton, SelectedChats());

        private void BatchTagButton_Click(object sender, RoutedEventArgs e) => OpenTagPicker(BatchTagButton, SelectedChats());

        private void BatchArchiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null || _selection.Count == 0)
            {
                return;
            }

            var allArchived = _selection.Items.All(id => _services.Organizer.PlacementOf(id).Archived);
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
                _selection.Remove(ids);
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
