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

    [Fact]
    public void The_card_shows_the_installed_version_and_when_it_last_looked()
    {
        // Ради этих двух строк плашку и переделывали: раньше номер версии был виден только
        // внутри фразы о статусе, а дату последней проверки не показывали нигде, хотя она
        // уже лежала в настройках.
        var (version, lastCheck) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var versionText = (TextBlock)window.FindName("UpdateVersionText")!;
            var lastCheckText = (TextBlock)window.FindName("UpdateLastCheckText")!;

            window.LatestRelease = null;
            window.LoadUpdatesUi();
            return (versionText.Text, lastCheckText.Text);
        });

        Assert.Equal("v" + RuntimeContext.AppVersion, version);
        Assert.False(string.IsNullOrWhiteSpace(lastCheck));
    }

    [Fact]
    public void A_found_release_lights_up_the_pill()
    {
        // Единственное, что видно на плашке издалека. Пилюля обязана перекраситься вместе с
        // находкой, иначе «есть обновление» приходится вычитывать из строки статуса.
        var (found, accented) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var pill = (Border)window.FindName("UpdateStatePill")!;
            var pillText = (TextBlock)window.FindName("UpdateStatePillText")!;
            var version = (TextBlock)window.FindName("SettingsVersionText")!;

            try
            {
                window.LatestRelease = NewerRelease();
                window.LoadUpdatesUi();

                // Кисти сравниваются здесь же: снаружи потока это уже чужой Freezable.
                return (pillText.Text, ReferenceEquals(pill.Background, Application.Current.Resources["Accent.Fill"]));
            }
            finally
            {
                window.LatestRelease = null;
                window.LoadUpdatesUi();
                version.Text = "v" + RuntimeContext.AppVersion;
                version.ClearValue(TextBlock.ForegroundProperty);
            }
        });

        Assert.Equal(Loc.Get("S.Updates.Pill.Available"), found);
        Assert.True(accented);
    }

    [Fact]
    public void The_idle_pill_uses_theme_chrome_not_status_green()
    {
        // SuccessSoft — приглушённый зелёный для текста, не заливка. Пара с Status.Success
        // давала «Последняя версия» контраст около 1.3:1 и игнорировала акцент текущей темы.
        var (surface, caption, outline, radius, thickness) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var pill = (Border)window.FindName("UpdateStatePill")!;
            var pillText = (TextBlock)window.FindName("UpdateStatePillText")!;

            window.LatestRelease = null;
            window.Staged = null;
            window.LoadUpdatesUi();

            return (
                ReferenceEquals(pill.Background, Application.Current.Resources["Bg.Raised"]),
                ReferenceEquals(pillText.Foreground, Application.Current.Resources["Text.Secondary"]),
                ReferenceEquals(pill.BorderBrush, Application.Current.Resources["Accent.Fill"]),
                pill.CornerRadius,
                pill.BorderThickness);
        });

        Assert.True(surface);
        Assert.True(caption);
        Assert.True(outline);
        Assert.Equal(new CornerRadius(6), radius);
        Assert.Equal(new Thickness(1.2), thickness);
    }

    [Fact]
    public void Closing_is_not_held_up_when_there_is_nothing_to_install()
    {
        // Самая опасная сторона установки при выходе: ошибись здесь — и программа перестанет
        // закрываться вовсе.
        var deferred = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            window.Staged = null;
            return window.TryDeferCloseForUpdate();
        });

        Assert.False(deferred);
    }

    [Fact]
    public void A_downloaded_build_holds_the_window_open_until_it_is_installed()
    {
        // Решение проверяется отдельно от самого выхода: позвать его целиком значило бы погасить
        // приложение, общее на все оконные тесты.
        var (withStaged, afterInstall) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            try
            {
                window.Staged = StagedBuild();
                var held = window.ShouldDeferClose;

                window.Staged = null;
                return (held, window.ShouldDeferClose);
            }
            finally
            {
                window.Staged = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.True(withStaged);
        Assert.False(afterInstall);
    }

    [Fact]
    public void A_downloaded_build_is_announced_on_the_card_and_in_the_sidebar()
    {
        var (pill, badge) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var pillText = (TextBlock)window.FindName("UpdateStatePillText")!;
            var version = (TextBlock)window.FindName("SettingsVersionText")!;

            try
            {
                window.Staged = StagedBuild();
                window.LoadUpdatesUi();
                return (pillText.Text, version.Text);
            }
            finally
            {
                window.Staged = null;
                window.LatestRelease = null;
                window.LoadUpdatesUi();
                version.Text = "v" + RuntimeContext.AppVersion;
                version.ClearValue(TextBlock.ForegroundProperty);
            }
        });

        Assert.Equal(Loc.Get("S.Updates.Pill.Ready"), pill);
        Assert.Equal(Loc.Format("S.Updates.SidebarReady", RuntimeContext.AppVersion), badge);
    }

    /// <summary>Сборка, будто бы уже скачанная и ждущая выхода. На диск ничего не кладётся.</summary>
    private static StagedUpdate StagedBuild()
    {
        var release = NewerRelease();
        var asset = release.WindowsBuild!;
        var folder = Path.Combine(Path.GetTempPath(), UpdateInstaller.TempWorkFolderName);

        return new StagedUpdate(
            new UpdatePlan(asset, Path.Combine(folder, "Amarin Admin AI.exe"), folder),
            Path.Combine(folder, asset.Name),
            release.Version);
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
