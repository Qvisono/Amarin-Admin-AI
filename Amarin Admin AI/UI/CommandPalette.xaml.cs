using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;
using Path = System.Windows.Shapes.Path;

namespace Amarin.UI;

/// <summary>Пункт палитры для окна: что искать, каким значком рисовать и что сделать.</summary>
/// <param name="Keys">Сочетание клавиш действия — справа плашками, а не текстом пути.</param>
internal sealed record PaletteEntry(PaletteItem Item, Geometry Icon, Action Run, string? Keys = null);

/// <summary>
/// Палитра команд (1.37.0): поле поиска и выдача группами. Что искать и что делать, даёт окно
/// (<c>MainWindow.Palette.cs</c>); здесь только показ, выбор и клавиши.
/// </summary>
/// <remarks>
/// Строки собираются кодом, а не шаблоном: их до трёх десятков, и каждая со своим выделением
/// совпадения — виртуализация списка здесь стоила бы дороже, чем экономит.
/// </remarks>
public partial class CommandPalette : UserControl
{
    private readonly List<(Border Row, PaletteEntry Entry)> _rows = [];
    private PaletteItem[] _items = [];

    // По ссылке, а не по значению записи: две одноимённые строки одной страницы равны как записи.
    private Dictionary<PaletteItem, PaletteEntry> _byItem = new(ReferenceEqualityComparer.Instance);
    private int _selected = -1;

    public CommandPalette() => InitializeComponent();

    /// <summary>Палитру закрыли — Esc, щелчок мимо или выбранный пункт.</summary>
    internal event Action? Closed;

    /// <summary>Сколько строк выдачи сейчас на экране — для тестов.</summary>
    internal int ShownCount => _rows.Count;

    /// <summary>Выбранная строка — для тестов.</summary>
    internal PaletteEntry? SelectedEntry => _selected >= 0 && _selected < _rows.Count ? _rows[_selected].Entry : null;

    internal void Open(IReadOnlyList<PaletteEntry> entries)
    {
        Use(entries);
        Search.Text = "";
        Render();
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => Search.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Пункты пополнились (достроились страницы настроек) — выдача обновляется, запрос остаётся.</summary>
    internal void Refresh(IReadOnlyList<PaletteEntry> entries)
    {
        Use(entries);
        Render();
    }

    /// <summary>Пункты и их поиск по найденному — раз на набор, а не на каждое нажатие клавиши.</summary>
    private void Use(IReadOnlyList<PaletteEntry> entries)
    {
        _items = [.. entries.Select(entry => entry.Item)];
        _byItem = new Dictionary<PaletteItem, PaletteEntry>(entries.Count, ReferenceEqualityComparer.Instance);
        foreach (var entry in entries)
        {
            _byItem[entry.Item] = entry;
        }
    }

    internal void Close()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    /// <summary>Выполняет выбранный пункт: палитра закрывается раньше — действие может открыть свой слой.</summary>
    internal void RunSelected()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        Close();
        Dispatcher.BeginInvoke(entry.Run, DispatcherPriority.Input);
    }

    private void Render()
    {
        var query = Search.Text.Trim();
        var matches = PaletteSearch.Find(_items, query);

        Rows.Children.Clear();
        _rows.Clear();
        PaletteKind? group = null;
        foreach (var match in matches)
        {
            if (match.Item.Kind != group)
            {
                group = match.Item.Kind;
                Rows.Children.Add(GroupHeader(group.Value, query.Length == 0, Rows.Children.Count == 0));
            }

            var entry = _byItem[match.Item];
            var row = BuildRow(entry, match);
            _rows.Add((row, entry));
            Rows.Children.Add(row);
        }

        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ListScroll.ScrollToTop();
        Select(_rows.Count > 0 ? 0 : -1, scroll: false);
    }

    private static TextBlock GroupHeader(PaletteKind kind, bool emptyQuery, bool first)
    {
        var key = kind == PaletteKind.Chat && emptyQuery ? "S.Palette.Group.Recent" : "S.Palette.Group." + kind;
        var header = new TextBlock
        {
            Text = Loc.Get(key),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, first ? 6 : 8, 8, 3)
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        return header;
    }

    private Border BuildRow(PaletteEntry entry, PaletteMatch match)
    {
        var icon = new Path
        {
            Data = entry.Icon,
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.SetResourceReference(Path.StrokeProperty, "Text.Muted");

        var title = new TextBlock
        {
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");
        FillTitle(title, entry.Item.Title, match);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        grid.Children.Add(icon);
        grid.Children.Add(title);
        if (Detail(entry) is { } detail)
        {
            Grid.SetColumn(detail, 2);
            grid.Children.Add(detail);
        }

        var row = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 0, 8, 0),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = grid
        };
        System.Windows.Automation.AutomationProperties.SetName(row, entry.Item.Title);
        row.MouseEnter += (_, _) => Select(_rows.FindIndex(pair => ReferenceEquals(pair.Row, row)), scroll: false);

        // Нажатие забирается здесь же: невзятое всплыло бы к окну, и заголовок начал бы им
        // перетаскивание окна.
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Select(_rows.FindIndex(pair => ReferenceEquals(pair.Row, row)), scroll: false);
            RunSelected();
        };
        return row;
    }

    /// <summary>Название с совпадением полужирным и ярче — видно, за что пункт нашёлся.</summary>
    private static void FillTitle(TextBlock title, string text, PaletteMatch match)
    {
        if (match.Length <= 0 || match.Start + match.Length > text.Length)
        {
            title.Text = text;
            return;
        }

        title.Inlines.Add(new Run(text[..match.Start]));
        var hit = new Run(text.Substring(match.Start, match.Length)) { FontWeight = FontWeights.SemiBold };
        hit.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");
        title.Inlines.Add(hit);
        title.Inlines.Add(new Run(text[(match.Start + match.Length)..]));
    }

    /// <summary>Справа: сочетание действия плашкой или путь, дата, подсказка аргумента — тускло.</summary>
    private static FrameworkElement? Detail(PaletteEntry entry)
    {
        if (entry.Keys is { Length: > 0 } keys)
        {
            var text = new TextBlock { Text = keys, FontSize = 11 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
            var cap = new Border
            {
                Child = text,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 0, 5, 0),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            cap.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            return cap;
        }

        // У действия справа только сочетание: его описание ищется, но строку не удлиняет.
        if (entry.Item.Detail.Length == 0 || entry.Item.Kind == PaletteKind.Action)
        {
            return null;
        }

        var detail = new TextBlock
        {
            Text = entry.Item.Detail,
            FontSize = 11.5,
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
        return detail;
    }

    private void Select(int index, bool scroll)
    {
        if (_selected >= 0 && _selected < _rows.Count)
        {
            _rows[_selected].Row.Background = Brushes.Transparent;
        }

        _selected = index;
        if (index < 0 || index >= _rows.Count)
        {
            return;
        }

        var row = _rows[index].Row;
        row.SetResourceReference(Border.BackgroundProperty, "Bg.Selected");
        if (scroll)
        {
            row.BringIntoView();
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Render();

    private void Palette_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Down:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                Move(+1);
                break;
            case Key.Up:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.Shift:
                Move(-1);
                break;
            case Key.Enter:
                RunSelected();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Move(int step)
    {
        if (_rows.Count == 0)
        {
            return;
        }

        Select((_selected + step + _rows.Count) % _rows.Count, scroll: true);
    }

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
}
