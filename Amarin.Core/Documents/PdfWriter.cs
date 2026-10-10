using System.Globalization;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;
using AddedFont = UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont;

namespace Amarin.Core;

/// <summary>Что при сборке пришлось пропустить — модель скажет об этом человеку.</summary>
internal sealed class PdfBuildReport
{
    public List<string> Skipped { get; } = [];

    public int Pages { get; set; }
}

/// <summary>
/// PDF из Markdown: A4, заголовки, абзацы с переносом по словам, списки, таблицы с сеткой,
/// код на сером фоне, цитаты, ссылки и картинки.
/// </summary>
/// <remarks>
/// Вёрстка своя, простая и предсказуемая: принтер «Microsoft Print to PDF», которым печатается
/// чат (<c>ChatPrint</c>), спрашивает имя файла окном, а модель окном ничего не спросит. Ссылки в
/// файле — настоящие, по ним щёлкают.
/// </remarks>
internal static class PdfWriter
{
    public static PdfBuildReport Create(string path, string markdown, Func<string, byte[]?> images, bool overwrite = false)
    {
        var report = new PdfBuildReport();
        var bytes = Render(MarkdownBlocks.Parse(markdown), images, report);
        DocumentFiles.WriteAtomically(path, stream => stream.Write(bytes), overwrite);
        return report;
    }

    internal static byte[] Render(IReadOnlyList<DocBlock> blocks, Func<string, byte[]?> images, PdfBuildReport report)
    {
        using var builder = new PdfDocumentBuilder();
        var layout = new PdfLayout(builder, images, report);
        foreach (var block in blocks)
        {
            layout.Block(block, indent: 0);
        }

        report.Pages = layout.Pages;
        return builder.Build();
    }
}

/// <summary>Вёрстка страниц: блок за блоком сверху вниз, новая страница — когда следующая строка не влезает.</summary>
internal sealed class PdfLayout
{
    private const double PageWidth = 595.28;
    private const double PageHeight = 841.89;
    private const double Margin = 56;
    private const double Top = 60;
    private const double Bottom = 56;
    private const double BodySize = 10.5;
    private const double Leading = 1.38;
    private const double ContentWidth = PageWidth - (2 * Margin);

    private readonly PdfDocumentBuilder _builder;
    private readonly Func<string, byte[]?> _images;
    private readonly PdfBuildReport _report;
    private readonly PdfFonts _fonts;
    private PdfPageBuilder _page;

    /// <summary>Верх следующей строки, в пунктах от низа страницы.</summary>
    private double _y;

    public PdfLayout(PdfDocumentBuilder builder, Func<string, byte[]?> images, PdfBuildReport report)
    {
        _builder = builder;
        _images = images;
        _report = report;
        _page = builder.AddPage(PageSize.A4);
        Pages = 1;
        _y = PageHeight - Top;
        _fonts = PdfFonts.Load(builder, _page);
    }

    public int Pages { get; private set; }

    public void Block(DocBlock block, double indent)
    {
        switch (block)
        {
            case DocHeading heading:
                var size = heading.Level switch { 1 => 19, 2 => 15, 3 => 12.5, _ => 11.5 };
                Space(heading.Level <= 2 ? 12 : 8);
                Text(heading.Spans, size, indent, ContentWidth - indent, bold: true, italic: false, color: (20, 20, 20), keepWithNext: true);
                Space(heading.Level == 1 ? 6 : 4);
                break;
            case DocText text:
                Text(text.Spans, BodySize, indent, ContentWidth - indent, bold: false, italic: false, color: (34, 34, 34));
                Space(6);
                break;
            case DocList list:
                List(list, indent);
                Space(4);
                break;
            case DocTable table:
                Table(table, indent);
                Space(8);
                break;
            case DocCode code:
                Code(code, indent);
                Space(8);
                break;
            case DocQuote quote:
                Quote(quote, indent);
                Space(6);
                break;
            case DocRule:
                Ensure(12);
                _page.SetStrokeColor(200, 200, 200);
                _page.DrawLine(new PdfPoint(Margin + indent, _y - 6), new PdfPoint(PageWidth - Margin, _y - 6), 0.6);
                _page.ResetColor();
                _y -= 12;
                break;
            case DocPageBreak:
                NewPage();
                break;
            case DocImage image:
                Image(image, indent);
                break;
        }
    }

