using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Специальные возможности (I2, I3): применить сразу, без перезапуска.</summary>
    public partial class MainWindow
    {
        private void HighContrastToggle_Changed(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            _services.Settings.FollowHighContrast = AppearancePage.HighContrastToggle.IsChecked == true;
            _services.SettingsStore.Save(_services.Settings);
            ApplyAccessibility();
        }

        private void ApplyAccessibility()
        {
            if (_services is not { } services)
            {
                return;
            }

            if (ThemeManager.FollowHighContrast != services.Settings.FollowHighContrast)
            {
                ThemeManager.FollowHighContrast = services.Settings.FollowHighContrast;
                ThemeManager.Reapply();
            }

            // Шрифт ленты — пересборка без сброса лупы: содержимое то же, меняется только вид.
            if (ChatFonts.Apply(services.Settings))
            {
                RebuildTranscript(resetZoom: false);
            }
        }
    }
}
