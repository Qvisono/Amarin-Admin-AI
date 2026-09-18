using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// График трат по дням: заливка под линией, сама линия и точки.
/// </summary>
/// <remarks>
/// Свой контрол, а не разметка с фигурами, как у плашки остатка и кольца контекста: там форма
/// постоянная, а здесь на «Год» приходит 365 точек. Как элементы разметки это 365 фигур плюс
/// столько же подписей внутри панели настроек высотой 460 — один проход
/// <see cref="DrawingContext"/> дешевле на порядок.
/// <para>
/// Рисунок разложен на два визуала. В первом — сетка, подписи, заливка, линия и точки; он
/// пересобирается только при смене ряда, размера или темы. Во втором — волосок и подсвеченная
/// точка под курсором. Врозь именно затем, чтобы движение мыши не перерисовывало серию.
/// </para>
/// <para>
/// Цвета берутся из палитры через <c>TryFindResource</c> и перечитываются по
/// <see cref="ThemeManager.EffectiveThemeChanged"/>: кисть, присвоенная один раз, осталась бы
/// от прежней темы. Подписка снимается на <c>Unloaded</c> — страница настроек открывается
/// и закрывается сколько угодно раз, и без этого каждый заход оставлял бы за собой живой
/// контрол.
/// </para>
/// </remarks>
internal sealed class SpendChart : FrameworkElement
{
    private const double LeftGutter = 44;
    private const double RightGutter = 10;
    private const double TopGutter = 10;
    private const double BottomGutter = 20;

    /// <summary>Дальше кружки сливаются в сплошную полосу и только мешают читать линию.</summary>
    private const int MaxDots = 90;

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _series = new();
    private readonly DrawingVisual _hover = new();

    private IReadOnlyList<SpendPoint> _points = [];
    private string _emptyNote = "";
    private DateFormat _dateFormat = DateFormat.DayMonthShort;
    private int _hoverIndex = -1;

    private Brush _accent = Brushes.DodgerBlue;
    private Brush _grid = Brushes.Gray;
    private Brush _label = Brushes.Gray;
    private Brush _surface = Brushes.Black;

    /// <summary>Подложка плашки у точки. Плотная: под ней лежит линия графика.</summary>
    private Brush _callout = Brushes.Black;

    private Brush _amountBrush = Brushes.White;
    private Typeface _typeface = new("Segoe UI");

    public SpendChart()
    {
        _visuals = new VisualCollection(this) { _series, _hover };
        ClipToBounds = true;

        Loaded += (_, _) =>
        {
            ThemeManager.EffectiveThemeChanged -= OnThemeChanged;
            ThemeManager.EffectiveThemeChanged += OnThemeChanged;
            ReadBrushes();
            Redraw();
        };
        Unloaded += (_, _) => ThemeManager.EffectiveThemeChanged -= OnThemeChanged;

        MouseMove += OnMouseMoved;
        MouseLeave += (_, _) => SetHover(-1);
    }

    /// <summary>Что показать на наведении. Хозяин страницы сам решает, где это нарисовать.</summary>
    public event Action<SpendPoint?>? PointHovered;

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    /// <param name="emptyNote">
    /// Строка поверх пустого поля: нет ключа, нет данных, сеть не ответила. Пустое место без
    /// объяснения человек читает как поломку.
    /// </param>
    public void SetSeries(IReadOnlyList<SpendPoint> points, string emptyNote, DateFormat dateFormat)
    {
        _points = points ?? [];
        _emptyNote = emptyNote ?? "";
        _dateFormat = dateFormat;
        _hoverIndex = -1;
        ReadBrushes();
        Redraw();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Redraw();
    }