    private void Space(double points) => _y -= points;

    private void NewPage()
    {
        _page = _builder.AddPage(PageSize.A4);
        Pages++;
        _y = PageHeight - Top;
    }

    /// <summary>Хватит ли места на этой странице; нет — новая.</summary>
    private void Ensure(double height)
    {
        if (_y - height < Bottom)
        {
            NewPage();
        }
    }

    /// <summary>Строки абзаца: куски одного начертания со смещением от левого края.</summary>
    private sealed record Piece(string Text, AddedFont Font, double X, double Width, string? Link, bool Code);

    private void Text(
        IReadOnlyList<DocSpan> spans,
        double size,
        double indent,
        double width,
        bool bold,
        bool italic,
        (byte R, byte G, byte B) color,
        bool keepWithNext = false,
        string? marker = null)
    {
        var lines = Wrap(spans, size, width, bold, italic);
        var height = size * Leading;
        if (keepWithNext)
        {
            // Заголовок не остаётся последней строкой страницы: под ним должна влезть хотя бы
            // пара строк текста.
            Ensure((lines.Count * height) + (BodySize * Leading * 2));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            Ensure(height);
            var baseline = _y - (size * 1.02);
            if (i == 0 && marker is not null)
            {
                Draw(marker, _fonts.Regular, size, Margin + indent - 14, baseline, color);
            }

            foreach (var piece in lines[i])
            {
                var x = Margin + indent + piece.X;
                if (piece.Code)
                {
                    _page.SetTextAndFillColor(240, 240, 240);
                    _page.DrawRectangle(new PdfPoint(x - 1, baseline - (size * 0.28)), piece.Width + 2, size * 1.25, 0, fill: true);
                }

                var tint = piece.Link is not null ? ((byte)5, (byte)99, (byte)193) : color;
                Draw(piece.Text, piece.Font, size, x, baseline, tint);
                if (piece.Link is { } link && Uri.TryCreate(link, UriKind.Absolute, out _))
                {
                    _page.AddLink(link, new PdfRectangle(x, baseline - (size * 0.25), x + piece.Width, baseline + (size * 0.85)));
                }
            }

            _y -= height;
        }
    }

    private void Draw(string text, AddedFont font, double size, double x, double baseline, (byte R, byte G, byte B) color)
    {
        if (text.Trim().Length == 0)
        {
            return;
        }

        _page.SetTextAndFillColor(color.R, color.G, color.B);
        _page.AddText(text, size, new PdfPoint(x, baseline), font);
        _page.ResetColor();
    }

