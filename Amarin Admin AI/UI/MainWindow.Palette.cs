using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Палитра команд (1.37.0, Ctrl+Shift+P): что в ней искать и что делать с найденным.
    /// </summary>
    /// <remarks>
    /// Настройки берутся из самих построенных страниц — подписи строк стилем <c>SettingTitle</c> на
    /// текущем языке, — а не из своего списка: второй список разошёлся бы с разметкой при первом
    /// же переносе настройки, а перевод интерфейса пришлось бы вести дважды.
    /// </remarks>
    public partial class MainWindow
    {
        // Значки пунктов, сетка 16, одно перо. У настроек — значок их страницы из навигации.
        private const string PaletteActionGlyph = "M9,1.5 L3.5,9 L8,9 L7,14.5 L12.5,7 L8,7 Z";
        private const string PaletteCommandGlyph = "M3,2.5 L13,2.5 L13,13.5 L3,13.5 Z M10,4.5 L6,11.5";
        private const string PaletteRecipeGlyph = "M2.5,4 L8.5,4 M2.5,8 L8.5,8 M2.5,12 L6.5,12 M10.5,8.5 L14,10.75 L10.5,13 Z";
        private const string PaletteChatGlyph = "M2.5,3 L13.5,3 L13.5,11 L7,11 L4,13.5 L4,11 L2.5,11 Z";

        private CommandPalette? _palette;

        /// <summary>Открывает палитру. Сама палитра создаётся при первом открытии.</summary>
        internal bool OpenCommandPalette()
        {
            if (_services is null)
            {
                return false;
            }

            if (_palette is null)
            {
                _palette = new CommandPalette();
                _palette.Closed += () =>
                {
                    PaletteHost.Visibility = Visibility.Collapsed;
                    FocusMessageInput();
                };
                PaletteHost.Children.Add(_palette);
            }

            PaletteHost.Visibility = Visibility.Visible;
            _palette.Open(BuildPaletteEntries());

            // Страницы настроек, которых прогрев ещё не построил, достраиваются за палитрой: она
            // показывается сразу, а их подписи доезжают в выдачу следом.
            if (SettingsUi is { AllBuilt: false } view)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    while (view.BuildNext())
                    {
                    }

                    if (PaletteHost.Visibility == Visibility.Visible)
                    {
                        _palette.Refresh(BuildPaletteEntries());
                    }
                }, DispatcherPriority.Background);
            }

            return true;
        }

        internal IReadOnlyList<PaletteEntry> BuildPaletteEntries()
        {
            var entries = new List<PaletteEntry>();
            AddPaletteActions(entries);
            AddPaletteSettings(entries);
            AddPaletteCommands(entries);
            AddPaletteRecipes(entries);
            AddPaletteChats(entries);
            return entries;
        }

        private void AddPaletteActions(List<PaletteEntry> entries)
        {
            var icon = Glyphs.Get(PaletteActionGlyph);
            var assignments = _services?.Settings.Hotkeys;
            foreach (var action in HotkeyMap.All.Where(action => action.Id != HotkeyMap.CommandPalette))
            {
                var keys = HotkeyMap.Effective(assignments, action.Id) is { } gesture ? HotkeyMap.Display(gesture) : null;
                var id = action.Id;
                entries.Add(new PaletteEntry(
                    new PaletteItem(PaletteKind.Action, id, Loc.Get(action.TitleKey), Loc.Get(action.DescriptionKey)),
                    icon,
                    () => Run(id),
                    keys));
            }

            entries.Add(new PaletteEntry(
                new PaletteItem(PaletteKind.Action, "journal", Loc.Get("S.Journal.Title")),
                icon,
                OpenJournal));
            entries.Add(new PaletteEntry(
                new PaletteItem(PaletteKind.Action, "temporary", Loc.Get("S.Palette.TemporaryChat")),
                icon,
                StartTemporaryChat));
        }

        /// <summary>Новый чат и сразу временный — как плашка посреди пустого чата.</summary>
        private void StartTemporaryChat()
        {
            StartNewChatFromUi();
            if (!_session.IsTemporary)
            {
                TemporaryChatPlaque_Click(TemporaryChatPlaque, new RoutedEventArgs());
            }
        }

        private void AddPaletteSettings(List<PaletteEntry> entries)
        {
            var view = SettingsUi;
            foreach (var nav in view.Navs)
            {
                var pageTitle = nav.Content as string ?? "";
                entries.Add(new PaletteEntry(
                    new PaletteItem(PaletteKind.Setting, nav.Name, pageTitle, Loc.Get("S.Palette.Settings")),
                    nav.Tag as Geometry ?? Glyphs.Get(PaletteActionGlyph),
                    () => OpenSettings(nav)));
            }

            foreach (var page in view.BuiltPages)
            {
                if (page.TryFindResource("SettingTitle") is not Style titleStyle)
                {
                    continue;
                }

                var nav = view.NavOf(page);
                var pageTitle = nav.Content as string ?? "";
                var icon = nav.Tag as Geometry ?? Glyphs.Get(PaletteActionGlyph);
                foreach (var title in SettingTitles(page, titleStyle))
                {
                    var (sub, link) = DrillOf(title, page);
                    var path = link is not null && FirstTitle(link, titleStyle) is { } subTitle && !ReferenceEquals(subTitle, title)
                        ? pageTitle + " › " + subTitle.Text
                        : pageTitle;

                    // Подпись самой строки-ссылки ведёт в её подстраницу, а не к строке на корне.
                    var opener = Ancestors(title, page).OfType<ButtonBase>().FirstOrDefault(button => SettingsDrill.GetOpens(button) is not null);
                    var target = title;
                    entries.Add(new PaletteEntry(
                        new PaletteItem(PaletteKind.Setting, nav.Name + "/" + title.Text, title.Text, path),
                        icon,
                        () => OpenSettingAt(nav, sub, link, opener, target)));
                }
            }
        }

        /// <summary>Подписи строк страницы, которые сейчас можно увидеть.</summary>
        private static IEnumerable<TextBlock> SettingTitles(FrameworkElement page, Style titleStyle)
        {
            foreach (var node in LogicalDescendants(page))
            {
                if (node is TextBlock { Text.Length: > 0 } title && ReferenceEquals(title.Style, titleStyle) && !HiddenOnPage(title, page))
                {
                    yield return title;
                }
            }
        }

        /// <summary>
        /// Строка спрятана сама или её раздел (настройка, которой сейчас нет, вложенная под
        /// выключенным переключателем). Свёрнутые подстраницы и корень под открытой подстраницей
        /// спрятанными не считаются: их открывает сама палитра.
        /// </summary>
        private static bool HiddenOnPage(FrameworkElement title, FrameworkElement page)
        {
            foreach (var node in Ancestors(title, page).Prepend(title))
            {
                if (ReferenceEquals(node, page))
                {
                    return false;
                }

                var drillChild = LogicalTreeHelper.GetParent(node) is Panel host && SettingsDrill.GetIsHost(host);
                if (!drillChild && node is UIElement { Visibility: Visibility.Collapsed })
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Подстраница, в которой лежит подпись, и строка-ссылка, которая её открывает.</summary>
        private static (FrameworkElement? Sub, ButtonBase? Link) DrillOf(FrameworkElement title, FrameworkElement page)
        {
            DependencyObject child = title;
            foreach (var node in Ancestors(title, page))
            {
                if (node is Panel host && SettingsDrill.GetIsHost(host) && child is FrameworkElement sub &&
                    host.Children.IndexOf(sub) > 0)
                {
                    var link = LogicalDescendants(page).OfType<ButtonBase>()
                        .FirstOrDefault(button => ReferenceEquals(SettingsDrill.GetOpens(button), sub));
                    return (sub, link);
                }

                child = node;
            }

            return (null, null);
        }

        private static TextBlock? FirstTitle(DependencyObject root, Style titleStyle) =>
            LogicalDescendants(root).OfType<TextBlock>().FirstOrDefault(block => ReferenceEquals(block.Style, titleStyle));

        private static IEnumerable<DependencyObject> Ancestors(DependencyObject node, DependencyObject stop)
        {
            for (var parent = LogicalTreeHelper.GetParent(node); parent is not null; parent = LogicalTreeHelper.GetParent(parent))
            {
                yield return parent;
                if (ReferenceEquals(parent, stop))
                {
                    yield break;
                }
            }
        }

        private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                yield return node;
                foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>().Reverse())
                {
                    pending.Push(child);
                }
            }
        }

        /// <summary>Открывает настройки на странице строки, её подстраницу и прокручивает к строке.</summary>
        private void OpenSettingAt(RadioButton nav, FrameworkElement? sub, ButtonBase? link, ButtonBase? opener, FrameworkElement target)
        {
            OpenSettings(nav);
            if (opener is not null && SettingsDrill.GetOpens(opener) is { } opened)
            {
                SettingsDrill.Open(opened, opener);
                return;
            }

            if (sub is not null)
            {
                SettingsDrill.Open(sub, link);
            }

            // После раскладки: только что показанная страница ещё не знает, где у неё строка.
            Dispatcher.BeginInvoke(target.BringIntoView, DispatcherPriority.Loaded);
        }

        private void AddPaletteCommands(List<PaletteEntry> entries)
        {
            var icon = Glyphs.Get(PaletteCommandGlyph);
            foreach (var command in ChatCommands.All)
            {
                var text = "/" + command.Name;
                entries.Add(new PaletteEntry(
                    new PaletteItem(PaletteKind.Command, command.Name, text, Loc.Get(command.DescriptionKey)),
                    icon,
                    () => PutCommandInComposer(text)));
            }
        }

        /// <summary>Команда встаёт в поле ввода: аргумент дописывает человек, отправляет — Enter.</summary>
        private void PutCommandInComposer(string command)
        {
            MessageTextBox.Text = command + " ";
            FocusMessageInput();
            MessageTextBox.CaretIndex = MessageTextBox.Text.Length;
        }

        private void AddPaletteRecipes(List<PaletteEntry> entries)
        {
            if (_services is null)
            {
                return;
            }

            var icon = Glyphs.Get(PaletteRecipeGlyph);
            foreach (var recipe in _services.Recipes.All())
            {
                var id = recipe.Id;
                var detail = recipe.Description?.Split('\n')[0].Trim() ?? "";
                entries.Add(new PaletteEntry(
                    new PaletteItem(PaletteKind.Recipe, id, recipe.Name, detail),
                    icon,
                    () =>
                    {
                        OpenSettings(SettingsUi.NavAutomation);
                        AutomationPage.Run(id);
                    }));
            }
        }

        private void AddPaletteChats(List<PaletteEntry> entries)
        {
            if (_services is null)
            {
                return;
            }

            var icon = Glyphs.Get(PaletteChatGlyph);
            var today = DateTime.Now.Date;
            var chats = _temporaryChats.Entries()
                .Concat(_services.ChatStore.List())
                .OrderByDescending(entry => entry.UpdatedAt);
            foreach (var chat in chats)
            {
                var id = chat.Id;
                var when = chat.UpdatedAt.Date == today
                    ? ChatFormat.Clock(chat.UpdatedAt)
                    : ChatFormat.DateTimeShort(chat.UpdatedAt, ActiveDateFormat);
                entries.Add(new PaletteEntry(
                    new PaletteItem(PaletteKind.Chat, id, DisplayTitle(chat.Title), when),
                    icon,
                    () => OpenChat(id)));
            }
        }
    }
}
