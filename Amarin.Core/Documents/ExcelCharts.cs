using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using C = DocumentFormat.OpenXml.Drawing.Charts;
using S = DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;

namespace Amarin.Core;

/// <summary>
/// Настоящая диаграмма Excel на листе — столбцы, линия или круг по диапазону ячеек.
/// </summary>
/// <remarks>
/// <para>
/// Ряды ссылаются на ячейки, а не несут копию чисел: человек поправит таблицу — диаграмма
/// перестроится сама, как у диаграммы, вставленной руками. Первая строка диапазона — названия
/// рядов, первый столбец — подписи, остальные столбцы — ряды; у диапазона в один столбец подписей нет.
/// </para>
/// <para>
/// Значения кладутся и в кэш диаграммы: файл, открытый в просмотрщике без пересчёта (Проводник,
/// почта), иначе показал бы пустые оси.
/// </para>
/// </remarks>
internal static class ExcelCharts
{
    /// <summary>Сколько столбцов и строк листа занимает диаграмма.</summary>
    private const int WidthColumns = 8;

    private const int HeightRows = 16;

    /// <summary>Дети листа, перед которыми по схеме стоит <c>&lt;drawing&gt;</c>.</summary>
    private static readonly Type[] AfterDrawing =
    [
        typeof(S.LegacyDrawing), typeof(S.LegacyDrawingHeaderFooter), typeof(S.DrawingHeaderFooter), typeof(S.Picture),
        typeof(S.OleObjects), typeof(S.Controls), typeof(S.WebPublishItems), typeof(S.TableParts), typeof(S.WorksheetExtensionList)
    ];

    public static void Add(WorkbookPart workbook, WorksheetPart sheet, string sheetName, string type, string range, string? title, string? cell)
    {
        if (type is not (ChartSpec.Bar or ChartSpec.Line or ChartSpec.Pie))
        {
            throw new DocumentException("add_chart type is bar, line or pie.");
        }

        if (!CellAddress.TryParseRange(range, out var area) || area.FromRow < 1 || area.FromColumn < 1 || area.ToRow > 1_048_576 || area.ToColumn > 16_384)
        {
            throw new DocumentException($"'{range}' is not a cell range. Use an A1-style range with the header row and the label column.");
        }

        if (area.ToRow - area.FromRow < 1)
        {
            throw new DocumentException("The range needs a header row and at least one row of values.");
        }

        var labelled = area.ToColumn > area.FromColumn;
        var firstSeries = labelled ? area.FromColumn + 1 : area.FromColumn;
        var seriesColumns = Enumerable.Range(firstSeries, area.ToColumn - firstSeries + 1).Take(type == ChartSpec.Pie ? 1 : ChartSpec.MaxSeries).ToList();

        var grid = ExcelGrid.Load(workbook);
        var calc = grid.Calc();
        string Shown(int column, int row)
        {
            if (!grid.Cells(sheetName).TryGetValue((column, row), out var entry))
            {
                return "";
            }

            return entry.Source.Formula is null && !entry.Source.Opaque
                ? ExcelCalc.ToText(entry.Source.Value)
                : calc.TryValue(sheetName, column, row) is { } value ? ExcelCalc.ToText(value) : "";
        }

        var prefix = ExcelEditor.Quote(sheetName) + "!";
        string Address(int column, int row) => "$" + CellAddress.ColumnName(column) + "$" + row.ToString(CultureInfo.InvariantCulture);
        var dataRows = Enumerable.Range(area.FromRow + 1, area.ToRow - area.FromRow).ToList();
        var labels = labelled ? dataRows.Select(row => Shown(area.FromColumn, row)).ToList() : null;
        var categories = labelled ? prefix + Address(area.FromColumn, area.FromRow + 1) + ":" + Address(area.FromColumn, area.ToRow) : null;

        var chartPart = Drawings(sheet).AddNewPart<ChartPart>();
        var series = seriesColumns.Select((column, index) => Series(
            type,
            (uint)index,
            prefix + Address(column, area.FromRow),
            Shown(column, area.FromRow),
            categories,
            labels,
            prefix + Address(column, area.FromRow + 1) + ":" + Address(column, area.ToRow),
            [.. dataRows.Select(row => Shown(column, row))])).ToList();
        chartPart.ChartSpace = ChartSpace(type, title, series);
        chartPart.ChartSpace.Save();

        var anchorColumn = area.ToColumn + 1;
        var anchorRow = area.FromRow;
        if (cell is { Length: > 0 } && !CellAddress.TryParse(cell, out anchorColumn, out anchorRow))
        {
            throw new DocumentException($"'{cell}' is not a cell address. cell is the A1-style top-left corner of the chart.");
        }

        Anchor(Drawings(sheet), chartPart, anchorColumn, anchorRow, title ?? "Chart");
    }

