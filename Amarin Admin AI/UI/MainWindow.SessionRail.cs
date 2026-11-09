using System.Windows;

namespace Amarin.UI;

/// <summary>Размещение статического концепта панели сеанса рядом с лентой.</summary>
public partial class MainWindow
{
    private void InitializeSessionRail()
    {
        SessionRail.Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => UpdateSessionRail();
    }

    private void UpdateSessionRail()
    {
        SessionRail.Visibility = SessionRailPanel.FitsWindow(ActualWidth)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
