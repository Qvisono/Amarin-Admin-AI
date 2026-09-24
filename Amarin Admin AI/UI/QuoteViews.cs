using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Как выглядят цитаты: строка в поле ввода, карточка над пузырём сообщения, пункт подсказки
/// «@» и подсвеченная ссылка <c>@N</c> в тексте.
/// </summary>
/// <remarks>
/// Все четыре вида собраны из одних деталей — полоса слева, как у цитаты в разметке ответа
/// (<c>ChatMarkdown.BuildQuote</c>), и бейдж номера, — чтобы человек узнавал цитату где бы
/// она ни стояла. Цвета только ресурсами: палитра программы монохромная, и выделять цитату
/// акцентом было бы нечем, а захардкоженная кисть не перекрасилась бы вместе с темой.
/// </remarks>
internal static class QuoteViews
{
    /// <summary>Прикидка высоты карточки в пузыре — для места под ещё не построенное сообщение.</summary>
    internal const double CardHeightEstimate = 76;

    private const double PreviewLineHeight = 17;
    private const int PreviewLines = 3;
    private const double TooltipWidth = 420;
    private const int TooltipChars = 600;

    /// <summary>Номер цитаты «@N».</summary>
    public static Border Badge(FrameworkElement host, int number)
    {
        var label = new TextBlock
        {
            Text = "@" + number.ToString(CultureInfo.InvariantCulture),
            Style = (Style)host.FindResource("QuoteBadgeText")
        };
        return new Border { Style = (Style)host.FindResource("QuoteBadge"), Child = label };
    }

    /// <summary>Откуда цитата — словами для человека.</summary>
    public static string SourceLabel(QuoteSourceKind kind, ChatDisplayMessage? source, DateFormat format)
    {
        switch (kind)
        {
            case QuoteSourceKind.Last:
                return Loc.Get("S.Quote.FromLast");
            case QuoteSourceKind.Gone:
                return Loc.Get("S.Quote.SourceGone");
            case QuoteSourceKind.Interrupted:
                return Loc.Get("S.Quote.Interrupted");
        }

        if (source is null)
        {
            return "";
        }

        // Часов хватает, пока ответ сегодняшний; из вчерашнего чата «14:02» ничего не скажет.
        var when = source.CreatedAt.Date == DateTime.Today
            ? ChatFormat.Clock(source.CreatedAt)
            : ChatFormat.DateTimeShort(source.CreatedAt, format);
        return Loc.Format("S.Quote.FromAnswer", when);
    }

