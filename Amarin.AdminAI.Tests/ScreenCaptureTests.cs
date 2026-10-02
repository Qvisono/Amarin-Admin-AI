using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Окно программы видно снимкам экрана. Жаловались, что при открытой программе скриншоты не
/// делаются; запрет снимка Windows умеет ставить только этими двумя способами, и оба выключены.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ScreenCaptureTests
{
    private const uint WdaNone = 0;
    private const int DwmwaCloaked = 14;

    private readonly WpfFixture _wpf;

    public ScreenCaptureTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_main_window_is_neither_excluded_from_capture_nor_cloaked()
    {
        var (affinityRead, affinity, cloaked) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow
            {
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.Show();
            try
            {
                var handle = new WindowInteropHelper(window).Handle;
                var read = GetWindowDisplayAffinity(handle, out var value);
                _ = DwmGetWindowAttribute(handle, DwmwaCloaked, out var cloak, sizeof(int));
                return (read, value, cloak);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(affinityRead);
        Assert.Equal(WdaNone, affinity);
        Assert.Equal(0, cloaked);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
