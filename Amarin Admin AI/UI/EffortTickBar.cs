using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>Акцентная дорожка и точки для дискретного ползунка в меню моделей.</summary>
internal sealed class EffortTickBar : TickBar
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(EffortTickBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActiveDotBrushProperty = DependencyProperty.Register(
        nameof(ActiveDotBrush), typeof(Brush), typeof(EffortTickBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InactiveDotBrushProperty = DependencyProperty.Register(
        nameof(InactiveDotBrush), typeof(Brush), typeof(EffortTickBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? ActiveDotBrush
    {
        get => (Brush?)GetValue(ActiveDotBrushProperty);
        set => SetValue(ActiveDotBrushProperty, value);
    }

    public Brush? InactiveDotBrush
    {
        get => (Brush?)GetValue(InactiveDotBrushProperty);
        set => SetValue(InactiveDotBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var range = Maximum - Minimum;
        var travel = ActualWidth - ReservedSpace;
        if (range <= 0 || travel <= 0)
        {
            return;
        }

        const double trackRadius = 7;
        const double dotRadius = 2;
        var inset = ReservedSpace / 2;
        var centerY = ActualHeight / 2;
        var step = travel / range;
        var selected = Math.Clamp(Value, Minimum, Maximum);
        var fillWidth = inset + (selected - Minimum) * step;
        drawingContext.DrawRoundedRectangle(Fill, null,
            new Rect(0, centerY - trackRadius, fillWidth, 2 * trackRadius), trackRadius, trackRadius);

        // Положения совпадают с центром бегунка: Track оставляет по полширины бегунка с каждой стороны.
        for (var level = Minimum; level <= Maximum; level++)
        {
            var brush = level <= selected ? ActiveDotBrush : InactiveDotBrush;
            var center = new Point(inset + (level - Minimum) * step, centerY);
            drawingContext.DrawEllipse(brush, null, center, dotRadius, dotRadius);
        }
    }
}