    /// <summary>Первая содержательная строка цитаты — то, что помещается в одну строку плашки.</summary>
    public static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            // Ограждение кода — не содержание: по «```powershell» цитату не узнать.
            if (line.Length > 0 && !line.StartsWith("```", StringComparison.Ordinal))
            {
                return line;
            }
        }

        return text.Trim();
    }

    /// <summary>Подсказка с цитатой целиком — сколько её влезает, с переносами.</summary>
    public static TextBlock Tooltip(string text, string? footer = null)
    {
        var body = text.Length <= TooltipChars ? text : text[..TooltipChars].TrimEnd() + "…";
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = TooltipWidth };
        block.Inlines.Add(new Run(body));
        if (!string.IsNullOrEmpty(footer))
        {
            block.Inlines.Add(new LineBreak());
            block.Inlines.Add(new LineBreak());
            var hint = new Run(footer);
            hint.SetResourceReference(TextElement.ForegroundProperty, "Text.Muted");
            block.Inlines.Add(hint);
        }

        return block;
    }

    /// <summary>
    /// Строка прикреплённой цитаты над полем ввода: бейдж, первая строка фрагмента, откуда он,
    /// крестик.
    /// </summary>
    /// <remarks>
    /// Строкой во всю ширину, а не плиткой, как картинки: цитата — это текст, и в плитку шириной
    /// с миниатюру его не прочесть.
    /// </remarks>
    public static Border ComposerRow(
        FrameworkElement host,
        MessageQuote quote,
        string sourceLabel,
        Action insertReference,
        Action remove)
    {
        var grid = new Grid { Height = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var bar = Bar(new Thickness(7, 7, 0, 7));
        grid.Children.Add(bar);

        var badge = Badge(host, quote.Number);
        badge.Margin = new Thickness(8, 0, 8, 0);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        var preview = new TextBlock
        {
            Text = FirstLine(quote.Text),
            FontSize = 12.5,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        Grid.SetColumn(preview, 2);
        grid.Children.Add(preview);

        var source = new TextBlock
        {
            Text = sourceLabel,
            FontSize = 11,
            Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        source.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        Grid.SetColumn(source, 3);
        grid.Children.Add(source);

        var close = RemoveButton(host, remove);
        Grid.SetColumn(close, 4);
        grid.Children.Add(close);

        var row = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 5),
            Cursor = Cursors.Hand,
            Child = grid,
            ToolTip = Tooltip(quote.Text, Loc.Format("S.Quote.InsertRef", quote.Number))
        };
        row.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
        Hover(row, "Bg.Card", "Bg.Raised");

        // Крестик гасит своё нажатие сам (Button помечает событие обработанным), поэтому сюда
        // доходят только щелчки по самой строке.
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (!e.Handled)
            {
                e.Handled = true;
                insertReference();
            }
        };
        return row;
    }

    private static Button RemoveButton(FrameworkElement host, Action remove)
    {
        var glyph = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");

        var button = new Button
        {
            Style = (Style)host.FindResource("MsgActionButton"),
            Width = 22,
            Height = 22,
            Margin = new Thickness(0, 0, 4, 0),
            Focusable = false,
            Content = glyph,
            ToolTip = Loc.Get("S.Quote.Remove")
        };
        button.Click += (_, _) => remove();
        return button;
    }

    /// <summary>
    /// Карточки цитат над пузырём отправленного сообщения. Щелчок по карточке показывает
    /// фрагмент там, откуда его взяли.
    /// </summary>
    public static FrameworkElement BubbleStrip(
        FrameworkElement host,
        ChatDisplayMessage message,
        MessageActions? actions)
    {
        // Карточки растянуты по самой широкой — ровный столбик читается лучше лесенки.
        var strip = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 560,
            Margin = new Thickness(60, 0, 0, 6)
        };

        var transcript = actions?.Transcript?.Invoke();
        var index = transcript is null ? -1 : IndexOf(transcript, message);
        var format = actions?.CurrentDateFormat?.Invoke() ?? DateFormat.DayMonthShort;

        foreach (var quote in message.Quotes)
        {
            ChatDisplayMessage? source = null;
            var kind = transcript is null || index < 0
                ? QuoteSourceKind.Gone
                : ChatQuotes.Classify(transcript, index, quote.SourceMessageId, out source);
            strip.Children.Add(BubbleCard(host, quote, SourceLabel(kind, source, format),
                kind == QuoteSourceKind.Gone ? null : actions?.ShowQuoteSource));
        }

        return strip;
    }

    private static int IndexOf(IReadOnlyList<ChatDisplayMessage> transcript, ChatDisplayMessage message)
    {
        for (var i = 0; i < transcript.Count; i++)
        {
            if (ReferenceEquals(transcript[i], message))
            {
                return i;
            }
        }

        return -1;
    }

    private static Border BubbleCard(
        FrameworkElement host,
        MessageQuote quote,
        string sourceLabel,
        Action<MessageQuote>? open)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(Badge(host, quote.Number));
        var label = new TextBlock
        {
            Text = sourceLabel,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        header.Children.Add(label);

        var fragment = new TextBlock
        {
            Text = quote.Text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = PreviewLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = PreviewLineHeight * PreviewLines,
            Margin = new Thickness(0, 5, 0, 0)
        };
        fragment.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");

        var column = new StackPanel();
        column.Children.Add(header);
        column.Children.Add(fragment);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(Bar(new Thickness(0, 1, 10, 1)));
        Grid.SetColumn(column, 1);
        grid.Children.Add(column);

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 12, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Child = grid,
            ToolTip = Tooltip(quote.Text, open is null ? null : Loc.Get("S.Quote.ShowSource"))
        };
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

        if (open is null)
        {
            card.SetResourceReference(Border.BackgroundProperty, "Bg.Card");
            return card;
        }

        card.Cursor = Cursors.Hand;
        Hover(card, "Bg.Card", "Bg.Raised");
        card.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            open(quote);
        };
        return card;
    }

    /// <summary>Пункт подсказки «@»: бейдж, первая строка фрагмента, откуда он.</summary>
    public static Button SuggestRow(
        FrameworkElement host,
        MessageQuote quote,
        string sourceLabel,
        bool highlighted,
        Action commit)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Badge(host, quote.Number));

        var preview = new TextBlock
        {
            Text = FirstLine(quote.Text),
            FontSize = 12.5,
            Margin = new Thickness(8, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        preview.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");
        Grid.SetColumn(preview, 1);
        grid.Children.Add(preview);

        var source = new TextBlock
        {
            Text = sourceLabel,
            FontSize = 11,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        source.SetResourceReference(TextBlock.ForegroundProperty, "Text.Muted");
        Grid.SetColumn(source, 2);
        grid.Children.Add(source);

        var row = new Button
        {
            Style = (Style)host.FindResource("QuotePillButton"),
            Height = 30,
            Padding = new Thickness(6, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = grid
        };

        // Выбранный стрелками пункт — тем же фоном, что и под курсором: клавиатура и мышь
        // показывают выбор одинаково.
        if (highlighted)
        {
            row.SetResourceReference(Control.BackgroundProperty, "Bg.Raised");
        }

        row.Click += (_, _) => commit();
        return row;
    }

    /// <summary>
    /// Подсвечивает в тексте сообщения ссылки <c>@N</c> на его цитаты.
    /// </summary>
    /// <remarks>
    /// Только фон и цвет, без жирного: ширину короткого пузыря меряют по обычному начертанию
    /// (<c>FitUserBubble</c>) с запасом в несколько пикселей, и жирная ссылка переносила бы
    /// строку. Ссылка на номер, которого у сообщения нет, остаётся простым текстом — так видно,
    /// что она ни на что не указывает.
    /// </remarks>
    public static void HighlightReferences(
        FlowDocument document,
        IReadOnlyList<MessageQuote> quotes,
        Action<MessageQuote>? open = null)
    {
        if (quotes.Count == 0)
        {
            return;
        }

        // Сперва собираем, потом режем: правка документа посреди обхода сбила бы указатель.
        var runs = new List<Run>();
        var position = document.ContentStart;
        while (position is not null && position.CompareTo(document.ContentEnd) < 0)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text &&
                position.Parent is Run run &&
                QuoteSelection.GetSource(run) is null &&
                (runs.Count == 0 || !ReferenceEquals(runs[^1], run)))
            {
                runs.Add(run);
            }

            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }

        foreach (var run in runs)
        {
            Split(run, quotes, open);
        }
    }

    private static void Split(Run run, IReadOnlyList<MessageQuote> quotes, Action<MessageQuote>? open)
    {
        var text = run.Text;
        var references = ChatQuotes.References(text);
        if (references.Count == 0 || run.Parent is not Paragraph and not Span)
        {
            return;
        }

        var siblings = run.SiblingInlines;
        var consumed = 0;
        foreach (var (start, length, number) in references)
        {
            var quote = quotes.FirstOrDefault(item => item.Number == number);
            if (quote is null)
            {
                continue;
            }

            if (start > consumed)
            {
                siblings.InsertBefore(run, new Run(text[consumed..start]));
            }

            siblings.InsertBefore(run, Reference(text.Substring(start, length), quote, open));
            consumed = start + length;
        }

        if (consumed > 0)
        {
            run.Text = text[consumed..];
        }
    }

    private static Run Reference(string text, MessageQuote quote, Action<MessageQuote>? open)
    {
        var reference = new Run(text)
        {
            ToolTip = Tooltip(quote.Text, open is null ? null : Loc.Get("S.Quote.ShowSource"))
        };
        reference.SetResourceReference(TextElement.ForegroundProperty, "Text.Bright");
        reference.SetResourceReference(TextElement.BackgroundProperty, "Bg.Elevated");
        if (open is not null)
        {
            // На нажатие, а не на отпускание: нажатие в боксе забирает редактор текста — он
            // захватывает мышь под выделение, и отпускание до ссылки уже не доходит. Погашенное
            // здесь нажатие заодно не начинает выделение, как и у Hyperlink.
            reference.Cursor = Cursors.Hand;
            reference.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                open(quote);
            };
        }

        return reference;
    }

    private static Border Bar(Thickness margin)
    {
        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Margin = margin
        };
        bar.SetResourceReference(Border.BackgroundProperty, "Border.Strong");
        return bar;
    }

    /// <summary>Подсветка под курсором — ресурсами, чтобы она перекрашивалась с темой.</summary>
    private static void Hover(Border element, string idle, string hover)
    {
        element.SetResourceReference(Border.BackgroundProperty, idle);
        element.MouseEnter += (_, _) => element.SetResourceReference(Border.BackgroundProperty, hover);
        element.MouseLeave += (_, _) => element.SetResourceReference(Border.BackgroundProperty, idle);
    }
}
