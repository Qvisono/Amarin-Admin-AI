using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The toast is supposed to stay on screen for its full dwell time. These measure how long it
/// actually survives, so "it flashes and vanishes" becomes a number instead of a guess.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ToastLifetimeTests
{
    private readonly WpfFixture _wpf;

    public ToastLifetimeTests(WpfFixture wpf) => _wpf = wpf;

    private NotificationToast ShowToast() => _wpf.Ui.Invoke(() => NotificationToast.Show(
        "grok-4-6", "Готово", "Grok 4.6 · 3 с", uiScalePercent: 100,
        ownerHandle: IntPtr.Zero, onActivated: null));

    private (bool Loaded, double Opacity, double Left, double Top, double Width, double Height) Probe(
        NotificationToast toast) =>
        _wpf.Ui.Invoke(() =>
        {
            var card = (FrameworkElement)((Grid)toast.Content).Children[0];
            return (toast.IsLoaded, card.Opacity, toast.Left, toast.Top, toast.ActualWidth, toast.ActualHeight);
        });

    [Fact]
    public void Toast_is_still_fully_visible_after_two_seconds()
    {
        var toast = ShowToast();
        try
        {
            Thread.Sleep(2000);
            var state = Probe(toast);

            Assert.True(state.Opacity > 0.99,
                $"card faded to {state.Opacity} after 2s - something dismissed it early");
            Assert.True(state.Width > 1 && state.Height > 1, $"toast has no size: {state.Width}x{state.Height}");
        }
        finally
        {
            _wpf.Ui.Invoke(() => { toast.Close(); return 0; });
        }
    }

    [Fact]
    public void Toast_lands_inside_the_work_area()
    {
        var toast = ShowToast();
        try
        {
            Thread.Sleep(400);
            var state = Probe(toast);
            var area = _wpf.Ui.Invoke(() => SystemParameters.WorkArea);

            // Off the bottom or the right edge looks exactly like "it disappeared".
            Assert.InRange(state.Left, area.Left - 1, area.Right - state.Width + 1);
            Assert.InRange(state.Top, area.Top - 1, area.Bottom - state.Height + 1);
        }
        finally
        {
            _wpf.Ui.Invoke(() => { toast.Close(); return 0; });
        }
    }

    [Fact]
    public void Toast_is_in_the_topmost_band()
    {
        // Without this the card paints once and the foreground app draws over it — which is
        // indistinguishable from the toast flashing and vanishing.
        var toast = ShowToast();
        try
        {
            Thread.Sleep(400);
            var style = _wpf.Ui.Invoke(() =>
                GetWindowLong(new WindowInteropHelper(toast).Handle, GwlExStyle));

            Assert.True((style & WsExTopmost) != 0, "WS_EX_TOPMOST is not set");
            Assert.True((style & WsExNoActivate) != 0, "WS_EX_NOACTIVATE is not set");
            Assert.True((style & WsExToolWindow) != 0, "WS_EX_TOOLWINDOW is not set");
        }
        finally
        {
            _wpf.Ui.Invoke(() => { toast.Close(); return 0; });
        }
    }

    [Fact]
    public void Toast_outlives_the_old_five_second_dwell()
    {
        // It used to disappear at 5s, which was easy to miss in the corner of a large screen.
        var toast = ShowToast();
        try
        {
            Thread.Sleep(6500);
            var state = Probe(toast);
            Assert.True(state.Opacity > 0.99, $"card faded to {state.Opacity} after 6.5s");
        }
        finally
        {
            _wpf.Ui.Invoke(() => { toast.Close(); return 0; });
        }
    }

    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopmost = 0x00000008;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [Fact]
    public void Toast_survives_its_own_fade_in()
    {
        var toast = ShowToast();
        try
        {
            // The entrance animation is 0.24s; opacity must be 1 once it settles, not 0.
            Thread.Sleep(600);
            var state = Probe(toast);
            Assert.True(state.Opacity > 0.99, $"opacity settled at {state.Opacity}");
        }
        finally
        {
            _wpf.Ui.Invoke(() => { toast.Close(); return 0; });
        }
    }
}
