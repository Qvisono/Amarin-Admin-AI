using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Amarin.Core;
using Amarin.UI;
using Path = System.Windows.Shapes.Path;
using ToggleButton = System.Windows.Controls.Primitives.ToggleButton;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Спидометр силы размышления. Прежний рисунок водил стрелку в пределах ±72° по полукругу —
/// треть шкалы, до краёв она не доходила никогда, — а сам рисунок был прижат к низу бокса.
/// Эти тесты держат геометрию: развёртку, попадание стрелки в бокс и то, что уровень видно.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ReasoningGaugeTests
{
    private readonly WpfFixture _wpf;

    public ReasoningGaugeTests(WpfFixture wpf) => _wpf = wpf;

    private const double IconBox = 20;

    private static VeniceModelInfo Model() => new()
    {
        Id = "claude-sonnet-5",
        ModelSpec = new VeniceModelSpec
        {
            Capabilities = new VeniceModelCapabilities
            {
                SupportsReasoning = true,
                SupportsReasoningEffort = true,
                SupportsReasoningEffortWithTools = true,
                ReasoningEffortOptions = ["none", "low", "medium", "high", "max"],
                DefaultReasoningEffort = "medium"
            }
        }
    };

    private ReasoningPicker Build(bool disableThinking, string? effort) => _wpf.Ui.Invoke(() =>
    {
        var picker = new ReasoningPicker { ShowGaugeIcon = true };
        picker.SetUsesTools(true);
        picker.SetCatalog([Model()]);
        picker.SetModel("claude-sonnet-5");
        picker.SetChoice(disableThinking, effort);
        picker.Measure(new Size(240, 40));
        picker.Arrange(new Rect(0, 0, 240, 40));
        picker.UpdateLayout();
        return picker;
    });

    private T Part<T>(ReasoningPicker picker, string name) where T : class =>
        _wpf.Ui.Invoke(() =>
        {
            var button = (ToggleButton)picker.FindName("OpenButton")!;
            button.ApplyTemplate();
            return (T)button.Template.FindName(name, button)!;
        });

    private double Angle(ReasoningPicker picker) =>
        _wpf.Ui.Invoke(() => Part<RotateTransform>(picker, "GaugeNeedleRotate").Angle);

    [Fact]
    public void The_needle_sweeps_the_whole_dial()
    {
        // Края шкалы: -110° и +110°. Прежний рисунок упирался в ±72 и оставлял дугу пустой.
        Assert.Equal(-110, Angle(Build(disableThinking: true, effort: null)), 1);
        Assert.Equal(110, Angle(Build(disableThinking: false, effort: "max")), 1);

        var low = Angle(Build(disableThinking: false, effort: "low"));
        var high = Angle(Build(disableThinking: false, effort: "high"));
        Assert.True(low < high, $"low ({low}°) должен стоять левее high ({high}°)");
        Assert.True(low > -110 && high < 110, $"промежуточные уровни уехали на края: {low}°, {high}°");
    }

    [Fact]
    public void The_needle_tip_stays_inside_the_icon_box()
    {
        // Стрелка длиной 4.6 от точки (10,12) при радиусе шкалы 6.5 не должна ни вылезать за
        // бокс, ни упираться в дугу — на этом прежний рисунок и выглядел кривым.
        foreach (var effort in new[] { "low", "medium", "high", "max" })
        {
            var angle = Angle(Build(disableThinking: false, effort: effort)) * Math.PI / 180;
            var x = 10 + (4.6 * Math.Sin(angle));
            var y = 12 - (4.6 * Math.Cos(angle));

            Assert.InRange(x, 1, IconBox - 1);
            Assert.InRange(y, 1, IconBox - 1);
        }
    }

    [Fact]
    public void The_level_arc_is_hidden_when_thinking_is_off()
    {
        var off = Part<Path>(Build(disableThinking: true, effort: null), "GaugeProgress");
        var on = Part<Path>(Build(disableThinking: false, effort: "high"), "GaugeProgress");

        Assert.Equal(Visibility.Collapsed, _wpf.Ui.Invoke(() => off.Visibility));
        Assert.Equal(Visibility.Visible, _wpf.Ui.Invoke(() => on.Visibility));
        Assert.NotNull(_wpf.Ui.Invoke(() => on.Data));
    }

    [Fact]
    public void Both_arcs_share_one_start_point()
    {
        // Дуга уровня обязана лечь на фоновую, а не рядом с ней.
        var picker = Build(disableThinking: false, effort: "medium");
        var (track, progress) = _wpf.Ui.Invoke(() =>
        {
            var t = (PathGeometry)Part<Path>(picker, "GaugeTrack").Data;
            var p = (PathGeometry)Part<Path>(picker, "GaugeProgress").Data;
            return (t.Figures[0].StartPoint, p.Figures[0].StartPoint);
        });

        Assert.Equal(track.X, progress.X, 3);
        Assert.Equal(track.Y, progress.Y, 3);
    }

    [Fact]
    public void Low_and_max_do_not_look_the_same()
    {
        var low = Snapshot(Build(disableThinking: false, effort: "low"));
        var max = Snapshot(Build(disableThinking: false, effort: "max"));

        Assert.False(low.SequenceEqual(max), "иконка не меняется при смене уровня");
    }

    [Fact]
    public void The_dial_shows_the_level_in_the_accent_colour()
    {
        var accent = _wpf.Ui.Invoke(() =>
            ((SolidColorBrush)Application.Current.FindResource("Accent.Fill")).Color);

        var on = CountAccent(Snapshot(Build(disableThinking: false, effort: "max")), accent);
        var off = CountAccent(Snapshot(Build(disableThinking: true, effort: null)), accent);

        Assert.True(on > 10, $"дуга уровня не нарисована: акцентных пикселей {on}");
        Assert.Equal(0, off);
    }

    private byte[] Snapshot(ReasoningPicker picker) => _wpf.Ui.Invoke(() =>
    {
        var icon = Part<FrameworkElement>(picker, "GaugeIcon");
        var bitmap = new RenderTargetBitmap((int)IconBox * 4, (int)IconBox * 4, 384, 384, PixelFormats.Pbgra32);
        bitmap.Render(icon);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    });

    /// <summary>Считает пиксели акцентного цвета, прощая сглаживание по краям штриха.</summary>
    private static int CountAccent(byte[] pixels, Color accent)
    {
        var hits = 0;
        for (var i = 0; i + 3 < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 200)
            {
                continue;
            }

            if (Math.Abs(pixels[i + 2] - accent.R) <= 12 &&
                Math.Abs(pixels[i + 1] - accent.G) <= 12 &&
                Math.Abs(pixels[i] - accent.B) <= 12)
            {
                hits++;
            }
        }

        return hits;
    }
}
