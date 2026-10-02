using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок кода: шапка с языком и кнопкой «копировать», под ней подсвеченный моноширинный текст
/// с горизонтальной прокруткой. Кладётся в <see cref="BlockUIContainer"/> внутри тела сообщения.
/// </summary>
internal static class CodeBlockView
{
    // Размер и шрифт кода — от настроек ленты (I3). Начертание для замера строк держит
    // ChatFonts: составное имя семейства разбирается при создании, а замер идёт на каждую
    // строку каждого блока каждой перерисовки.
    private static double CodeFontSize => ChatFonts.CodeSize;
    private static double CodeLineHeight => ChatFonts.CodeLine;
    private static FontFamily Mono => ChatFonts.Mono;
    private static Typeface MonoTypeface => ChatFonts.MonoTypeface;

    /// <summary>Сколько держать надпись «Скопировано» вместо имени языка.</summary>
    private static readonly TimeSpan CopiedFor = TimeSpan.FromSeconds(1.5);

    // Живой рендер пересобирает блок примерно двенадцать раз в секунду, а замер строк —
    // самая дорогая его часть. Ключ включает DPI: на другом мониторе ширина другая.
    // Блокировки нет и не нужно: документ собирается только на UI-потоке.
    private const int WidthCacheCapacity = 64;
    private static readonly Dictionary<(string Code, double Dip, int Fonts), double> WidthCache = [];
    private static readonly Queue<(string Code, double Dip, int Fonts)> WidthCacheOrder = new();

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

        // Кнопки фокусируемы (D13): до них доходят Tab-ом, и дикторы читают их подписи.
        var copy = ChatMessageViews.IconAction(host, "Copy", Loc.Get("S.Common.Copy"));
        Accessible(copy, Loc.Get("S.Common.Copy"));
        AttachCopy(copy, label, code);

        var save = ChatMessageViews.IconAction(host, "ExportJson", Loc.Get("S.Code.Save"));
        Accessible(save, Loc.Get("S.Code.Save"));
        save.Click += (_, _) => SaveToFile(save, code, language);

        var wrap = GlyphButton(host, "M1,2 L13,2 M1,6 L11,6 A2.5,2.5 0 0 1 11,11 L7,11 M8.5,9 L6.5,11 L8.5,13 M1,10 L4,10", Loc.Get("S.Code.Wrap"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (CodeLanguages.IsRunnableScript(language))
        {
            var run = GlyphButton(host, "M3,1.5 L12,7 L3,12.5 Z", Loc.Get("S.Code.RunViaAgent"));
            run.Click += (_, _) => run.RaiseEvent(new RunScriptEventArgs(RunScriptEvent, run, code, CodeLanguages.Normalize(language)));
            buttons.Children.Add(run);
        }

        buttons.Children.Add(wrap);
        buttons.Children.Add(save);
        buttons.Children.Add(copy);

        var header = new Grid { Margin = new Thickness(10, 3, 4, 3) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(label);
        Grid.SetColumn(buttons, 1);
        header.Children.Add(buttons);

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

        // Номера строк (D13) — отдельной колонкой тем же шрифтом и интерлиньяжем: выделение
        // и копирование берут только код. Включаются настройкой, а не кнопкой: это привычка
        // читателя, а не свойство блока.
        FrameworkElement content = scroller;
        TextBlock? gutter = null;
        if (ShowLineNumbers?.Invoke() == true)
        {
            var lines = code.Split('\n').Length;
            gutter = new TextBlock
            {
                Text = string.Join("\n", Enumerable.Range(1, lines)),
                FontFamily = Mono,
                FontSize = CodeFontSize,
                LineHeight = CodeLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                TextAlignment = TextAlignment.Right,
                Margin = new Thickness(0, 0, 12, 0),
                IsHitTestVisible = false
            };
            gutter.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(scroller, 1);
            grid.Children.Add(gutter);
            grid.Children.Add(scroller);
            content = grid;
        }

        // Перенос строк (D13): ширина текста — по колонке, а не по самой длинной строке. Номера
        // строк при переносе убираются — с ними перенесённое читалось бы новой строкой.
        var fixedWidth = text.Width;
        wrap.Click += (_, _) =>
        {
            var wrapped = double.IsNaN(text.Width);
            if (wrapped)
            {
                text.Width = fixedWidth;
                text.Document.PageWidth = fixedWidth;
                scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            }
            else
            {
                text.Width = double.NaN;
                text.Document.PageWidth = double.NaN;
                scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }

            if (gutter is not null)
            {
                gutter.Visibility = wrapped ? Visibility.Visible : Visibility.Collapsed;
            }

            wrap.Opacity = wrapped ? 1 : 0.65;
            wrap.ToolTip = Loc.Get(wrapped ? "S.Code.Wrap" : "S.Code.NoWrap");
        };

        var body = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 9, 10, 9),
            Child = content
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

            // Обычный текст берёт цвет у самого поля — своя ссылка на ресурс у каждого такого
            // куска только утяжеляла документ.
            if (span.Kind != CodeTokenKind.Plain)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, BrushKey(span.Kind));
            }

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

