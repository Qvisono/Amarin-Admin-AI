using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The toast used to vanish a frame after it appeared, and the existing lifetime tests could
/// not see it: they build a <see cref="NotificationToast"/> directly, so the window's own
/// <c>_toast</c> field stayed null and the code that dismissed it was never reached. These go
/// through <see cref="MainWindow.ShowCompletionToast"/> and then activate the window, which is
/// the path that actually broke.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ToastActivationTests
{
    private readonly WpfFixture _wpf;

    public ToastActivationTests(WpfFixture wpf) => _wpf = wpf;

    private static ChatDisplayMessage Completed() => new()
    {
        Role = "assistant",
        Id = "a1",
        Text = "Готово",
        CreatedAt = DateTime.Now,
        Duration = TimeSpan.FromSeconds(3),
        Status = AssistantStatus.Complete
    };

    private MainWindow Window() =>
        _wpf.Ui.Invoke(() => Application.Current.Windows.OfType<MainWindow>().Single());

    private static double Opacity(NotificationToast toast) =>
        ((FrameworkElement)((Grid)toast.Content).Children[0]).Opacity;

    [Fact]
    public void Activation_right_after_the_toast_appears_does_not_dismiss_it()
    {
        var window = Window();
        try
        {
            var state = _wpf.Ui.Invoke(() =>
            {
                window.ShowCompletionToast(Completed());

                // Exactly what SetBusy(false) -> FocusMessageInput() used to trigger: the window
                // activates in the same breath as the notification going up.
                window.OnWindowActivated();
                return new { Toast = window.CurrentToast };
            });

            Assert.NotNull(state.Toast);

            Thread.Sleep(800);
            var opacity = _wpf.Ui.Invoke(() => Opacity(state.Toast!));
            Assert.True(opacity > 0.99, $"card faded to {opacity} - the activation dismissed it");
            Assert.NotNull(_wpf.Ui.Invoke(() => window.CurrentToast));
        }
        finally
        {
            CloseToast(window);
        }
    }

    [Fact]
    public void Activation_after_the_grace_period_does_dismiss_it()
    {
        var window = Window();
        try
        {
            var toast = _wpf.Ui.Invoke(() =>
            {
                window.ShowCompletionToast(Completed());
                return window.CurrentToast;
            });
            Assert.NotNull(toast);

            // Past the grace window: this is a real "user came back to read it".
            Thread.Sleep(1200);
            _wpf.Ui.Invoke(() => { window.OnWindowActivated(); return 0; });

            Thread.Sleep(500);
            var opacity = _wpf.Ui.Invoke(() => Opacity(toast!));
            Assert.True(opacity < 0.5, $"card was still at {opacity} - returning to the app should clear it");
        }
        finally
        {
            CloseToast(window);
        }
    }

    private void CloseToast(MainWindow window) => _wpf.Ui.Invoke(() =>
    {
        window.CurrentToast?.Close();
        return 0;
    });
}
