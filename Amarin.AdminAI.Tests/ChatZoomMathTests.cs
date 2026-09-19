using System.Windows;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Арифметика лупы над лентой чата. Чистая — потока интерфейса не требует.
/// </summary>
/// <remarks>
/// Для решения о колесе это не удобство, а единственная возможность: <c>RaiseEvent</c> не ставит
/// <c>Keyboard.Modifiers</c>, и поднятое в тесте колесо никогда не выглядит нажатым с Ctrl.
/// Проверить условие можно только там, где оно живёт отдельной функцией.
/// </remarks>
public sealed class ChatZoomMathTests
{
    /// <summary>Лента 900x4000 в окне 900x600 — обычный длинный чат.</summary>
    private const double DocumentHeight = 4000;
    private const double ViewportWidth = 900;
    private const double ViewportHeight = 600;

    [Fact]
    public void The_wheel_never_shrinks_the_chat_below_its_normal_size()
    {
        var scale = 1.0;
        for (var i = 0; i < 20; i++)
        {
            scale = ChatZoomMath.StepScale(scale, -120);
        }

        Assert.Equal(1.0, scale, 6);
    }

    [Fact]
    public void The_wheel_stops_at_three_times()
    {
        var scale = 1.0;
        for (var i = 0; i < 40; i++)
        {
            scale = ChatZoomMath.StepScale(scale, 120);
        }

        Assert.Equal(ChatZoomHost.MaxScale, scale, 6);
    }

    [Fact]
    public void A_notch_feels_the_same_at_any_zoom()
    {
        // Геометрически, а не прибавкой: иначе у дальнего края шаг был бы мельче ближнего.
        var once = ChatZoomMath.StepScale(ChatZoomMath.StepScale(1.0, 120), 120);
        var twice = ChatZoomMath.StepScale(1.0, 240);

        Assert.Equal(twice, once, 6);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_cursor_in_place()
    {
        const double offsetY = 900;
        const double panX = -120;
        const double from = 1.4;
        const double to = 2.2;
        var cursor = new Point(410, 260);

        var documentY = ChatZoomMath.DocumentY(offsetY, cursor.Y, from);
        var documentX = ChatZoomMath.DocumentX(panX, cursor.X, from);

        var movedY = ChatZoomMath.OffsetForDocumentY(documentY, cursor.Y, to, DocumentHeight, ViewportHeight);
        var movedX = ChatZoomMath.PanForDocumentX(documentX, cursor.X, to, ViewportWidth);

        // Та же точка ленты обязана снова оказаться под тем же пикселем.
        Assert.Equal(documentY, ChatZoomMath.DocumentY(movedY, cursor.Y, to), 6);
        Assert.Equal(documentX, ChatZoomMath.DocumentX(movedX, cursor.X, to), 6);
    }

    [Fact]
    public void The_view_never_leaves_the_transcript_vertically()
    {
        // У самого верха приближение не должно уводить смещение в минус.
        var atTop = ChatZoomMath.OffsetForDocumentY(0, 0, 2.0, DocumentHeight, ViewportHeight);
        Assert.Equal(0, atTop, 6);

        // У самого низа — не дальше последней строки.
        var atBottom = ChatZoomMath.OffsetForDocumentY(
            DocumentHeight, ViewportHeight, 2.0, DocumentHeight, ViewportHeight);
        Assert.Equal(ChatZoomMath.MaxOffsetY(DocumentHeight, 2.0, ViewportHeight), atBottom, 6);
    }

    [Fact]
    public void A_transcript_shorter_than_the_window_has_nowhere_to_scroll()
    {
        Assert.Equal(0, ChatZoomMath.MaxOffsetY(300, 1.0, ViewportHeight), 6);
        Assert.Equal(0, ChatZoomMath.MaxOffsetY(300, 1.5, ViewportHeight), 6);
    }

    [Fact]
    public void Panning_cannot_show_anything_beside_the_zoomed_column()
    {
        Assert.Equal(0, ChatZoomMath.ClampPanX(9999, 2.0, ViewportWidth), 6);
        Assert.Equal(ViewportWidth * -1.0, ChatZoomMath.ClampPanX(-9999, 2.0, ViewportWidth), 6);
    }

    [Fact]
    public void An_unzoomed_transcript_does_not_slide_sideways()
    {
        Assert.Equal(0, ChatZoomMath.ClampPanX(250, 1.0, ViewportWidth), 6);
        Assert.Equal(0, ChatZoomMath.ClampPanX(-250, 1.0, ViewportWidth), 6);
    }

    [Fact]
    public void Zooming_runs_at_the_same_speed_on_any_monitor()
    {
        // Через Exp(-k*dt), а не долей за кадр: иначе на 144 Гц приближение ехало бы вдвое
        // быстрее, чем на 60, и «плавно» означало бы разное на разных машинах.
        var oneLongStep = ChatZoomMath.FollowScale(1.0, 3.0, 1.0 / 30.0);

        var twoShortSteps = ChatZoomMath.FollowScale(1.0, 3.0, 1.0 / 60.0);
        twoShortSteps = ChatZoomMath.FollowScale(twoShortSteps, 3.0, 1.0 / 60.0);

        Assert.Equal(oneLongStep, twoShortSteps, 6);
    }

    [Fact]
    public void A_dropped_frame_cannot_throw_the_transcript()
    {
        Assert.Equal(ChatZoomMath.MaxDt, ChatZoomMath.StepTime(3.0), 6);
        Assert.Equal(ChatZoomMath.MinDt, ChatZoomMath.StepTime(0), 6);
    }

    [Fact]
    public void Only_ctrl_over_the_transcript_claims_the_wheel()
    {
        var viewport = new Size(ViewportWidth, ViewportHeight);
        var inside = new Point(400, 300);

        Assert.True(ChatZoomMath.ClaimsWheel(true, false, true, inside, viewport));

        // Без Ctrl колесо остаётся обычной прокруткой чата.
        Assert.False(ChatZoomMath.ClaimsWheel(false, false, true, inside, viewport));

        // Поверх чата открыт оверлей — настройки или просмотр картинки со своим колесом.
        Assert.False(ChatZoomMath.ClaimsWheel(true, true, true, inside, viewport));

        // Выпадашка модели лежит над лентой, но живёт отдельным окном.
        Assert.False(ChatZoomMath.ClaimsWheel(true, false, false, inside, viewport));

        // Композер, шапка и колонка чатов — всё это вне прямоугольника ленты.
        Assert.False(ChatZoomMath.ClaimsWheel(true, false, true, new Point(400, 700), viewport));
        Assert.False(ChatZoomMath.ClaimsWheel(true, false, true, new Point(-20, 300), viewport));
    }
}
