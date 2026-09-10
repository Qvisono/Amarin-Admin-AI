using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// A colour field: a swatch that opens a popup with ready-made colours, H/S/V sliders and a hex
/// box. WPF ships no colour picker, and the Win32 common dialog is a modal grey box that would
/// look nothing like the rest of the settings page.
/// </summary>
public partial class ColorPickerField : UserControl
{
    /// <summary>
    /// A spread that stays usable as an accent in both light and dark palettes: nothing so pale
    /// it disappears on white, nothing so dark it disappears on the dark surfaces.
    /// </summary>
    private static readonly string[] Presets =
    [
        "#E5484D", "#E5533D", "#F76B15", "#FFB224", "#9BB82F", "#46A758",
        "#12A594", "#00A2C7", "#3E63DD", "#5B5BD6", "#8E4EC6", "#D6409F",
        "#F5F5F5", "#C9C9C9", "#8F8F8F", "#5A5A5A", "#2E2E2E", "#101010"
    ];

    private bool _updating;
    private string _hex = "";

    public ColorPickerField()
    {
        InitializeComponent();
        PopupManager.Register(PickerPopup, OpenButton);
        BuildSwatches();
        Render();
    }

    /// <summary>Selected colour as <c>#RRGGBB</c>. Empty means "not set — use the theme's own".</summary>
    public string Hex
    {
        get => _hex;
        set
        {
            var normalized = AppearanceSettings.NormalizeHex(value);
            if (normalized == _hex)
            {
                return;
            }

            _hex = normalized;
            Render();
        }
    }

    /// <summary>Raised on every change the user makes, including clearing to empty.</summary>
    public event EventHandler<string>? ColorChanged;

    /// <summary>Hides the "Сброс" button for fields where an empty value makes no sense.</summary>
    public bool AllowClear
    {
        get => ClearButton.Visibility == Visibility.Visible;
        set => ClearButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildSwatches()
    {
        foreach (var preset in Presets)
        {
            var color = AppearanceManager.Parse(preset) ?? Colors.Gray;
            var button = new Button
            {
                Width = 32,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                ToolTip = preset,
                Tag = preset,
                Template = SwatchTemplate(color)
            };
            button.Click += (_, _) => Commit(preset);
            SwatchList.Items.Add(button);
        }
    }

    private static ControlTemplate SwatchTemplate(Color color)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Freeze(color));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2)));
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Freeze(Colors.White)));
        template.Triggers.Add(hover);
        return template;
    }

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Commit(string hex)
    {
        var normalized = AppearanceSettings.NormalizeHex(hex);
        _hex = normalized;
        Render();
        ColorChanged?.Invoke(this, normalized);
    }

    /// <summary>Pushes the current value into every part of the control without re-raising events.</summary>
    private void Render()
    {
        if (_updating)
        {
            return;
        }

        _updating = true;
        try
        {
            OpenButton.ApplyTemplate();
            var fill = OpenButton.Template.FindName("SwatchFill", OpenButton) as Rectangle;
            var label = OpenButton.Template.FindName("SwatchLabel", OpenButton) as TextBlock;
            var empty = OpenButton.Template.FindName("EmptyMark", OpenButton) as System.Windows.Shapes.Path;

            var color = AppearanceManager.Parse(_hex);
            if (fill is not null)
            {
                fill.Fill = color is null ? Brushes.Transparent : Freeze(color.Value);
            }

            if (empty is not null)
            {
                empty.Visibility = color is null ? Visibility.Visible : Visibility.Collapsed;
            }

            if (label is not null)
            {
                label.Text = color is null ? "по теме" : _hex;
            }

            PreviewFill.Fill = color is null ? Brushes.Transparent : Freeze(color.Value);
            HexBox.Text = _hex;

            var (h, s, v) = ToHsv(color ?? Color.FromRgb(0x80, 0x80, 0x80));
            HueSlider.Value = h;
            SatSlider.Value = s * 100;
            ValSlider.Value = v * 100;
            PaintSliderTracks(h);
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>
    /// Fills each slider's groove with the range it actually spans, so the control reads as a
    /// colour picker rather than three anonymous sliders.
    /// </summary>
    private void PaintSliderTracks(double hue)
    {
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (var i = 0; i <= 6; i++)
        {
            rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6.0));
        }

        rainbow.Freeze();
        HueSlider.Background = rainbow;

        var saturation = new LinearGradientBrush(FromHsv(hue, 0, 1), FromHsv(hue, 1, 1), 0);
        saturation.Freeze();
        SatSlider.Background = saturation;

        var value = new LinearGradientBrush(Colors.Black, FromHsv(hue, 1, 1), 0);
        value.Freeze();
        ValSlider.Background = value;
    }

    private void Channel_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        var color = FromHsv(HueSlider.Value, SatSlider.Value / 100.0, ValSlider.Value / 100.0);
        Commit($"#{color.R:X2}{color.G:X2}{color.B:X2}");
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => CommitHexBox();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        CommitHexBox();
        e.Handled = true;
    }

    private void CommitHexBox()
    {
        var typed = AppearanceSettings.NormalizeHex(HexBox.Text);
        if (typed.Length == 0)
        {
            // Unparseable: put the real value back rather than silently clearing the colour.
            HexBox.Text = _hex;
            return;
        }

        Commit(typed);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => Commit("");

    // ───────────────────────── HSV ─────────────────────────

    internal static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    internal static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);

        var c = value * saturation;
        var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        var m = value - c;

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
