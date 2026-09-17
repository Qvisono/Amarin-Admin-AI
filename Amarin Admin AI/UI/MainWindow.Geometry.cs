using System.Windows;

namespace Amarin.UI
{
    /// <summary>
    /// Память о размере окна: галочка в настройках и снимок при закрытии. Сам пересчёт пикселей
    /// живёт в <see cref="WindowGeometry"/>.
    /// </summary>
    public partial class MainWindow
    {
        private void RememberWindowSizeToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var remember = RememberWindowSizeToggle.IsChecked == true;
            _services.Settings.RememberWindowSize = remember;

            if (!remember)
            {
                // Снятая галочка означает «открывай как в первый раз», поэтому прошлый размер
                // забывается сразу: иначе он вернулся бы при следующем включении галочки, и это
                // выглядело бы как размер, взявшийся ниоткуда.
                _services.Settings.WindowPixelWidth = 0;
                _services.Settings.WindowPixelHeight = 0;
                _services.Settings.WindowMaximized = false;
            }
            else
            {
                WindowGeometry.Capture(this, _services.Settings);
            }

            _services.SettingsStore.Save(_services.Settings);
        }

        /// <summary>Снимает размер окна на закрытии. Ошибки здесь молчат: программа уже уходит.</summary>
        private void SaveWindowGeometry()
        {
            if (_services is null || !_services.Settings.RememberWindowSize)
            {
                return;
            }

            WindowGeometry.Capture(this, _services.Settings);
            _services.SettingsStore.Save(_services.Settings);
        }
    }
}
