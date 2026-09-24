using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Amarin.UI;

/// <summary>
/// Блок кода: шапка с языком и кнопкой «копировать», под ней подсвеченный моноширинный текст
/// с горизонтальной прокруткой. Кладётся в <see cref="BlockUIContainer"/> внутри тела сообщения.
/// </summary>
internal static class CodeBlockView
{
    private const double CodeFontSize = 12.5;
    private const double CodeLineHeight = 18;
    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New");

    /// <summary>Начертание для замера строк. Статическое: составное имя семейства разбирается
    /// при создании, а замер идёт на каждую строку каждого блока каждой перерисовки.</summary>
    private static readonly Typeface MonoTypeface =
        new(Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Сколько держать надпись «Скопировано» вместо имени языка.</summary>
    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    // Живой рендер пересобирает блок примерно двенадцать раз в секунду, а замер строк —
    // самая дорогая его часть. Ключ включает DPI: на другом мониторе ширина другая.
    // Блокировки нет и не нужно: документ собирается только на UI-потоке.
    private const int WidthCacheCapacity = 64;
    private static readonly Dictionary<(string Code, double Dip), double> WidthCache = [];
    private static readonly Queue<(string Code, double Dip)> WidthCacheOrder = new();

    /// <param name="cache">
    /// Запоминать ли подсветку и замер ширины. <c>false</c> — пока блок дописывает модель:
    /// у растущего текста ключ меняется на каждой перерисовке, попаданий не бывает, а
    /// промежуточные состояния вытесняют из кэша уже дописанные блоки.
    /// </param>
    /// <param name="chatMenu">
    /// Блок стоит в ленте чата: системное меню по правому клику у него снимается, и событие
    /// доходит до ленты, которая открывает своё — с «Ответить». В журнале и окне подтверждения
    /// ленты нет, и там остаётся штатное меню.
    /// </param>
    public static FrameworkElement Create(
        FrameworkElement host,
        string code,
        string? language,
        bool cache = true,
        bool chatMenu = false)
    {
        code = code.Replace("\r\n", "\n").TrimEnd('\n');

        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "text" : language.Trim().ToLowerInvariant(),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

        var copy = ChatMessageViews.IconAction(host, "Copy", "Копировать");
        copy.Focusable = false;
        copy.VerticalAlignment = VerticalAlignment.Center;
        copy.Margin = new Thickness(0);
        AttachCopy(copy, label, code);

        var header = new Grid { Margin = new Thickness(10, 3, 4, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(label);
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var text = BuildCodeText(host, code, language, cache);
        if (chatMenu)
        {
            // Именно локальный null, а не пустое значение: встретив его, редактор текста не
            // открывает своё меню и не гасит событие, и оно всплывает до ленты.
            text.ContextMenu = null;
        }
        var scroller = new ScrollViewer
        {
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            Padding = new Thickness(0, 0, 0, 2)
        };

        var body = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9, 10, 9),
            Child = scroller
        };
        body.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(body);

        var root = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 2, 0, 10),
            Child = stack
        };
        root.SetResourceReference(Border.BackgroundProperty, "Bg.Sidebar");
        root.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");

