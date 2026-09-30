using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>Специальные возможности (I2, I3): применить сразу, без перезапуска.</summary>
    public partial class MainWindow
    {
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
