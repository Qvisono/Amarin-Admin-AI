using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Исходник элемента, который в документе нарисован картинкой, а не текстом: блок кода,
/// формула, моноширинный кусок строки.
/// </summary>
/// <remarks>
/// Три части, а не готовая строка: блок кода перестраивается при каждой перерисовке живого
/// ответа, и склеивать ради цитаты, которую, может, никто не возьмёт, весь его текст с
/// ограждением было бы пустой тратой. Склеивает <see cref="QuoteSelection.Compose"/> — только
/// когда человек и правда цитирует.
/// </remarks>
/// <param name="Block">Блочный элемент: встаёт своими строками, ограждение — на отдельных.</param>
internal sealed record QuoteSource(string Open, string Body, string Close, bool Block = false);

/// <summary>Снимок выделения, готовый стать цитатой.</summary>
internal sealed record QuoteDraft(string SourceId, string Text, string? Context);

/// <summary>
/// Выделение в ответе модели → текст цитаты, и обратно: фрагмент цитаты → место в ответе.
/// </summary>
/// <remarks>
/// <para>
/// <c>Selection.Text</c> здесь не годится: формулы и блоки кода лежат в документе элементами
/// интерфейса (<see cref="InlineUIContainer"/>, <see cref="BlockUIContainer"/>), и он молча
/// выбрасывает их — цитата «выполните <c>netsh …</c>» доезжала бы до модели как «выполните».
/// Поэтому документ обходится вручную, а вместо встроенного элемента берётся исходник,
/// который положил на него <see cref="ChatMarkdown"/> (<see cref="SourceProperty"/>).
/// </para>
/// <para>
/// Выделение не пересекает сообщения: у каждого свой <see cref="RichTextBox"/>, и
/// выделение живёт в одном из них.
/// </para>
/// </remarks>
internal static class QuoteSelection
{
    /// <summary>Исходник встроенного элемента или моноширинного <see cref="Run"/>.</summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(QuoteSource),
        typeof(QuoteSelection),
        new PropertyMetadata(null));

    /// <summary>Этот бокс — тело ответа модели, и из него можно цитировать.</summary>
    public static readonly DependencyProperty IsQuotableProperty = DependencyProperty.RegisterAttached(
        "IsQuotable",
        typeof(bool),
        typeof(QuoteSelection),
        new PropertyMetadata(false));

    public static void SetSource(DependencyObject element, QuoteSource? value) =>
        element.SetValue(SourceProperty, value);

    public static QuoteSource? GetSource(DependencyObject element) =>
        (QuoteSource?)element.GetValue(SourceProperty);

    public static void SetIsQuotable(DependencyObject element, bool value) =>
        element.SetValue(IsQuotableProperty, value);

    public static bool GetIsQuotable(DependencyObject element) =>
        (bool)element.GetValue(IsQuotableProperty);

    internal static string Compose(QuoteSource source) =>
        source.Block
            ? source.Open + "\n" + source.Body + "\n" + source.Close
            : source.Open + source.Body + source.Close;

    /// <summary>
    /// Текст документа между двумя позициями — с кодом и формулами вместо пустоты на их месте.
    /// </summary>
    internal static string ExtractText(TextPointer start, TextPointer end)
    {
        var sb = new StringBuilder();
        var position = start;

        while (position is not null && position.CompareTo(end) < 0)
        {
            switch (position.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.Text:
                    position = AppendText(sb, position, end);
                    continue;

                case TextPointerContext.ElementStart:
                    OpenElement(sb, position.GetAdjacentElement(LogicalDirection.Forward));
                    break;

                case TextPointerContext.EmbeddedElement:
                    if (position.GetAdjacentElement(LogicalDirection.Forward) is DependencyObject embedded &&
                        GetSource(embedded) is { } source)
                    {
                        if (source.Block)
                        {
                            BreakLine(sb);
                            sb.Append(Compose(source)).Append('\n');
                        }
                        else
                        {
                            sb.Append(Compose(source));
                        }
                    }

                    // Без исходника — картинка или карточка: в цитате ей места нет.
                    break;
            }

            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }

        return sb.ToString();
    }

    private static TextPointer? AppendText(StringBuilder sb, TextPointer position, TextPointer end)
    {
        var run = position.Parent as Run;

        // Моноширинный кусок целиком — обратно в `…`: так модель узнаёт команду, а не только
        // её буквы. Частично выделенный идёт как текст, без тонких шпаций, которыми он обложен.
        if (run is not null && GetSource(run) is { } source &&
            position.CompareTo(run.ContentStart) <= 0 &&
            end.CompareTo(run.ContentEnd) >= 0)
        {
            sb.Append(Compose(source));
            return run.ContentEnd;
        }

        var text = position.GetTextInRun(LogicalDirection.Forward);
        var available = position.GetOffsetToPosition(end);
        if (available < text.Length)
        {
            text = text[..Math.Max(0, available)];
        }

        sb.Append(run is not null && GetSource(run) is not null ? text.Trim(' ') : text);
        return position.GetPositionAtOffset(text.Length, LogicalDirection.Forward) ?? end;
    }

    private static void OpenElement(StringBuilder sb, DependencyObject? element)
    {
        switch (element)
        {
            case Paragraph paragraph:
                // Абзацы верхнего уровня отделены пустой строкой, как в разметке. Первый абзац
                // пункта списка или ячейки таблицы продолжает строку: перед ним уже стоят маркер
                // «- » или разделитель « | », и перенос отрывал бы текст от них — цитата списка
                // приходила строками «-» и «один» вместо «- один».
                if (paragraph.Parent is ListItem or TableCell)
                {
                    if (paragraph.PreviousBlock is not null)
                    {
                        BreakLine(sb);
                    }
                }
                else if (paragraph.Parent is FlowDocument or Section)
                {
                    BreakParagraph(sb);
                }
                else
                {
                    BreakLine(sb);
                }

                break;

            case ListItem item:
                BreakLine(sb);
                sb.Append(ListMarker(item));
                break;

            case TableRow or BlockUIContainer or List:
                BreakLine(sb);
                break;

            case TableCell cell when cell.Parent is TableRow row && !ReferenceEquals(row.Cells.FirstOrDefault(), cell):
                sb.Append(" | ");
                break;

            case LineBreak:
                sb.Append('\n');
                break;
        }
    }

    private static string ListMarker(ListItem item)
    {
        if (item.List is not { } list || list.MarkerStyle != TextMarkerStyle.Decimal)
        {
            return "- ";
        }

        var index = list.StartIndex;
        foreach (var sibling in list.ListItems)
        {
            if (ReferenceEquals(sibling, item))
            {
                break;
            }

            index++;
        }

        return index.ToString(CultureInfo.InvariantCulture) + ". ";
    }

    private static void BreakLine(StringBuilder sb)
    {
        if (sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
    }

    private static void BreakParagraph(StringBuilder sb)
    {
        if (sb.Length == 0)
        {
            return;
        }

        BreakLine(sb);
        if (sb.Length < 2 || sb[^2] != '\n')
        {
            sb.Append('\n');
        }
    }

    /// <summary>
    /// Снимает выделение бокса в цитату или отдаёт <c>null</c>, если цитировать нечего.
    /// </summary>
    /// <param name="box">Тело ответа — или вложенный бокс блока кода внутри него.</param>
    internal static QuoteDraft? Capture(RichTextBox box, string sourceId)
    {
        ArgumentNullException.ThrowIfNull(box);
        var selection = box.Selection;
        if (selection is null || selection.IsEmpty)
        {
            return null;
        }

        // Бокс блока кода — сам код без обёрток: исходник висит на блоке целиком, а здесь
        // выделена лишь часть его строк.
        var inner = !GetIsQuotable(box);
        var raw = inner ? selection.Text : ExtractText(selection.Start, selection.End);
        var text = ChatQuotes.Clip(ChatQuotes.Normalize(raw));
        if (text.Length == 0)
        {
            return null;
        }

        string? context = null;
        if (!inner && selection.Start.Paragraph is { } paragraph)
        {
            context = ChatQuotes.ContextAround(
                new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text,
                text);
        }

        return new QuoteDraft(sourceId, text, context);
    }

    /// <summary>
    /// Бокс, в котором произошло событие, и сообщение, которому он принадлежит.
    /// </summary>
    /// <returns>
    /// <c>Quotable</c> — бокс сам тело ответа модели или лежит внутри него (блок кода).
    /// </returns>
    internal static (RichTextBox Box, ChatMessageHost Host, bool Quotable)? FindOwner(DependencyObject? origin)
    {
        RichTextBox? box = null;
        var quotable = false;
        var current = origin;
        var guard = 0;

        while (current is not null && guard++ < 512)
        {
            if (current is RichTextBox candidate)
            {
                box ??= candidate;
                quotable |= GetIsQuotable(candidate);
            }

            if (current is ChatMessageHost host)
            {
                return box is null ? null : (box, host, quotable);
            }

            current = current is Visual
                ? VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current) ?? (current as FrameworkContentElement)?.Parent;
        }

        return null;
    }

    /// <summary>Тело ответа внутри построенного сообщения.</summary>
    internal static RichTextBox? FindQuotableBox(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RichTextBox box && GetIsQuotable(box))
            {
                return box;
            }

            // Внутрь чужих боксов не спускаемся: тело ответа не бывает вложено в другой бокс.
            if (child is RichTextBox)
            {
                continue;
            }

            if (FindQuotableBox(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Место цитаты в ответе — чтобы по клику на карточку показать, откуда её взяли.
    /// </summary>
    /// <remarks>
    /// Ищется первая содержательная строка фрагмента, а не он весь: цитата могла быть обрезана
    /// серединой, а в документе между строками стоят границы абзацев. Сравнение идёт без учёта
    /// пробелов — переносы и тонкие шпации вокруг кода в документе и в цитате расходятся.
    /// </remarks>
    internal static TextRange? Locate(FlowDocument document, string fragment)
    {
        ArgumentNullException.ThrowIfNull(document);
        var needle = Squeeze(FirstMeaningfulLine(fragment));
        if (needle.Length == 0)
        {
            return null;
        }

        // Текст документа без пробелов и позиция каждого его символа.
        var letters = new StringBuilder();
        var positions = new List<TextPointer>();
        var position = document.ContentStart;
        while (position is not null && position.CompareTo(document.ContentEnd) < 0)
        {
            if (position.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = position.GetTextInRun(LogicalDirection.Forward);
                for (var i = 0; i < text.Length; i++)
                {
                    if (!IsSpace(text[i]))
                    {
                        letters.Append(text[i]);
                        positions.Add(position.GetPositionAtOffset(i)!);
                    }
                }

                position = position.GetPositionAtOffset(text.Length) ?? document.ContentEnd;
                continue;
            }

            position = position.GetNextContextPosition(LogicalDirection.Forward);
        }

        var at = letters.ToString().IndexOf(needle, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var last = positions[at + needle.Length - 1];
        return new TextRange(positions[at], last.GetPositionAtOffset(1) ?? last);
    }

    private static string FirstMeaningfulLine(string fragment)
    {
        foreach (var raw in fragment.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '#', '>', ' ').Trim('`', '$');

            // Ограждение кода и маркеры списков в документе не нарисованы — по ним не найти.
            if (line.Length >= 3 && !raw.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                return line.Length > 120 ? line[..120] : line;
            }
        }

        return fragment.Length > 120 ? fragment[..120] : fragment;
    }

    private static string Squeeze(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!IsSpace(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static bool IsSpace(char ch) => char.IsWhiteSpace(ch) || ch is '​' or '﻿';
}
