using System.Windows;

namespace Amarin.UI;

/// <summary>Окно встроенного браузера агента (1.37.0). Страницей управляет <see cref="Browser.WebViewBrowser"/>.</summary>
public partial class AgentBrowserWindow : Window
{
    private TaskCompletionSource<bool>? _handoff;

    public AgentBrowserWindow()
    {
        InitializeComponent();
    }

    internal void ShowAddress(string? url) => AddressText.Text = url ?? "";

    /// <summary>
    /// Полоса с просьбой к человеку. Окно выходит вперёд: шаг теперь его, а не агента. Ответ —
    /// нажатие «Готово»; срок и отмену ведёт вызывающий.
    /// </summary>
    internal Task<bool> AskHuman(string message)
    {
        _handoff?.TrySetResult(false);
        _handoff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        HandoffText.Text = message;
        HandoffBar.Visibility = Visibility.Visible;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        _ = Activate();
        return _handoff.Task;
    }

    internal void EndHandoff()
    {
        HandoffBar.Visibility = Visibility.Collapsed;
        _handoff?.TrySetResult(false);
        _handoff = null;
    }

    private void HandoffDone_Click(object sender, RoutedEventArgs e)
    {
        HandoffBar.Visibility = Visibility.Collapsed;
        _handoff?.TrySetResult(true);
        _handoff = null;
    }
}