    /// <summary>Перенос по словам: слово, не влезающее в строку целиком, переносится по буквам.</summary>
    private List<List<Piece>> Wrap(IReadOnlyList<DocSpan> spans, double size, double width, bool bold, bool italic)
    {
        var lines = new List<List<Piece>> { new() };
        var x = 0.0;
        foreach (var span in spans)
        {
            var font = _fonts.For(bold || span.Bold, italic || span.Italic, span.Code);
            foreach (var token in Tokens(span.Text))
            {
                if (token == "\n")
                {
                    lines.Add([]);
                    x = 0;
                    continue;
                }

                foreach (var (text, pieceFont) in _fonts.Split(token, font))
                {
                    var tokenWidth = _fonts.Width(text, pieceFont, size);
                    if (x + tokenWidth > width && x > 0)
                    {
                        if (text.Trim().Length == 0)
                        {
                            continue;
                        }

                        lines.Add([]);
                        x = 0;
                    }

                    if (tokenWidth > width)
                    {
                        foreach (var part in BreakWord(text, pieceFont, size, width - x))
                        {
                            var partWidth = _fonts.Width(part, pieceFont, size);
                            if (x + partWidth > width && x > 0)
                            {
                                lines.Add([]);
                                x = 0;
                            }

                            lines[^1].Add(new Piece(part, pieceFont, x, partWidth, span.Link, span.Code));
                            x += partWidth;
                        }

                        continue;
                    }

                    lines[^1].Add(new Piece(text, pieceFont, x, tokenWidth, span.Link, span.Code));
                    x += tokenWidth;
                }
            }
        }

        if (lines.Count > 1 && lines[^1].Count == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    /// <summary>Слова вместе с пробелом за ними; перевод строки — отдельным знаком.</summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > start)
                {
                    yield return text[start..i];
                }

                yield return "\n";
                start = i + 1;
            }
            else if (text[i] == ' ' && (i + 1 == text.Length || text[i + 1] != ' '))
            {
                yield return text[start..(i + 1)];
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            yield return text[start..];
        }
    }

    private IEnumerable<string> BreakWord(string word, AddedFont font, double size, double firstRoom)
    {
        var start = 0;
        var room = firstRoom;
        for (var end = 1; end <= word.Length; end++)
        {
            if (_fonts.Width(word[start..end], font, size) > Math.Max(room, size) && end - 1 > start)
            {
                yield return word[start..(end - 1)];
                start = end - 1;
                room = ContentWidth;
            }
        }

        if (start < word.Length)
        {
            yield return word[start..];
        }
    }

