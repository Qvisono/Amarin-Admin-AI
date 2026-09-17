using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Строка обновлений в настройках. Проверяется то, из-за чего обновлением нельзя было
/// воспользоваться: найденный релиз должен переживать открытие страницы.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class UpdateUiTests
{
    private readonly WpfFixture _wpf;

    public UpdateUiTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Opening_the_settings_does_not_wipe_a_found_release()
    {
        // Автопроверка при запуске находит новую версию и показывает «Обновить», но человек в
        // этот момент ещё не в настройках. Раньше открытие страницы гасило обе кнопки, и
        // обновиться можно было только нажав «Проверить» ещё раз — уже внутри страницы.
        var (first, second) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var button = (Button)window.FindName("UpdateNowButton")!;
            var version = (TextBlock)window.FindName("SettingsVersionText")!;

            try
            {
                window.LatestRelease = NewerRelease();

                window.LoadUpdatesUi();
                var once = button.Visibility;

                window.LoadUpdatesUi();
                return (once, button.Visibility);
            }
            finally
            {
                window.LatestRelease = null;
                window.LoadUpdatesUi();
                version.Text = "v" + RuntimeContext.AppVersion;
                version.ClearValue(TextBlock.ForegroundProperty);
            }
        });

        Assert.Equal(Visibility.Visible, first);
        Assert.Equal(Visibility.Visible, second);
    }

    [Fact]
    public void Without_a_release_the_page_says_which_version_is_installed()
    {
        var (visible, status) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var button = (Button)window.FindName("UpdateNowButton")!;
            var text = (TextBlock)window.FindName("UpdateStatusText")!;

            window.LatestRelease = null;
            window.LoadUpdatesUi();
            return (button.Visibility, text.Text);
        });

        Assert.Equal(Visibility.Collapsed, visible);
        Assert.Contains(RuntimeContext.AppVersion, status, StringComparison.Ordinal);
    }

    /// <summary>Релиз заведомо новее любой собранной версии.</summary>
    private static ReleaseInfo NewerRelease() =>
        new(
            "v99.0.0",
            new Version(99, 0, 0),
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v99.0.0",
            "Release 99.0.0",
            null,
            [
                new ReleaseAsset(
                    "Amarin-Admin-AI-v99.0.0-win-x64.exe",
                    "https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v99.0.0/Amarin-Admin-AI-v99.0.0-win-x64.exe",
                    1024,
                    null)
            ]);
}
