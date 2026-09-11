using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Path = System.Windows.Shapes.Path;
using Shape = System.Windows.Shapes.Shape;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// How full the model's context window is, as a ring beside the balance plate.
/// </summary>
/// <remarks>
/// <para>
/// Built like <see cref="BalanceBadge"/>: the markup owns the shapes, this class only fills them.
/// A UserControl would have carried its own template into a toolbar that already hand-rolls every
/// other control, and the plate has to match the balance beside it pixel for pixel.
/// </para>
/// <para>
/// Unlike the balance, this one does change colour. Running out of context is not something the
/// user already knows — the model just quietly starts forgetting the beginning of the
/// conversation, and by the time that shows in the answers the damage is done.
/// </para>
/// </remarks>
internal sealed class ContextRing
{
    /// <summary>Amber from here: still fine, but worth knowing before starting a long task.</summary>
    private const double WarnFraction = 0.80;

    /// <summary>Red from here: the oldest messages are about to start falling out.</summary>
    private const double CriticalFraction = 0.95;

    // One set of constants for the markup and the arc. The ring sits in an 18x18 box; the radius
    // leaves room for the 2px stroke to stay inside it at both caps.
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

        // Brushes come from swapped dictionaries, and the progress colour depends on the reading,
        // so it is reassigned on every render rather than bound once.
        ThemeManager.EffectiveThemeChanged += Render;
        Render();
    }

    /// <summary>
    /// Called after every turn and whenever the open chat or model changes. An unknown reading
    /// leaves the ring empty with a dash rather than blanking the plate: the toolbar keeps its
    /// shape, and "not measured yet" is itself worth showing.
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
            _amount.Text = "—";
            _progress.Visibility = Visibility.Collapsed;
            _plate.ToolTip = Loc.Get("S.Context.Unknown");
            return;
        }

        _amount.Text = VeniceModelCatalog.FormatContext(_usage.Used) + " / " +
                       VeniceModelCatalog.FormatContext(_usage.Max);
        _plate.ToolTip = BuildTooltip(_usage);

        // At zero the arc degenerates to a point, where ArcSegment is undefined; below a degree
        // it is invisible anyway, so the sliver is simply not drawn.
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

        // Same wash as the balance plate beside it, so the pair reads as one row of readouts.
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

    /// <summary>Arc from twelve o'clock, clockwise, in degrees.</summary>
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
