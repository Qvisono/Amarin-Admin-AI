using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Диаграмма в ответе чата — блок <c>```chart</c> (<see cref="ChartSpec"/>): столбцы, линия или
/// круг в цветах темы, с подписью значения под курсором.
/// </summary>
/// <remarks>
/// <para>
/// Рисуется одним проходом <see cref="DrawingContext"/>, как <see cref="SpendChart"/>: точек до
/// двухсот, и фигурами разметки это были бы сотни элементов в каждом сообщении ленты.
/// </para>
/// <para>
/// Цвета рядов — из уже существующих ключей палитры (акцент и цвета подсветки кода): они подобраны
/// под фон каждой темы, а у монохромных тем акцент серый, и ряды, выведенные из одного акцента,
/// были бы неразличимы. Новых ключей палитры не заводится — их пришлось бы дописать в полсотни тем.
/// Кисти читаются при каждой отрисовке, поэтому смена темы — это просто перерисовка.
/// </para>
/// </remarks>
internal sealed class ChatChartView : FrameworkElement
{
    private const double PlotHeight = 170;
    private const double PieSize = 170;
    private const double TitleHeight = 22;
    private const double AxisGap = 6;
    private const double BottomGutter = 18;
    private const double LegendRow = 18;

    private static readonly string[] SeriesKeys =
        ["Accent.Fill", "Code.Keyword", "Code.String", "Code.Number", "Code.Control", "Code.Type", "Status.Warning", "Status.Danger"];

    private readonly ChartSpec _spec;
    private int _hover = -1;

    private ChatChartView(ChartSpec spec)
    {
        _spec = spec;
        ClipToBounds = true;
        Cursor = Cursors.Arrow;
        MouseMove += (_, e) => SetHover(HitIndex(e.GetPosition(this)));
        MouseLeave += (_, _) => SetHover(-1);
        Loaded += (_, _) =>
        {
            ThemeManager.EffectiveThemeChanged -= InvalidateVisual;
            ThemeManager.EffectiveThemeChanged += InvalidateVisual;
        };
        Unloaded += (_, _) => ThemeManager.EffectiveThemeChanged -= InvalidateVisual;
    }

    /// <summary>Карточка с диаграммой — того же вида, что блок кода.</summary>
    public static FrameworkElement Create(ChartSpec spec)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 2, 0, 8),
            Child = new ChatChartView(spec)
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bg.Sidebar");
        card.SetResourceReference(Border.BorderBrushProperty, "Border.Subtle");
        return card;
    }

    internal int Hover => _hover;

    /// <summary>Подсветка без мыши — для проверки и снимков в тестах.</summary>
    internal void HoverForShot(int index) => SetHover(index);

    private bool IsPie => _spec.Type == ChartSpec.Pie;

    private bool HasTitle => !string.IsNullOrWhiteSpace(_spec.Title);

    private bool HasLegend => IsPie || _spec.Series.Count > 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        var height = (HasTitle ? TitleHeight : 0) + (IsPie
            ? Math.Max(PieSize, _spec.Labels.Count * LegendRow)
            : PlotHeight + BottomGutter + (HasLegend ? LegendRow + 4 : 0));
        return new Size(width, Math.Min(height, IsPie ? PieSize + 260 : height));
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        ArgumentNullException.ThrowIfNull(hitTestParameters);
        return new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
    }

    private void SetHover(int index)
    {
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        if (ActualWidth < 40 || ActualHeight < 40)
        {
            return;
        }

        var top = 0.0;
        if (HasTitle)
        {
            var title = Text(_spec.Title ?? "", 12.5, Brush("Text.Bright"), ActualWidth);
            title.SetFontWeight(FontWeights.SemiBold);
            title.MaxLineCount = 1;
            title.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(title, new Point(0, 0));
            top = TitleHeight;
        }

        if (IsPie)
        {
            DrawPie(context, top);
        }
        else
        {
            DrawPlot(context, top);
        }
    }

    private (double Floor, double Ceiling) Scale()
    {
        var values = _spec.Series.SelectMany(series => series.Values).OfType<double>().ToList();
        var max = values.Count == 0 ? 0 : values.Max();
        var min = values.Count == 0 ? 0 : values.Min();
        var ceiling = max > 0 ? (double)SpendChart.NiceCeiling((decimal)max) : 0;
        var floor = min < 0 ? -(double)SpendChart.NiceCeiling((decimal)-min) : 0;
        return ceiling - floor <= 0 ? (0, 1) : (floor, ceiling);
    }

    private Rect PlotArea(double top)
    {
        var (floor, ceiling) = Scale();
        var gutter = Enumerable.Range(0, 4).Select(line => Text(Axis(floor + (ceiling - floor) * line / 3), 9.5, Brushes.Gray, 200).Width).Max() + AxisGap;
        return new Rect(gutter, top + 6, Math.Max(10, ActualWidth - gutter - 4), PlotHeight - 6);
    }

    private void DrawPlot(DrawingContext context, double top)
    {
        var plot = PlotArea(top);
        var (floor, ceiling) = Scale();
        double YOf(double value) => plot.Bottom - plot.Height * (value - floor) / (ceiling - floor);

        var grid = new Pen(Brush("Border.Subtle"), 1);
        grid.Freeze();
        var label = Brush("Text.Faint");
        for (var line = 0; line <= 3; line++)
        {
            var value = floor + (ceiling - floor) * line / 3;
            var y = Math.Round(YOf(value)) + 0.5;
            context.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var text = Text(Axis(value), 9.5, label, 200);
            context.DrawText(text, new Point(plot.Left - AxisGap - text.Width, y - text.Height / 2));
        }

        var count = _spec.Labels.Count;
        var slot = plot.Width / count;
        if (_hover >= 0)
        {
            context.DrawRectangle(Brush("Bg.Hover"), null, new Rect(plot.Left + slot * _hover, plot.Top, slot, plot.Height));
        }

        // Подписи оси прореживаются так, чтобы не налезали друг на друга.
        var widest = _spec.Labels.Select(item => Text(item, 9.5, label, 400).Width).DefaultIfEmpty(0).Max() + 8;
        var stride = Math.Max(1, (int)Math.Ceiling(widest / Math.Max(1, slot)));
        for (var i = 0; i < count; i += stride)
        {
            var text = Text(_spec.Labels[i], 9.5, label, Math.Max(20, slot * stride - 4));
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            var x = Math.Clamp(plot.Left + slot * (i + 0.5) - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            context.DrawText(text, new Point(x, plot.Bottom + 3));
        }

        var zero = YOf(Math.Clamp(0, floor, ceiling));
        if (_spec.Type == ChartSpec.Bar)
        {
            var groups = _spec.Series.Count;
            // Столбец не шире 36 точек: на трёх подписях во всю ширину сообщения они выходили плитами.
            var width = Math.Max(1, Math.Min(36, slot * 0.66 / groups));
            var offset = (slot - width * groups) / 2;
            for (var s = 0; s < groups; s++)
            {
                var fill = SeriesBrush(s);
                for (var i = 0; i < count; i++)
                {
                    if (_spec.Series[s].Values[i] is not { } value)
                    {
                        continue;
                    }

                    var x = plot.Left + slot * i + offset + width * s;
                    var y = YOf(value);
                    var rect = new Rect(x, Math.Min(y, zero), Math.Max(1, width - 1), Math.Max(1, Math.Abs(zero - y)));
                    context.DrawRoundedRectangle(fill, null, rect, Math.Min(2, rect.Width / 2), Math.Min(2, rect.Width / 2));
                }
            }
        }
        else
        {
            for (var s = 0; s < _spec.Series.Count; s++)
            {
                var stroke = new Pen(SeriesBrush(s), 1.8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                stroke.Freeze();
                Point? previous = null;
                for (var i = 0; i < count; i++)
                {
                    if (_spec.Series[s].Values[i] is not { } value)
                    {
                        previous = null;
                        continue;
                    }

                    var point = new Point(plot.Left + slot * (i + 0.5), YOf(value));
                    if (previous is { } from)
                    {
                        context.DrawLine(stroke, from, point);
                    }

                    if (count <= 60 || i == _hover)
                    {
                        context.DrawEllipse(SeriesBrush(s), null, point, i == _hover ? 3.6 : 2.4, i == _hover ? 3.6 : 2.4);
                    }

                    previous = point;
                }
            }
        }

        if (HasLegend)
        {
            DrawLegendRow(context, plot.Bottom + BottomGutter + 4);
        }

        if (_hover >= 0)
        {
            DrawCallout(context, plot.Left + slot * (_hover + 0.5), plot.Top + 4, _spec.Labels[_hover],
                [.. _spec.Series.Select((series, s) => (s, Name: series.Name, Value: series.Values[_hover]))]);
        }
    }

    private void DrawPie(DrawingContext context, double top)
    {
        var values = _spec.Series[0].Values.Select(value => Math.Max(0, value ?? 0)).ToList();
        var total = values.Sum();
        var radius = PieSize / 2 - 4;
        var center = new Point(radius + 4, top + radius + 4);
        if (total <= 0)
        {
            return;
        }

        var start = -90.0;
        for (var i = 0; i < values.Count; i++)
        {
            var sweep = values[i] / total * 360;
            if (sweep <= 0)
            {
                continue;
            }

            var grow = i == _hover ? 4 : 0;
            context.DrawGeometry(SeriesBrush(i), new Pen(Brush("Bg.Sidebar"), 1.5), Slice(center, radius + grow, start, sweep));
            start += sweep;
        }

        // Легенда справа: цвет, подпись и доля.
        var left = center.X + radius + 18;
        var label = Brush("Text.Secondary");
        for (var i = 0; i < _spec.Labels.Count; i++)
        {
            var y = top + 4 + i * LegendRow;
            if (y + LegendRow > ActualHeight)
            {
                break;
            }

            context.DrawRoundedRectangle(SeriesBrush(i), null, new Rect(left, y + 4, 9, 9), 2, 2);
            var share = (values[i] / total).ToString("P0", CultureInfo.CurrentCulture);
            var text = Text(_spec.Labels[i] + "  " + share, 11, i == _hover ? Brush("Text.Bright") : label, Math.Max(20, ActualWidth - left - 16));
            text.MaxLineCount = 1;
            text.Trimming = TextTrimming.CharacterEllipsis;
            context.DrawText(text, new Point(left + 15, y + 1));
        }

        if (_hover >= 0 && _hover < values.Count)
        {
            DrawCallout(context, center.X, top + 8, _spec.Labels[_hover], [(_hover, _spec.Series[0].Name, _spec.Series[0].Values[_hover])]);
        }
    }

    private static StreamGeometry Slice(Point center, double radius, double start, double sweep)
    {
        static Point On(Point center, double radius, double degrees)
        {
            var angle = degrees * Math.PI / 180;
            return new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
        }

        var geometry = new StreamGeometry();
        using (var draw = geometry.Open())
        {
            if (sweep >= 359.99)
            {
                draw.BeginFigure(On(center, radius, 0), isFilled: true, isClosed: true);
                draw.ArcTo(On(center, radius, 180), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
                draw.ArcTo(On(center, radius, 360), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                draw.BeginFigure(center, isFilled: true, isClosed: true);
                draw.LineTo(On(center, radius, start), true, false);
                draw.ArcTo(On(center, radius, start + sweep), new Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawLegendRow(DrawingContext context, double y)
    {
        var x = 0.0;
        var label = Brush("Text.Secondary");
        for (var s = 0; s < _spec.Series.Count; s++)
        {
            var text = Text(_spec.Series[s].Name.Length > 0 ? _spec.Series[s].Name : (s + 1).ToString(CultureInfo.CurrentCulture), 11, label, 240);
            if (x + text.Width + 16 > ActualWidth)
            {
                break;
            }

            context.DrawRoundedRectangle(SeriesBrush(s), null, new Rect(x, y + 4, 9, 9), 2, 2);
            context.DrawText(text, new Point(x + 14, y + 1));
            x += text.Width + 30;
        }
    }

    /// <summary>
    /// Плашка с подписью и значениями точки под курсором — в самой диаграмме, как у
    /// <see cref="SpendChart"/>: всплывающая подсказка уехала бы в своё окно и не знала бы о лупе.
    /// </summary>
    private void DrawCallout(DrawingContext context, double x, double y, string title, IReadOnlyList<(int Index, string Name, double? Value)> rows)
    {
        const double Pad = 8;
        var head = Text(title, 10.5, Brush("Text.Faint"), 260);
        var lines = rows.Select(row => (row.Index, Text(
            (rows.Count > 1 || IsPie ? (row.Name.Length > 0 ? row.Name + ": " : "") : "") + (row.Value is { } value ? Number(value) : "-"),
            12, Brush("Text.Bright"), 260))).ToList();
        var width = Math.Max(head.Width, lines.Max(line => line.Item2.Width + (rows.Count > 1 ? 14 : 0))) + Pad * 2;
        var height = head.Height + lines.Sum(line => line.Item2.Height + 2) + Pad * 2;
        var left = x + 12 + width > ActualWidth ? x - 12 - width : x + 12;
        var card = new Rect(Math.Max(0, left), Math.Min(y, Math.Max(0, ActualHeight - height)), width, height);

        var border = new Pen(Brush("Border.Subtle"), 1);
        border.Freeze();
        context.DrawRoundedRectangle(Brush("Bg.Elevated"), border, card, 6, 6);
        context.DrawText(head, new Point(card.Left + Pad, card.Top + Pad - 1));
        var lineTop = card.Top + Pad + head.Height + 1;
        foreach (var (index, text) in lines)
        {
            var textLeft = card.Left + Pad;
            if (rows.Count > 1)
            {
                context.DrawRoundedRectangle(SeriesBrush(index), null, new Rect(textLeft, lineTop + 4, 8, 8), 2, 2);
                textLeft += 14;
            }

            text.SetFontWeight(FontWeights.SemiBold);
            context.DrawText(text, new Point(textLeft, lineTop));
            lineTop += text.Height + 2;
        }
    }

    /// <summary>Индекс подписи под точкой: у столбцов и линии — по ширине слота, у круга — по углу.</summary>
    internal int HitIndex(Point point)
    {
        if (_spec.Labels.Count == 0)
        {
            return -1;
        }

        var top = HasTitle ? TitleHeight : 0;
        if (IsPie)
        {
            var radius = PieSize / 2 - 4;
            var center = new Point(radius + 4, top + radius + 4);
            var dx = point.X - center.X;
            var dy = point.Y - center.Y;
            if (dx * dx + dy * dy > radius * radius)
            {
                return -1;
            }

            var angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
            var values = _spec.Series[0].Values.Select(value => Math.Max(0, value ?? 0)).ToList();
            var total = values.Sum();
            var start = 0.0;
            for (var i = 0; i < values.Count && total > 0; i++)
            {
                start += values[i] / total * 360;
                if (angle < start)
                {
                    return i;
                }
            }

            return -1;
        }

        var plot = PlotArea(top);
        if (point.X < plot.Left || point.X > plot.Right || point.Y > plot.Bottom + BottomGutter)
        {
            return -1;
        }

        return Math.Clamp((int)((point.X - plot.Left) / (plot.Width / _spec.Labels.Count)), 0, _spec.Labels.Count - 1);
    }

    private Brush SeriesBrush(int index) => Brush(SeriesKeys[index % SeriesKeys.Length]);

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string text, double size, Brush brush, double maxWidth)
    {
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(TextElement.GetFontFamily(this) ?? SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, maxWidth)
        };
        return formatted;
    }

    private static string Axis(double value) => Number(Math.Round(value, 6));

    /// <summary>Число для человека: с разделителем разрядов и не больше двух знаков после запятой.</summary>
    private static string Number(double value) => value.ToString("#,0.##", CultureInfo.CurrentCulture);
}