        ForwardMouseWheel(root, scroller);
        return root;
    }

    /// <summary>
    /// Тело — read-only <see cref="RichTextBox"/>: он даёт и цветные Run-ы, и выделение кода мышью.
    /// Ширину страницы задаём по самой длинной строке, иначе документ начнёт переносить код.
    /// </summary>
    private static RichTextBox BuildCodeText(
        FrameworkElement host,
        string code,
        string? language,
        bool cache)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = CodeLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            TextAlignment = TextAlignment.Left
        };

        foreach (var span in CodeHighlighter.Highlight(code, language, cache))
        {
            var run = new Run(span.Text);
            run.SetResourceReference(TextElement.ForegroundProperty, BrushKey(span.Kind));
            paragraph.Inlines.Add(run);
        }

        var width = MeasureWidest(code, host, cache);
        var document = new FlowDocument(paragraph)
        {
            PagePadding = new Thickness(0),
            FontFamily = Mono,
            FontSize = CodeFontSize,
            LineHeight = CodeLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            PageWidth = width
        };

        var box = new RichTextBox
        {
            Document = document,

            // ScrollViewer меряет содержимое бесконечной шириной, а TextBoxBase в таком
            // замере схлопывается до десятка пикселей и игнорирует PageWidth. Ширину
            // задаём явно — тем же приёмом, что и пузырь пользователя в FitUserBubble.
            Width = width,
            IsReadOnly = true,
            IsUndoEnabled = false,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            FontFamily = Mono,
            FontSize = CodeFontSize,
            CaretBrush = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null
        };
        box.SetResourceReference(Control.ForegroundProperty, "Text.Secondary");
        box.SetResourceReference(TextBoxBase.SelectionBrushProperty, "Bg.Elevated");
        return box;
    }

    private static string BrushKey(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => "Code.Keyword",
        CodeTokenKind.Control => "Code.Control",
        CodeTokenKind.String => "Code.String",
        CodeTokenKind.Comment => "Code.Comment",
        CodeTokenKind.Number => "Code.Number",
        CodeTokenKind.Type => "Code.Type",
        CodeTokenKind.Variable => "Code.Variable",
        CodeTokenKind.Function => "Code.Function",
        CodeTokenKind.Operator => "Code.Operator",
        _ => "Text.Secondary"
    };

    /// <summary>
    /// Ширина самой длинной строки. Меряем построчно: FormattedText на многострочном тексте
    /// округляет иначе, чем раскладка документа, и код начинает переноситься.
    /// </summary>
    private static double MeasureWidest(string code, FrameworkElement dpiHost, bool cache)
    {
        if (code.Length == 0)
        {
            return 1;
        }

        var dip = 1.0;
        try
        {
            dip = Math.Max(1.0, VisualTreeHelper.GetDpi(dpiHost).PixelsPerDip);
        }
        catch
        {
            // design-time / окно ещё не в дереве
        }

        var key = (code, dip);
        if (cache && WidthCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var widest = 0.0;
        foreach (var line in code.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var formatted = new FormattedText(
                line,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                MonoTypeface,
                CodeFontSize,
                Brushes.White,
                dip);

            widest = Math.Max(widest, formatted.WidthIncludingTrailingWhitespace);
        }

        // Запас на округление раскладки: без него хвост длинной строки всё равно уезжает вниз.
        var width = Math.Max(1, Math.Ceiling(widest) + 16);

        if (!cache)
        {
            return width;
        }

        WidthCache[key] = width;
        WidthCacheOrder.Enqueue(key);
        while (WidthCacheOrder.Count > WidthCacheCapacity)
        {
            WidthCache.Remove(WidthCacheOrder.Dequeue());
        }

        return width;
    }

    private static void AttachCopy(Button copy, TextBlock label, string code)
    {
        var original = label.Text;
        DispatcherTimer? restore = null;

        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(code);
            }
            catch
            {
                // Буфер обмена может держать другой процесс — молча переживаем.
                return;
            }

            label.Text = "Скопировано";
            restore?.Stop();
            restore = new DispatcherTimer { Interval = CopiedFor };
            restore.Tick += (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                label.Text = original;
            };
            restore.Start();
        };

        // Блок могут выбросить из документа посреди отсчёта — таймер не должен его держать.
        copy.Unloaded += (_, _) => restore?.Stop();
    }

    /// <summary>
    /// Внутренние ScrollViewer и RichTextBox съедают колесо мыши, из-за чего чат перестаёт
    /// прокручиваться над блоком кода. Перебрасываем событие внешнему списку сообщений,
    /// а Shift+колесо оставляем себе для горизонтальной прокрутки.
    /// </summary>
    private static void ForwardMouseWheel(UIElement root, ScrollViewer scroller)
    {
        root.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled)
            {
                return;
            }

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta);
                e.Handled = true;
                return;
            }

            // Некому передать — лучше отдать событие как есть, чем проглотить его.
            if (OuterScroller(root) is not { } outer)
            {
                return;
            }

            e.Handled = true;
            outer.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = outer
            });
        };
    }

    /// <summary>
    /// Ближайший предок, который вправду умеет прокручиваться. Пропускаем ScrollViewer внутри
    /// шаблона самого тела сообщения: прокрутка там выключена, и событие бы там и умерло.
    /// </summary>
    internal static ScrollViewer? OuterScroller(DependencyObject start)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { VerticalScrollBarVisibility: not ScrollBarVisibility.Disabled } viewer)
            {
                return viewer;
            }
        }

        return null;
    }
}