    private void List(DocList list, double indent)
    {
        var number = list.Start;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? number.ToString(CultureInfo.InvariantCulture) + "." : "•";
            number++;
            var first = true;
            foreach (var block in item)
            {
                if (block is DocText text)
                {
                    Text(text.Spans, BodySize, indent + 16, ContentWidth - indent - 16, bold: false, italic: false,
                        color: (34, 34, 34), marker: first ? marker : null);
                    Space(2);
                }
                else if (block is DocList nested)
                {
                    List(nested, indent + 16);
                }
                else
                {
                    Block(block, indent + 16);
                }

                first = false;
            }
        }
    }

    private void Quote(DocQuote quote, double indent)
    {
        var top = _y;
        foreach (var block in quote.Blocks)
        {
            if (block is DocText text)
            {
                Text(text.Spans, BodySize, indent + 12, ContentWidth - indent - 12, bold: false, italic: true, color: (90, 90, 90));
            }
            else
            {
                Block(block, indent + 12);
            }
        }

        // Черта слева — только на той странице, где цитата началась и, если влезла, кончилась.
        if (_y < top)
        {
            _page.SetStrokeColor(190, 190, 190);
            _page.DrawLine(new PdfPoint(Margin + indent + 3, top - 2), new PdfPoint(Margin + indent + 3, _y + 4), 2.2);
            _page.ResetColor();
        }
    }

    private void Code(DocCode code, double indent)
    {
        const double size = 9;
        var height = size * 1.35;
        var lines = code.Text.Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            Ensure(height + 8);
            // Сколько строк кода влезает на эту страницу — под ними один серый прямоугольник.
            var fit = Math.Max(1, (int)((_y - Bottom - 8) / height));
            var chunk = lines.Skip(index).Take(fit).ToList();
            var boxHeight = (chunk.Count * height) + 8;
            _page.SetTextAndFillColor(244, 244, 244);
            _page.DrawRectangle(new PdfPoint(Margin + indent, _y - boxHeight), ContentWidth - indent, boxHeight, 0, fill: true);
            _page.ResetColor();
            _y -= 4;
            foreach (var line in chunk)
            {
                var x = Margin + indent + 6;
                foreach (var (text, font) in _fonts.Split(line, _fonts.Mono))
                {
                    var room = PageWidth - Margin - 4 - x;
                    var shown = text;
                    while (shown.Length > 0 && _fonts.Width(shown, font, size) > room)
                    {
                        shown = shown[..^1];
                    }

                    Draw(shown, font, size, x, _y - (size * 1.02), (40, 40, 40));
                    x += _fonts.Width(shown, font, size);
                }

                _y -= height;
            }

            _y -= 4;
            index += chunk.Count;
        }
    }

    private const double TableSize = 9.5;

    /// <summary>Уже этого столбец не читается: от неё и считается, сколько столбцов помещается на странице.</summary>
    internal const double MinColumn = 36;
    private const double TablePadding = 4;

    /// <summary>Таблица; шире страницы — блоками столбцов, один под другим.</summary>
    private void Table(DocTable table, double indent)
    {
        var available = ContentWidth - indent;
        var natural = NaturalWidths(table, available);
        if (natural.Length == 0)
        {
            return;
        }

        var blocks = MarkdownBlocks.ColumnBlocks(table, [.. natural.Select(width => Math.Max(width, MinColumn))], available);
        for (var b = 0; b < blocks.Count; b++)
        {
            if (b > 0)
            {
                _y -= TableSize * Leading;
            }

            TableBlock(blocks[b], indent);
        }
    }

    private double[] NaturalWidths(DocTable table, double available)
    {
        var columns = table.Rows.Count == 0 ? 0 : table.Rows.Max(row => row.Count);
        var natural = new double[columns];
        foreach (var row in table.Rows)
        {
            for (var c = 0; c < row.Count; c++)
            {
                var text = MarkdownBlocks.PlainText(row[c]);
                natural[c] = Math.Max(natural[c], Math.Min(available, NaturalWidth(text, TableSize) + (2 * TablePadding)));
            }
        }

        return natural;
    }

    private void TableBlock(DocTable table, double indent)
    {
        const double size = TableSize;
        const double padding = TablePadding;
        var available = ContentWidth - indent;
        var natural = NaturalWidths(table, available);
        if (natural.Length == 0)
        {
            return;
        }

        var widths = Fit(natural, available);
        IReadOnlyList<IReadOnlyList<DocSpan>>? header = table.HasHeader ? table.Rows[0] : null;
        for (var r = 0; r < table.Rows.Count; r++)
        {
            var isHeader = table.HasHeader && r == 0;
            var cells = Cells(table.Rows[r], widths, size, padding, isHeader);
            var rowHeight = (cells.Max(lines => lines.Count) * size * Leading) + (2 * padding);
            if (_y - rowHeight < Bottom && r > 0)
            {
                NewPage();

                // Шапка таблицы повторяется на новой странице: без неё столбцы второй страницы
                // читались бы вслепую.
                if (header is not null && !isHeader)
                {
                    DrawRow(Cells(header, widths, size, padding, header: true), widths, size, padding, indent, shade: true);
                }
            }

            DrawRow(cells, widths, size, padding, indent, shade: isHeader);
        }
    }

    private List<List<List<Piece>>> Cells(IReadOnlyList<IReadOnlyList<DocSpan>> row, double[] widths, double size, double padding, bool header)
    {
        var result = new List<List<List<Piece>>>();
        for (var c = 0; c < widths.Length; c++)
        {
            var spans = c < row.Count ? row[c] : [];
            result.Add(Wrap(spans, size, widths[c] - (2 * padding), header, italic: false));
        }

        return result;
    }

    private void DrawRow(List<List<List<Piece>>> cells, double[] widths, double size, double padding, double indent, bool shade)
    {
        var rowHeight = (cells.Max(lines => lines.Count) * size * Leading) + (2 * padding);
        Ensure(rowHeight);
        var x = Margin + indent;
        var bottom = _y - rowHeight;
        if (shade)
        {
            _page.SetTextAndFillColor(242, 242, 242);
            _page.DrawRectangle(new PdfPoint(x, bottom), widths.Sum(), rowHeight, 0, fill: true);
            _page.ResetColor();
        }

        for (var c = 0; c < widths.Length; c++)
        {
            var baseline = _y - padding - (size * 1.02);
            foreach (var line in cells[c])
            {
                foreach (var piece in line)
                {
                    Draw(piece.Text, piece.Font, size, x + padding + piece.X, baseline, piece.Link is null ? ((byte)34, (byte)34, (byte)34) : ((byte)5, (byte)99, (byte)193));
                }

                baseline -= size * Leading;
            }

            _page.SetStrokeColor(191, 191, 191);
            _page.DrawRectangle(new PdfPoint(x, bottom), widths[c], rowHeight, 0.5);
            _page.ResetColor();
            x += widths[c];
        }

        _y = bottom;
    }

    private double NaturalWidth(string text, double size)
    {
        var width = 0.0;
        foreach (var (piece, font) in _fonts.Split(text, _fonts.Regular))
        {
            width += _fonts.Width(piece, font, size);
        }

        return width;
    }

    /// <summary>
    /// Ширины столбцов: по содержимому, если всё влезает; иначе узкие остаются как есть, а
    /// широкие делят остаток поровну — таблица не выходит за поля.
    /// </summary>
    internal static double[] Fit(double[] natural, double available)
    {
        var widths = natural.Select(width => Math.Max(width, MinColumn)).ToArray();
        if (widths.Sum() <= available)
        {
            return widths;
        }

        var fixedSet = new bool[widths.Length];
        while (true)
        {
            var flexible = Enumerable.Range(0, widths.Length).Where(i => !fixedSet[i]).ToList();
            if (flexible.Count == 0)
            {
                break;
            }

            var share = (available - Enumerable.Range(0, widths.Length).Where(i => fixedSet[i]).Sum(i => widths[i])) / flexible.Count;
            var narrow = flexible.Where(i => widths[i] <= share).ToList();
            if (narrow.Count == 0)
            {
                foreach (var i in flexible)
                {
                    widths[i] = Math.Max(MinColumn, share);
                }

                break;
            }

            foreach (var i in narrow)
            {
                fixedSet[i] = true;
            }
        }

        return widths;
    }

    private void Image(DocImage image, double indent)
    {
        var bytes = _images(image.Source);
        var format = bytes is null ? null : ImageFormats.Sniff(bytes);
        if (bytes is null || format is null)
        {
            _report.Skipped.Add(bytes is null ? $"image not found: {image.Source}" : $"image {image.Source} is not PNG, JPEG, GIF or BMP");
            return;
        }

        var (pixelWidth, pixelHeight) = ImageFormats.Size(bytes);
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            _report.Skipped.Add($"image {image.Source} has no readable size");
            return;
        }

        // Точка картинки — пункт печати при 96 точках на дюйм; шире полей и выше страницы — по месту.
        var width = Math.Min(ContentWidth - indent, pixelWidth * 0.75);
        var height = width * pixelHeight / pixelWidth;
        var maxHeight = PageHeight - Top - Bottom;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * pixelWidth / pixelHeight;
        }

        Ensure(height + 6);
        var left = Margin + indent + ((ContentWidth - indent - width) / 2);
        var rectangle = new PdfRectangle(left, _y - height, left + width, _y);
        var png = format.Value.MimeType == "image/png" ? bytes : format.Value.MimeType == "image/jpeg" ? null : ImageFormats.ToPng(bytes);
        if (format.Value.MimeType == "image/jpeg")
        {
            _page.AddJpeg(bytes, rectangle);
        }
        else if (png is not null)
        {
            _page.AddPng(png, rectangle);
        }
        else
        {
            _report.Skipped.Add($"image {image.Source} could not be converted");
            return;
        }

        _y -= height + 4;
        if (image.Caption.Trim().Length > 0)
        {
            Text([new DocSpan(image.Caption.Trim(), Italic: true)], 9, indent, ContentWidth - indent, bold: false, italic: true, color: (110, 110, 110));
        }

        Space(6);
    }
}
