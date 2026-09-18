using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Amarin.UI;

/// <summary>
/// Lets the user pick which part of a picture becomes the avatar, the way Discord and the rest
/// do it: drag to pan, wheel or slider to zoom, and a fixed square marking what is kept. The
/// image is never allowed to uncover the square, so the result is always fully painted.
/// </summary>
public partial class AvatarCropWindow : Window
{
    /// <summary>Side of the stage the photo is laid out on, in device-independent units.</summary>
    private const double StageSize = 360;

    /// <summary>Side of the kept square. The rest of the stage is only context.</summary>
    private const double CropSize = 300;

    /// <summary>Offset of the kept square inside the stage.</summary>
    private const double CropOrigin = (StageSize - CropSize) / 2;

    /// <summary>How far past "just covers the square" the user may zoom in.</summary>
    private const double MaxZoomFactor = 8;

    private readonly BitmapSource _image;
    private readonly double _minScale;
    private double _scale;
    private Point _dragStart;
    private Point _panStart;
    private bool _dragging;
    private bool _sliderEcho;

    private AvatarCropWindow(BitmapSource image)
    {
        InitializeComponent();
        _image = image;

        // Force one WPF unit per source pixel so the transform maths below is in image
        // coordinates; a bitmap whose metadata claims something other than 96 DPI would
        // otherwise be laid out at a different size than it is stored.
        Source.Source = image;
        Source.Width = image.PixelWidth;
        Source.Height = image.PixelHeight;

        // "Cover": the smallest zoom at which the square is still fully painted.
        _minScale = Math.Max(CropSize / image.PixelWidth, CropSize / image.PixelHeight);
        _scale = _minScale;

        ApplyScale(_scale);
        // Start centred on the middle of the picture — the usual intent.
        SetPan(
            CropOrigin + (CropSize - (image.PixelWidth * _scale)) / 2,
            CropOrigin + (CropSize - (image.PixelHeight * _scale)) / 2);
        SyncSlider();
    }

    /// <summary>
    /// The region the user chose, in source pixels; null when the dialog was cancelled.
    /// </summary>
    public Int32Rect? Selection { get; private set; }

    /// <summary>
    /// Asks the user to frame <paramref name="image"/>. Returns the chosen square in source
    /// pixels, or null if they cancelled.
    /// </summary>
    public static Int32Rect? Choose(BitmapSource image, Window owner)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(owner);

        var window = new AvatarCropWindow(image) { Owner = owner };
        return window.ShowDialog() == true ? window.Selection : null;
    }

    // ───────────────────────── Panning ─────────────────────────

    private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Stage);
        _panStart = new Point(Pan.X, Pan.Y);
        _dragging = Stage.CaptureMouse();
        e.Handled = true;
    }

    private void Stage_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var now = e.GetPosition(Stage);
        SetPan(_panStart.X + (now.X - _dragStart.X), _panStart.Y + (now.Y - _dragStart.Y));
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Stage.ReleaseMouseCapture();
    }

    // ───────────────────────── Zooming ─────────────────────────

    private void Stage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Zoom about the pointer, so the detail under the cursor stays put.
        ZoomTo(_scale * Math.Pow(1.0015, e.Delta), e.GetPosition(Stage));
        e.Handled = true;
    }

    private void ZoomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sliderEcho)
        {
            return;
        }

        // Geometric, so each step of the slider feels the same size at either end of the range.
        ZoomTo(
            _minScale * Math.Pow(MaxZoomFactor, e.NewValue),
            new Point(StageSize / 2, StageSize / 2));
    }

    private void ZoomTo(double scale, Point anchor)
    {
        var clamped = Math.Clamp(scale, _minScale, _minScale * MaxZoomFactor);
        if (Math.Abs(clamped - _scale) < 0.0000001)
        {
            return;
        }

        // Keep the source pixel currently under the anchor under it afterwards.
        var sourceX = (anchor.X - Pan.X) / _scale;
        var sourceY = (anchor.Y - Pan.Y) / _scale;

        _scale = clamped;
        ApplyScale(_scale);
        SetPan(anchor.X - (sourceX * _scale), anchor.Y - (sourceY * _scale));
        SyncSlider();
    }

    private void ApplyScale(double scale)
    {
        Zoom.ScaleX = scale;
        Zoom.ScaleY = scale;
    }

    private void SyncSlider()
    {
        _sliderEcho = true;
        ZoomSlider.Value = Math.Log(_scale / _minScale) / Math.Log(MaxZoomFactor);
        _sliderEcho = false;
    }

    /// <summary>Moves the picture, refusing any offset that would uncover the crop square.</summary>
    private void SetPan(double x, double y)
    {
        Pan.X = ClampAxis(x, _image.PixelWidth);
        Pan.Y = ClampAxis(y, _image.PixelHeight);
    }

    private double ClampAxis(double offset, int sourceLength)
    {
        var painted = sourceLength * _scale;
        // Leading edge no further right than the square's left edge, trailing edge no further
        // left than its right edge. At minimum zoom on the short axis these coincide.
        var min = CropOrigin + CropSize - painted;
        var max = CropOrigin;
        return min >= max ? min : Math.Clamp(offset, min, max);
    }

    // ───────────────────────── Result ─────────────────────────

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        Selection = CurrentSelection();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private Int32Rect CurrentSelection()
    {
        var side = (int)Math.Round(CropSize / _scale);
        var x = (int)Math.Round((CropOrigin - Pan.X) / _scale);
        var y = (int)Math.Round((CropOrigin - Pan.Y) / _scale);

        // Rounding can push the square a pixel past the edge; pull it back rather than handing
        // CroppedBitmap a rectangle it will reject.
        side = Math.Max(1, Math.Min(side, Math.Min(_image.PixelWidth, _image.PixelHeight)));
        x = Math.Clamp(x, 0, _image.PixelWidth - side);
        y = Math.Clamp(y, 0, _image.PixelHeight - side);
        return new Int32Rect(x, y, side, side);
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Borderless window: the card itself is the title bar. The stage handles its own
        // presses, so dragging the photo never drags the window.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