    /// <summary>Часть рисунков листа; нет её — создаётся и подключается к листу в своё место по схеме.</summary>
    private static DrawingsPart Drawings(WorksheetPart sheet)
    {
        if (sheet.DrawingsPart is { } existing)
        {
            return existing;
        }

        var drawings = sheet.AddNewPart<DrawingsPart>();
        drawings.WorksheetDrawing = new Xdr.WorksheetDrawing();
        var worksheet = sheet.Worksheet ?? throw new DocumentException("The sheet could not be opened.");
        var element = new S.Drawing { Id = sheet.GetIdOfPart(drawings) };
        var after = worksheet.ChildElements.FirstOrDefault(child => AfterDrawing.Contains(child.GetType()));
        if (after is null)
        {
            worksheet.Append(element);
        }
        else
        {
            after.InsertBeforeSelf(element);
        }

        return drawings;
    }

    private static void Anchor(DrawingsPart drawings, ChartPart chart, int column, int row, string name)
    {
        var root = drawings.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
        var id = root.Descendants<Xdr.NonVisualDrawingProperties>().Select(item => item.Id?.Value ?? 0U).DefaultIfEmpty(1U).Max() + 1;
        root.Append(new Xdr.TwoCellAnchor(
            new Xdr.FromMarker(
                new Xdr.ColumnId((column - 1).ToString(CultureInfo.InvariantCulture)),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId((row - 1).ToString(CultureInfo.InvariantCulture)),
                new Xdr.RowOffset("0")),
            new Xdr.ToMarker(
                new Xdr.ColumnId((column - 1 + WidthColumns).ToString(CultureInfo.InvariantCulture)),
                new Xdr.ColumnOffset("0"),
                new Xdr.RowId((row - 1 + HeightRows).ToString(CultureInfo.InvariantCulture)),
                new Xdr.RowOffset("0")),
            new Xdr.GraphicFrame(
                new Xdr.NonVisualGraphicFrameProperties(
                    new Xdr.NonVisualDrawingProperties { Id = id, Name = name },
                    new Xdr.NonVisualGraphicFrameDrawingProperties()),
                new Xdr.Transform(new A.Offset { X = 0L, Y = 0L }, new A.Extents { Cx = 0L, Cy = 0L }),
                new A.Graphic(new A.GraphicData(new C.ChartReference { Id = drawings.GetIdOfPart(chart) })
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/chart"
                }))
            { Macro = "" },
            new Xdr.ClientData()));
        root.Save();
    }

    private static C.ChartSpace ChartSpace(string type, string? title, List<OpenXmlCompositeElement> series)
    {
        const uint CategoryAxis = 48_650_112U;
        const uint ValueAxis = 48_672_768U;
        OpenXmlCompositeElement plot = type switch
        {
            ChartSpec.Pie => new C.PieChart(new C.VaryColors { Val = true }),
            ChartSpec.Line => new C.LineChart(new C.Grouping { Val = C.GroupingValues.Standard }, new C.VaryColors { Val = false }),
            _ => new C.BarChart(new C.BarDirection { Val = C.BarDirectionValues.Column }, new C.BarGrouping { Val = C.BarGroupingValues.Clustered }, new C.VaryColors { Val = false })
        };
        foreach (var item in series)
        {
            plot.Append(item);
        }

        if (type == ChartSpec.Pie)
        {
            plot.Append(new C.FirstSliceAngle { Val = 0 });
        }
        else
        {
            if (type == ChartSpec.Line)
            {
                plot.Append(new C.ShowMarker { Val = true });
            }
            else
            {
                plot.Append(new C.GapWidth { Val = 150 });
            }

            plot.Append(new C.AxisId { Val = CategoryAxis }, new C.AxisId { Val = ValueAxis });
        }

        var area = new C.PlotArea(new C.Layout(), plot);
        if (type != ChartSpec.Pie)
        {
            area.Append(
                new C.CategoryAxis(
                    new C.AxisId { Val = CategoryAxis },
                    new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                    new C.Delete { Val = false },
                    new C.AxisPosition { Val = C.AxisPositionValues.Bottom },
                    new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
                    new C.CrossingAxis { Val = ValueAxis },
                    new C.Crosses { Val = C.CrossesValues.AutoZero },
                    new C.AutoLabeled { Val = true },
                    new C.LabelAlignment { Val = C.LabelAlignmentValues.Center },
                    new C.LabelOffset { Val = 100 }),
                new C.ValueAxis(
                    new C.AxisId { Val = ValueAxis },
                    new C.Scaling(new C.Orientation { Val = C.OrientationValues.MinMax }),
                    new C.Delete { Val = false },
                    new C.AxisPosition { Val = C.AxisPositionValues.Left },
                    new C.MajorGridlines(),
                    new C.NumberingFormat { FormatCode = "General", SourceLinked = true },
                    new C.TickLabelPosition { Val = C.TickLabelPositionValues.NextTo },
                    new C.CrossingAxis { Val = CategoryAxis },
                    new C.Crosses { Val = C.CrossesValues.AutoZero },
                    new C.CrossBetween { Val = C.CrossBetweenValues.Between }));
        }

        var chart = new C.Chart();
        if (!string.IsNullOrWhiteSpace(title))
        {
            chart.Append(new C.Title(
                new C.ChartText(new C.RichText(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text(title))))),
                new C.Overlay { Val = false }));
        }

        chart.Append(
            new C.AutoTitleDeleted { Val = string.IsNullOrWhiteSpace(title) },
            area,
            new C.Legend(new C.LegendPosition { Val = C.LegendPositionValues.Bottom }, new C.Overlay { Val = false }),
            new C.PlotVisibleOnly { Val = true });
        return new C.ChartSpace(new C.RoundedCorners { Val = false }, chart);
    }

    /// <summary>Ряд по типу диаграммы; дети — в порядке схемы своего типа.</summary>
    private static OpenXmlCompositeElement Series(
        string type, uint index, string nameRef, string name, string? categoriesRef, List<string>? labels, string valuesRef, List<string> values)
    {
        var children = new List<OpenXmlElement>
        {
            new C.Index { Val = index },
            new C.Order { Val = index },
            new C.SeriesText(new C.StringReference(new C.Formula(nameRef), StringCache([name])))
        };
        if (type == ChartSpec.Bar)
        {
            children.Add(new C.InvertIfNegative { Val = false });
        }
        else if (type == ChartSpec.Line)
        {
            children.Add(new C.Marker(new C.Symbol { Val = C.MarkerStyleValues.Circle }));
        }

        if (categoriesRef is not null && labels is not null)
        {
            children.Add(new C.CategoryAxisData(new C.StringReference(new C.Formula(categoriesRef), StringCache(labels))));
        }

        var cache = new C.NumberingCache(new C.FormatCode("General"), new C.PointCount { Val = (uint)values.Count });
        for (var i = 0; i < values.Count; i++)
        {
            if (TableQuery.TryNumber(values[i], out var number))
            {
                cache.Append(new C.NumericPoint(new C.NumericValue(number.ToString("R", CultureInfo.InvariantCulture))) { Index = (uint)i });
            }
        }

        children.Add(new C.Values(new C.NumberReference(new C.Formula(valuesRef), cache)));
        if (type == ChartSpec.Line)
        {
            children.Add(new C.Smooth { Val = false });
        }

        return type switch
        {
            ChartSpec.Pie => new C.PieChartSeries(children),
            ChartSpec.Line => new C.LineChartSeries(children),
            _ => new C.BarChartSeries(children)
        };
    }

    private static C.StringCache StringCache(IReadOnlyList<string> texts)
    {
        var cache = new C.StringCache(new C.PointCount { Val = (uint)texts.Count });
        for (var i = 0; i < texts.Count; i++)
        {
            cache.Append(new C.StringPoint(new C.NumericValue(texts[i])) { Index = (uint)i });
        }

        return cache;
    }
}
