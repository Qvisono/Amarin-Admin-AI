using System.Windows;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Pan/zoom constraints shared by the avatar crop dialog and the image viewer. Pure maths — no
/// UI thread needed.
/// </summary>
public sealed class PanZoomTests
{
    /// <summary>Crop: 300x300 window at (30,30) over a 1200x800 photo.</summary>
    private static PanZoom Crop() => new(30, 30, 300, 300, 1200, 800, cover: true);

    /// <summary>View: 800x600 stage over a 1600x1200 picture.</summary>
    private static PanZoom View() => new(0, 0, 800, 600, 1600, 1200, cover: false);

    [Fact]
    public void Cover_starts_at_the_smallest_scale_that_fills_the_frame()
    {
        var state = Crop();

        // 300/800 on the short side is what it takes to leave no gap.
        Assert.Equal(0.375, state.FitScale, 4);
        Assert.Equal(state.FitScale, state.Scale, 4);
    }

    [Fact]
    public void Cover_refuses_to_uncover_the_frame_in_either_direction()
    {
        var state = Crop();

        state.SetOffset(9999, 9999);
        Assert.True(state.OffsetX <= 30.0001, $"left edge slipped inside the frame: {state.OffsetX}");
        Assert.True(state.OffsetY <= 30.0001, $"top edge slipped inside the frame: {state.OffsetY}");

        state.SetOffset(-9999, -9999);
        var right = state.OffsetX + (1200 * state.Scale);
        var bottom = state.OffsetY + (800 * state.Scale);
        Assert.True(right >= 329.9999, $"right edge slipped inside the frame: {right}");
        Assert.True(bottom >= 329.9999, $"bottom edge slipped inside the frame: {bottom}");
    }

    [Fact]
    public void Zoom_keeps_the_pixel_under_the_cursor_in_place()
    {
        var state = View();
        var anchor = new Point(500, 320);
        var before = ((anchor.X - state.OffsetX) / state.Scale, (anchor.Y - state.OffsetY) / state.Scale);

        state.ZoomByWheel(600, anchor);

        var after = ((anchor.X - state.OffsetX) / state.Scale, (anchor.Y - state.OffsetY) / state.Scale);
        Assert.Equal(before.Item1, after.Item1, 1);
        Assert.Equal(before.Item2, after.Item2, 1);
        Assert.True(state.Scale > state.FitScale, "wheel up did not zoom in");
    }

    [Fact]
    public void Zoom_is_clamped_at_both_ends()
    {
        var state = View();

        state.ZoomTo(1000, new Point(400, 300));
        Assert.Equal(state.MaxScale, state.Scale, 4);

        state.ZoomTo(0.00001, new Point(400, 300));
        Assert.Equal(state.MinScale, state.Scale, 4);
    }

    [Fact]
    public void A_picture_smaller_than_the_stage_stays_centred()
    {
        // 200x100 inside an 800x600 stage: dragging must not slide it around the empty space.
        var state = new PanZoom(0, 0, 800, 600, 200, 100, cover: false);

        state.PanBy(250, 250);

        Assert.Equal((800 - (200 * state.Scale)) / 2, state.OffsetX, 3);
        Assert.Equal((600 - (100 * state.Scale)) / 2, state.OffsetY, 3);
    }

    [Fact]
    public void Viewing_never_magnifies_a_small_picture_to_fill_the_window()
    {
        var state = new PanZoom(0, 0, 800, 600, 200, 100, cover: false);
        Assert.Equal(1, state.FitScale, 4);
    }

    [Fact]
    public void Slider_round_trips_the_scale()
    {
        var state = View();
        state.ZoomTo(state.FitScale * 3, new Point(400, 300));

        var recovered = state.ScaleForSlider(state.SliderValue);
        Assert.Equal(state.Scale, recovered, 4);
    }

    [Fact]
    public void Reset_returns_to_the_starting_frame()
    {
        var state = View();
        var scale = state.Scale;
        var x = state.OffsetX;

        state.ZoomByWheel(900, new Point(100, 100));
        state.PanBy(-200, -120);
        state.Reset();

        Assert.Equal(scale, state.Scale, 4);
        Assert.Equal(x, state.OffsetX, 3);
    }
}
