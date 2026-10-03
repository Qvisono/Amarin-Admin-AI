using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;

namespace Amarin.UI;

/// <summary>
/// Заполненность окна контекста модели — кольцом рядом с плашкой остатка.
/// </summary>
/// <remarks>
/// <para>
/// Устроено как <see cref="BalanceBadge"/>: фигуры задаёт разметка, класс их только наполняет —
/// плашка обязана совпадать с соседней до пикселя.
/// </para>
/// <para>
/// В отличие от остатка, кольцо меняет цвет: о кончающемся контексте человек сам не знает —
/// модель просто тихо забывает начало разговора, и когда это видно в ответах, уже поздно.
/// </para>
/// </remarks>
internal sealed class ContextRing
{
    /// <summary>Отсюда жёлтое: ещё хватает, но перед длинной задачей это стоит знать.</summary>
    private const double WarnFraction = 0.80;

    /// <summary>Отсюда красное: старые сообщения вот-вот начнут выпадать.</summary>
    private const double CriticalFraction = 0.95;

    // Кольцо стоит в квадрате 18×18; радиус оставляет место, чтобы обводка в 2 точки не вылезала
    // за него на концах.
    private const double CentreX = 9;
    private const double CentreY = 9;
    private const double Radius = 6.5;

    private readonly Border _plate;
    private readonly Path _track;
    private readonly Path _progress;
    private readonly TextBlock _amount;

    private ContextUsage _usage = ContextUsage.Unknown;

    public ContextRing(Border plate, Path track, Path progress, TextBlock amount)
    {
        _plate = plate;
        _track = track;
        _progress = progress;
        _amount = amount;

        _track.Data = BuildArc(0, 359.999);

        // Кисти — из сменных словарей, а цвет дуги зависит от показания, поэтому он ставится на
        // каждой отрисовке, а не привязывается раз и навсегда.
        ThemeManager.EffectiveThemeChanged += Render;
        Render();
    }

    /// <summary>
    /// После каждого хода и при смене чата или модели. Неизвестное показание — пустое кольцо с
    /// прочерком, а не пропавшая плашка: панель не меняет вид, а «ещё не измерено» — тоже ответ.
    /// </summary>
    public void Show(ContextUsage usage)
    {
        _usage = usage;
        Render();
    }

    private void Render()
    {
        Paint();

        if (!_usage.HasScale)
        {
            _amount.Text = "-";
            _progress.Visibility = Visibility.Collapsed;
            _plate.ToolTip = Loc.Get("S.Context.Unknown");
            return;
        }

        _amount.Text = VeniceModelCatalog.FormatContext(_usage.Used) + " / " +
                       VeniceModelCatalog.FormatContext(_usage.Max);
        _plate.ToolTip = BuildTooltip(_usage);

        // У нуля дуга вырождается в точку, где ArcSegment не определён, а меньше градуса её и
        // так не видно — не рисуем.
        var sweep = _usage.Fraction * 360;
        if (sweep < 1)
        {
            _progress.Visibility = Visibility.Collapsed;
            return;
        }

        _progress.Visibility = Visibility.Visible;
        _progress.Data = BuildArc(0, Math.Min(sweep, 359.999));
    }

    private void Paint()
    {
        _track.SetResourceReference(Shape.StrokeProperty, "Text.Dim");
        _progress.SetResourceReference(Shape.StrokeProperty, ProgressBrushKey());

        var accent = _plate.TryFindResource("Text.Muted") as Brush ?? Frozen(Colors.Gray);
        _amount.Foreground = accent;

        // Та же подложка, что у плашки остатка: пара читается одним рядом показаний.
        var colour = accent is SolidColorBrush { Color: var value } ? value : Colors.Gray;
        _plate.Background = Frozen(Color.FromArgb(0x24, colour.R, colour.G, colour.B));
        _plate.BorderBrush = Frozen(Color.FromArgb(0x40, colour.R, colour.G, colour.B));
    }

    private string ProgressBrushKey() => _usage.Fraction switch
    {
        >= CriticalFraction => "Status.Danger",
        >= WarnFraction => "Status.Warning",
        _ => "Accent.Fill"
    };

    private static string BuildTooltip(ContextUsage usage)
    {
        var percent = (usage.Fraction * 100).ToString("0", CultureInfo.InvariantCulture);
        var text = Loc.Format("S.Context.Title", usage.Used.ToString("#,0", CultureInfo.InvariantCulture)
            .Replace(",", " "), VeniceModelCatalog.FormatContext(usage.Max), percent);

        if (usage.IsFloor)
        {
            text += "\n" + Loc.Get("S.Context.Auto");
        }

        if (usage.IsEstimate)
        {
            text += "\n" + Loc.Get("S.Context.Estimate");
        }

        if (usage.Fraction >= CriticalFraction)
        {
            text += "\n" + Loc.Get("S.Context.Full");
        }
        else if (usage.Fraction >= WarnFraction)
        {
            text += "\n" + Loc.Get("S.Context.Filling");
        }

        return text;
    }

    /// <summary>Дуга от двенадцати часов по часовой стрелке, в градусах.</summary>
    internal static Geometry BuildArc(double fromAngle, double toAngle)
    {
        var figure = new PathFigure { StartPoint = PointOnRing(fromAngle), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnRing(toAngle),
            Size = new Size(Radius, Radius),
            IsLargeArc = Math.Abs(toAngle - fromAngle) > 180,
            SweepDirection = SweepDirection.Clockwise
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnRing(double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180;
        return new Point(
            CentreX + (Radius * Math.Sin(radians)),
            CentreY - (Radius * Math.Cos(radians)));
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
