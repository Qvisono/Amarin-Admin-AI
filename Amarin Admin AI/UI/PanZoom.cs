using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Drag-to-pan and wheel-to-zoom over a fixed viewport, shared by the avatar crop dialog and
/// the image viewer so the two behave identically rather than drifting apart.
/// <para>
/// Coordinates: a source point <c>p</c> lands on the viewport at <c>p * Scale + Offset</c>.
/// The viewport is the rectangle <c>(originX, originY)</c> to <c>+ (viewWidth, viewHeight)</c>.
/// </para>
/// </summary>
internal sealed class PanZoom
{
    private readonly double _originX;
    private readonly double _originY;
    private readonly double _viewWidth;
    private readonly double _viewHeight;
    private readonly double _sourceWidth;
    private readonly double _sourceHeight;

    /// <param name="cover">
    /// true: the source must always cover the viewport (cropping — the avatar dialog).
    /// false: the source may be smaller than the viewport and is then centred (viewing).
    /// </param>
    public PanZoom(
        double originX,
        double originY,
        double viewWidth,
        double viewHeight,
        double sourceWidth,
        double sourceHeight,
        bool cover,
        double maxZoomFactor = 8)
    {
        _originX = originX;
        _originY = originY;
        _viewWidth = viewWidth;
        _viewHeight = viewHeight;
        _sourceWidth = Math.Max(1, sourceWidth);
        _sourceHeight = Math.Max(1, sourceHeight);
        Cover = cover;

        var byWidth = viewWidth / _sourceWidth;
        var byHeight = viewHeight / _sourceHeight;

        // Cover: the larger ratio, so neither axis leaves a gap. Contain: the smaller, so the
        // whole picture fits — and never magnify a small image just to fill the window.
        FitScale = cover ? Math.Max(byWidth, byHeight) : Math.Min(1, Math.Min(byWidth, byHeight));
        MinScale = cover ? FitScale : Math.Min(FitScale, byWidth < byHeight ? byWidth : byHeight);
        MaxScale = FitScale * maxZoomFactor;
        Scale = FitScale;
        Center();
    }

    public bool Cover { get; }

    /// <summary>Scale at which the picture first fits (or first covers) the viewport.</summary>
    public double FitScale { get; }

    public double MinScale { get; }

    public double MaxScale { get; }

    public double Scale { get; private set; }

    public double OffsetX { get; private set; }

    public double OffsetY { get; private set; }

    /// <summary>Puts the picture in the middle of the viewport at the current scale.</summary>
    public void Center()
    {
        SetOffset(
            _originX + ((_viewWidth - (_sourceWidth * Scale)) / 2),
            _originY + ((_viewHeight - (_sourceHeight * Scale)) / 2));
    }

    public void Reset()
    {
        Scale = FitScale;
        Center();
    }

    /// <summary>Moves the picture, refusing any offset the constraints do not allow.</summary>
    public void SetOffset(double x, double y)
    {
        OffsetX = ClampAxis(x, _originX, _viewWidth, _sourceWidth);
        OffsetY = ClampAxis(y, _originY, _viewHeight, _sourceHeight);
    }

    public void PanBy(double dx, double dy) => SetOffset(OffsetX + dx, OffsetY + dy);

    /// <summary>Zooms to <paramref name="scale"/>, keeping the source pixel under the anchor put.</summary>
    public void ZoomTo(double scale, Point anchor)
    {
        var clamped = Math.Clamp(scale, MinScale, MaxScale);
        if (Math.Abs(clamped - Scale) < 0.0000001)
        {
            return;
        }

        var sourceX = (anchor.X - OffsetX) / Scale;
        var sourceY = (anchor.Y - OffsetY) / Scale;

        Scale = clamped;
        SetOffset(anchor.X - (sourceX * Scale), anchor.Y - (sourceY * Scale));
    }

    /// <summary>Wheel step. A geometric factor so each notch feels the same at any zoom.</summary>
    public void ZoomByWheel(int delta, Point anchor) => ZoomTo(Scale * Math.Pow(1.0015, delta), anchor);

    /// <summary>Position on the 0..1 slider track that matches the current scale.</summary>
    public double SliderValue => MaxScale <= MinScale
        ? 0
        : Math.Log(Scale / MinScale) / Math.Log(MaxScale / MinScale);

    public double ScaleForSlider(double value) =>
        MinScale * Math.Pow(MaxScale / MinScale, Math.Clamp(value, 0, 1));

    /// <summary>
    /// One axis of the pan constraint. When the painted picture is larger than the viewport it
    /// may not be dragged past either edge; when it is smaller it stays centred rather than
    /// sliding around in the empty space.
    /// </summary>
    private double ClampAxis(double offset, double origin, double viewLength, double sourceLength)
    {
        var painted = sourceLength * Scale;
        var min = origin + viewLength - painted;
        var max = origin;

        if (min >= max)
        {
            // Smaller than the viewport. Cropping requires coverage, so the degenerate case
            // pins it flush; viewing simply centres it.
            return Cover ? min : origin + ((viewLength - painted) / 2);
        }

        return Math.Clamp(offset, min, max);
    }
}
