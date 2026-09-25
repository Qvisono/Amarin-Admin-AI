using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Главное окно — обычное окно приложения, а не «окно-инструмент».
/// </summary>
/// <remarks>
/// С <c>WindowStyle="ToolWindow"</c> у окна стоял <c>WS_EX_TOOLWINDOW</c>: Windows не
/// показывала его в Alt+Tab, кнопка на панели задач появлялась лишь после первого фокуса, а
/// сворачивание чужой программы уносило окно следом. Глазами в тесте этого не увидеть, поэтому
/// проверяются сами флаги окна.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class MainWindowStyleTests
{
    private readonly WpfFixture _wpf;

    public MainWindowStyleTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_main_window_is_an_application_window_not_a_tool_window()
    {
        var (exStyle, style) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var handle = new WindowInteropHelper(window).EnsureHandle();
            return (GetWindowLong(handle, GwlExStyle), GetWindowLong(handle, GwlStyle));
        });

        Assert.True((exStyle & WsExToolWindow) == 0, "WS_EX_TOOLWINDOW: окна нет в Alt+Tab и на панели задач");

        // Без кнопки свёртывания в стиле окно не сворачивается щелчком по своей кнопке на панели
        // задач и сочетанием Win+Down — Windows считает, что сворачивать его нельзя.
        Assert.True((style & WsMinimizeBox) != 0, "WS_MINIMIZEBOX не выставлен");
    }

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsMinimizeBox = 0x00020000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);
}
