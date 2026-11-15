using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Amarin.UI;

/// <summary>
/// Знак работающего агента: яркая точка обегает кольцо из восьми, а за ней тянется хвост
/// из гаснущих и уменьшающихся точек. Оборот занимает 1,1 секунды.
/// </summary>
public partial class AgentWorkingIndicator : UserControl
{
    private const int DotCount = 8;
    private const double Box = 16;
    private const double Orbit = 5.8;
    private const double DotSize = 3;
    private const double Dim = 0.2;
    private const double SmallScale = 0.55;

    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(1.1);

    private readonly Ellipse[] _dots = new Ellipse[DotCount];
    private readonly ScaleTransform[] _scales = new ScaleTransform[DotCount];

    public AgentWorkingIndicator()
    {
        InitializeComponent();
        BuildDots();
        ShowStill();
        Loaded += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }

    private void BuildDots()
    {
        for (var i = 0; i < DotCount; i++)
        {
            var angle = i * 2 * Math.PI / DotCount - Math.PI / 2;
            var scale = new ScaleTransform(1, 1, DotSize / 2, DotSize / 2);
            var dot = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                RenderTransform = scale
            };
            dot.SetResourceReference(Shape.FillProperty, "Accent.Fill");
            Canvas.SetLeft(dot, Box / 2 + Orbit * Math.Cos(angle) - DotSize / 2);
            Canvas.SetTop(dot, Box / 2 + Orbit * Math.Sin(angle) - DotSize / 2);
            Ring.Children.Add(dot);
            _dots[i] = dot;
            _scales[i] = scale;
        }
    }

    /// <summary>Кадр без движения (анимации выключены): та же голова с хвостом, а не ровное кольцо.</summary>
    private void ShowStill()
    {
        for (var i = 0; i < DotCount; i++)
        {
            var behind = (DotCount - i) % DotCount;
            var fade = Math.Min(1, behind / (DotCount - 1.5));
            _dots[i].Opacity = 1 - (1 - Dim) * fade;
            var size = 1 - (1 - SmallScale) * fade;
            _scales[i].ScaleX = size;
            _scales[i].ScaleY = size;
        }
    }

    private void UpdateAnimation()
    {
        Stop();
        if (!IsLoaded || !IsVisible || !UiMotion.Enabled)
        {
            return;
        }

        for (var i = 0; i < DotCount; i++)
        {
            // До своей очереди точка стоит приглушённой, а не в кадре «головы с хвостом».
            _dots[i].Opacity = Dim;
            _scales[i].ScaleX = SmallScale;
            _scales[i].ScaleY = SmallScale;
            var offset = TimeSpan.FromTicks(Turn.Ticks * i / DotCount);
            _dots[i].BeginAnimation(OpacityProperty, Pulse(1, Dim, offset));
            _scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, Pulse(1, SmallScale, offset));
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, Pulse(1, SmallScale, offset));
        }
    }

    private void Stop()
    {
        for (var i = 0; i < DotCount; i++)
        {
            _dots[i].BeginAnimation(OpacityProperty, null);
            _scales[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scales[i].BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        ShowStill();
    }

    /// <summary>Быстрая вспышка и плавное затухание; до своей очереди точка стоит приглушённой.</summary>
    private static DoubleAnimationUsingKeyFrames Pulse(double peak, double rest, TimeSpan offset) =>
        new()
        {
            Duration = new Duration(Turn),
            BeginTime = offset,
            RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(rest, KeyTime.FromPercent(0)),
                new LinearDoubleKeyFrame(peak, KeyTime.FromPercent(0.06)),
                new EasingDoubleKeyFrame(rest, KeyTime.FromPercent(0.85), new QuadraticEase { EasingMode = EasingMode.EaseOut }),
                new LinearDoubleKeyFrame(rest, KeyTime.FromPercent(1))
            }
        };
}