        var key = (code, dip, ChatFonts.Version);
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

    /// <summary>
    /// Показывать ли номера строк (<see cref="AppSettings.CodeLineNumbers"/>). Ставит окно при
    /// привязке служб; null — нет. Статическое состояние: тест, который его меняет, возвращает прежнее.
    /// </summary>
    internal static Func<bool>? ShowLineNumbers { get; set; }

    /// <summary>
    /// «Выполнить через агента» у скриптов PowerShell, cmd и bat (D13): всплывает до окна, а оно
    /// кладёт задачу агенту в поле ввода. Сам скрипт проходит обычное подтверждение шлюза.
    /// </summary>
    internal static readonly RoutedEvent RunScriptEvent = EventManager.RegisterRoutedEvent(
        "RunScript", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(CodeBlockView));

    internal sealed class RunScriptEventArgs(RoutedEvent routedEvent, object source, string code, string language)
        : RoutedEventArgs(routedEvent, source)
    {
        public string Code { get; } = code;

        public string Language { get; } = language;
    }

    private static void Accessible(Button button, string name)
    {
        button.Focusable = true;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Margin = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(button, name);
    }

    /// <summary>Кнопка со значком-контуром — для действий, у которых нет картинки в наборе значков.</summary>
    private static Button GlyphButton(FrameworkElement host, string data, string tooltip)
    {
        var glyph = new System.Windows.Shapes.Path
        {
            Data = Glyphs.Get(data),
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(6)
        };
        glyph.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Text.Dim");
        var button = new Button
        {
            Style = (Style)host.FindResource("MsgActionButton"),
            ToolTip = tooltip,
            Content = glyph
        };
        Accessible(button, tooltip);
        return button;
    }

    /// <summary>«Сохранить»: диалог с расширением по языку блока.</summary>
    private static void SaveToFile(Button anchor, string code, string? language)
    {
        var extension = CodeLanguages.Extension(language);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get("S.Code.Save"),
            FileName = "code" + extension,
            DefaultExt = extension,
            Filter = $"{CodeLanguages.Normalize(language)}|*{extension}|{Loc.Get("S.Common.AllFiles")}|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(anchor)) != true)
        {
            return;
        }

        try
        {
            // Скрипты Windows PowerShell 5.1 без BOM читает в кодировке системы — кириллица
            // в них ломается; остальное — без BOM, как принято.
            var bom = extension is ".ps1" or ".psm1" or ".bat" or ".cmd";
            File.WriteAllText(dialog.FileName, code.Replace("\n", Environment.NewLine), new System.Text.UTF8Encoding(bom));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            anchor.ToolTip = Loc.Format("S.Export.Failed", ex.Message);
        }
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

            label.Text = Loc.Get("S.Common.Copied");
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