    /// <summary>
    /// Мышь попадает в график по всей его площади.
    /// </summary>
    /// <remarks>
    /// Рисунок лежит в дочерних <see cref="DrawingVisual"/>, а сам элемент своего содержимого
    /// не имеет — и без этой замены WPF считал бы попаданием только нарисованные кружки
    /// диаметром в пять пикселей. Прозрачный прямоугольник в <c>OnRender</c> тут не годится:
    /// он появляется только после прохода отрисовки, а попадание спрашивают и раньше.
    /// </remarks>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        ArgumentNullException.ThrowIfNull(hitTestParameters);
        return new Rect(RenderSize).Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;
    }

    private void OnThemeChanged()
    {
        ReadBrushes();
        Redraw();
    }

    private void ReadBrushes()
    {
        _accent = Resource("Accent.Fill", Brushes.DodgerBlue);
        _grid = Resource("Border.Subtle", Brushes.Gray);
        _label = Resource("Text.Faint", Brushes.Gray);
        _surface = Resource("Bg.Panel", Brushes.Black);
        _callout = Resource("Bg.Elevated", Resource("Bg.Panel", Brushes.Black));
        _amountBrush = Resource("Text.Bright", Brushes.White);

        // Шрифт наследуется от страницы: человек выбирает гарнитуру в оформлении, и подписи
        // осей обязаны меняться вместе с остальным текстом. У FrameworkElement своего свойства
        // нет — берём вложенное, то самое, которым WPF и раздаёт шрифт вниз по дереву.
        _typeface = new Typeface(
            TextElement.GetFontFamily(this) ?? SystemFonts.MessageFontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);
    }

    private Brush Resource(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    private void OnMouseMoved(object sender, MouseEventArgs e)
    {
        if (_points.Count == 0)
        {
            return;
        }

        // Попадание по ближайшему X, а не по кругу точки: на плотном графике в кружок
        // диаметром пять пикселей человек не попадёт никогда.
        var plot = PlotWidth;
        var step = _points.Count > 1 ? plot / (_points.Count - 1) : 0;
        var x = e.GetPosition(this).X - LeftGutter;
        var index = step > 0
            ? (int)Math.Round(x / step, MidpointRounding.AwayFromZero)
            : 0;
        SetHover(Math.Clamp(index, 0, _points.Count - 1));
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex)
        {
            return;
        }

        _hoverIndex = index;
        DrawHover();
        PointHovered?.Invoke(index >= 0 && index < _points.Count ? _points[index] : null);
    }

    /// <summary>
    /// Ставит подсветку на точку без мыши — чтобы плашку было видно на снимке в тесте.
    /// </summary>
    internal void HoverForShot(int index) => SetHover(Math.Clamp(index, -1, _points.Count - 1));

    private double PlotWidth => Math.Max(1, ActualWidth - LeftGutter - RightGutter);

    private double PlotHeight => Math.Max(1, ActualHeight - TopGutter - BottomGutter);

    /// <summary>
    /// Потолок оси — округлённый вверх до «круглого» числа.
    /// </summary>
    /// <remarks>
    /// Раньше потолком служил сам максимум ряда, и деления подписывались его третями: на графике
    /// стояло «$0.6667» и «$0.3333». Так не подписывают ни один график — шкала обязана состоять
    /// из чисел, которые человек сложит в уме.
    /// <para>
    /// Все нули дали бы деление на ноль, а одна точка — вырожденный масштаб, на котором линия
    /// легла бы ровно по верхней кромке; отсюда запасная единица.
    /// </para>
    /// </remarks>
    private decimal Ceiling() => NiceCeiling(_points.Count == 0 ? 0m : _points.Max(point => point.Usd));

    /// <summary>Ближайшее сверху число вида 1, 2, 2.5 или 5, умноженное на степень десяти.</summary>
    internal static decimal NiceCeiling(decimal max)
    {
        if (max <= 0)
        {
            return 1m;
        }

        // Шаг делится на три деления сетки, поэтому ряд подобран так, чтобы трети оставались
        // читаемыми: 3 → 1, 1.5, 2.25… а 1.5 и 3 дают ровно 0.5 и 1.
        var power = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)max)));
        foreach (var step in new[] { 1m, 1.5m, 3m, 6m, 10m })
        {
            if (max <= step * power)
            {
                return step * power;
            }
        }

        return 10m * power;
    }

    private double XOf(int index) =>
        _points.Count > 1
            ? LeftGutter + PlotWidth * index / (_points.Count - 1)
            : LeftGutter + PlotWidth / 2;

    private double YOf(decimal value) =>
        TopGutter + PlotHeight * (1 - (double)(value / Ceiling()));

    private void Redraw()
    {
        using var context = _series.RenderOpen();
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        if (_points.Count == 0 || _points.All(point => point.Usd == 0))
        {
            DrawGrid(context, withScale: false);
            DrawNote(context, _emptyNote);
            DrawHover();
            return;
        }

        DrawGrid(context, withScale: true);
        DrawSeries(context);
        DrawHover();
    }

    /// <param name="withScale">
    /// Подписывать ли деления. На пустом отрезке цифры не нужны: сетка там — просто фон под
    /// объяснением, а «$1 / $0.67 / $0.33» рядом со словами «трат не было» только сбивает.
    /// </param>
    private void DrawGrid(DrawingContext context, bool withScale)
    {
        var pen = new Pen(_grid, 1);
        pen.Freeze();
        var ceiling = Ceiling();

        for (var line = 0; line <= 3; line++)
        {
            var value = ceiling * line / 3m;
            var y = Math.Round(YOf(value)) + 0.5;
            context.DrawLine(pen, new Point(LeftGutter, y), new Point(ActualWidth - RightGutter, y));
            if (withScale)
            {
                context.DrawText(Text(FormatAxis(value), 9.5, _label), new Point(4, y - 7));
            }
        }

        if (!withScale)
        {
            return;
        }

        if (_points.Count == 0)
        {
            return;
        }

        // Подписи оси прореживаются до пяти: за год их 365, и они слиплись бы в полосу.
        var stride = Math.Max(1, (int)Math.Ceiling(_points.Count / 5.0));
        for (var i = 0; i < _points.Count; i += stride)
        {
            var text = Text(ChatFormat.Date(_points[i].Date, _dateFormat), 9.5, _label);
            var x = Math.Clamp(XOf(i) - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            context.DrawText(text, new Point(x, ActualHeight - BottomGutter + 4));
        }
    }

    private void DrawSeries(DrawingContext context)
    {
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var bottom = TopGutter + PlotHeight;

        using (var lineContext = line.Open())
        using (var areaContext = area.Open())
        {
            var start = new Point(XOf(0), YOf(_points[0].Usd));
            lineContext.BeginFigure(start, isFilled: false, isClosed: false);
            areaContext.BeginFigure(new Point(start.X, bottom), isFilled: true, isClosed: true);
            areaContext.LineTo(start, isStroked: false, isSmoothJoin: false);

            for (var i = 1; i < _points.Count; i++)
            {
                var previous = new Point(XOf(i - 1), YOf(_points[i - 1].Usd));
                var current = new Point(XOf(i), YOf(_points[i].Usd));

                // Сглаживание горизонтальными касательными: обычный сплайн между двумя близкими
                // точками даёт выброс ниже нуля, а отрицательных трат не бывает.
                var handle = (current.X - previous.X) / 2;
                var first = new Point(previous.X + handle, previous.Y);
                var second = new Point(current.X - handle, current.Y);
                lineContext.BezierTo(first, second, current, isStroked: true, isSmoothJoin: true);
                areaContext.BezierTo(first, second, current, isStroked: false, isSmoothJoin: true);
            }

            areaContext.LineTo(new Point(XOf(_points.Count - 1), bottom), isStroked: false, isSmoothJoin: false);
        }

        line.Freeze();
        area.Freeze();

        var fill = new LinearGradientBrush(
            Colour(_accent, 0x50),
            Colour(_accent, 0x00),
            new Point(0, 0),
            new Point(0, 1));
        fill.Freeze();
        context.DrawGeometry(fill, null, area);

        var stroke = new Pen(_accent, 1.6)
        {
            LineJoin = PenLineJoin.Round,
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        stroke.Freeze();
        context.DrawGeometry(null, stroke, line);

        var dotStride = Math.Max(1, (int)Math.Ceiling((double)_points.Count / MaxDots));
        var dotEdge = new Pen(_surface, 1.5);
        dotEdge.Freeze();
        for (var i = 0; i < _points.Count; i += dotStride)
        {
            context.DrawEllipse(_accent, dotEdge, new Point(XOf(i), YOf(_points[i].Usd)), 2.6, 2.6);
        }
    }

    private void DrawHover()
    {
        using var context = _hover.RenderOpen();
        if (_hoverIndex < 0 || _hoverIndex >= _points.Count || ActualWidth <= 1)
        {
            return;
        }

        var point = _points[_hoverIndex];
        var x = XOf(_hoverIndex);
        var y = YOf(point.Usd);

        var hair = new Pen(_grid, 1) { DashStyle = new DashStyle([3, 3], 0) };
        hair.Freeze();
        context.DrawLine(hair, new Point(x, TopGutter), new Point(x, TopGutter + PlotHeight));

        var edge = new Pen(_surface, 2);
        edge.Freeze();
        context.DrawEllipse(_accent, edge, new Point(x, y), 4.5, 4.5);

        DrawCallout(context, point, x, y);
    }

    /// <summary>
    /// Плашка с датой и суммой рядом с точкой под курсором.
    /// </summary>
    /// <remarks>
    /// Рисуется прямо в графике, а не всплывающей подсказкой. Страница настроек лежит внутри
    /// <c>Viewbox</c>, а <c>ToolTip</c> и <c>Popup</c> уезжают в отдельное окно и его масштаб
    /// не наследуют — плашка оказалась бы другого размера, чем график под ней. Заодно не нужны
    /// ни <c>PopupManager</c>, ни правила жизни подсказки.
    /// <para>
    /// Сторона выбирается по месту: у правого края плашка переезжает влево от точки, иначе
    /// она вылезала бы за карточку и обрезалась. По высоте она прижимается к полю графика
    /// по той же причине.
    /// </para>
    /// </remarks>
    private void DrawCallout(DrawingContext context, SpendPoint point, double x, double y)
    {
        const double Pad = 8;
        const double Gap = 12;

        var date = Text(ChatFormat.Date(point.Date, _dateFormat), 10, _label);
        var amount = Text(SpendReport.FormatUsd(point.Usd), 12.5, _amountBrush);
        amount.SetFontWeight(FontWeights.SemiBold);

        var card = PlaceCallout(
            x,
            y,
            Math.Max(date.Width, amount.Width) + Pad * 2,
            date.Height + amount.Height + Pad * 2 + 2,
            ActualWidth,
            ActualHeight,
            Gap);

        var border = new Pen(_grid, 1);
        border.Freeze();
        context.DrawRoundedRectangle(_callout, border, card, 6, 6);

        context.DrawText(date, new Point(card.Left + Pad, card.Top + Pad - 1));
        context.DrawText(amount, new Point(card.Left + Pad, card.Top + Pad + date.Height + 1));
    }

    /// <summary>
    /// Куда положить плашку, чтобы она не вылезла за график.
    /// </summary>
    /// <remarks>
    /// По умолчанию справа от точки. У правого края переезжает влево — иначе у последней точки
    /// месяца плашку срезало бы краем карточки ровно тогда, когда на неё и смотрят. Отдельной
    /// чистой функцией, потому что проверить переворот на снимке нельзя.
    /// </remarks>
    internal static Rect PlaceCallout(
        double x,
        double y,
        double width,
        double height,
        double hostWidth,
        double hostHeight,
        double gap)
    {
        var left = x + gap;
        if (left + width > hostWidth - 2)
        {
            left = x - gap - width;
        }

        return new Rect(
            Math.Clamp(left, 2, Math.Max(2, hostWidth - width - 2)),
            Math.Clamp(y - height / 2, 2, Math.Max(2, hostHeight - height - 2)),
            width,
            height);
    }

    private void DrawNote(DrawingContext context, string note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return;
        }

        var text = Text(note, 11.5, _label);
        context.DrawText(
            text,
            new Point(
                Math.Max(0, (ActualWidth - text.Width) / 2),
                Math.Max(0, (ActualHeight - text.Height) / 2)));
    }

    private FormattedText Text(string value, double size, Brush brush) =>
        new(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>
    /// Подпись оси в долларах. Четыре знака у мелких сумм: типичный день стоит сотые доли
    /// цента, и «$0.00» на всех делениях не сказало бы ничего.
    /// </summary>
    internal static string FormatAxis(decimal value) =>
        value >= 1m ? value.ToString("$0.##", CultureInfo.InvariantCulture)
        : value > 0 ? value.ToString("$0.####", CultureInfo.InvariantCulture).TrimEnd('0')
        : "$0";

    private static Color Colour(Brush brush, byte alpha)
    {
        var colour = brush is SolidColorBrush solid ? solid.Color : Colors.DodgerBlue;
        return Color.FromArgb(alpha, colour.R, colour.G, colour.B);
    }
}
