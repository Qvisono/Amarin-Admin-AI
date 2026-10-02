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
[Trait(WpfCollection.Category, WpfCollection.Trait)]
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
        Assert.Equal(Loc.Format("S.Updates.SidebarStaged", RuntimeContext.AppVersion, "99.0.0"), badge);
    }

    [Fact]
    public void A_found_release_names_the_new_version_on_the_card_and_in_the_sidebar()
    {
        // «Есть обновление» без номера: какая версия вышла, видно не было — в боковой колонке
        // стоял только номер установленной.
        var (status, badge) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var statusText = (TextBlock)window.FindName("UpdateStatusText")!;
            var version = (TextBlock)window.FindName("SettingsVersionText")!;
            try
            {
                window.LatestRelease = NewerRelease();
                window.LoadUpdatesUi();
                return (statusText.Text, version.Text);
            }
            finally
            {
                window.LatestRelease = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.Contains("99.0.0", status, StringComparison.Ordinal);
        Assert.Equal(Loc.Format("S.Updates.SidebarNewer", RuntimeContext.AppVersion, "99.0.0"), badge);
    }

    [Fact]
    public void A_download_in_progress_names_the_version_and_its_button_says_cancel()
    {
        // Фоновая загрузка оставляла на кнопке «Обновить», и нажатие молча отменяло скачивание:
        // плашка застывала на «Скачивание 37 %».
        var (pill, status, button, visible) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var pillText = (TextBlock)window.FindName("UpdateStatePillText")!;
            var statusText = (TextBlock)window.FindName("UpdateStatusText")!;
            var updateNow = (Button)window.FindName("UpdateNowButton")!;
            using var download = new CancellationTokenSource();
            try
            {
                window.LatestRelease = NewerRelease();
                Set(window, "_autoDownload", download);
                Set(window, "_downloadingVersion", (ReleaseVersion?)new ReleaseVersion(new Version(99, 0, 0), ""));
                Set(window, "_downloadShare", 0.37);
                window.LoadUpdatesUi();
                return (pillText.Text, statusText.Text, updateNow.Content as string, updateNow.Visibility);
            }
            finally
            {
                Set(window, "_autoDownload", null);
                Set(window, "_downloadingVersion", null);
                window.LatestRelease = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.Equal(Loc.Get("S.Updates.Pill.Downloading"), pill);
        Assert.Equal(Loc.Format("S.Updates.DownloadingVersion", "99.0.0", "37"), status);
        Assert.Equal(Loc.Get("S.Common.Cancel"), button);
        Assert.Equal(Visibility.Visible, visible);
    }

    [Fact]
    public void A_poorer_answer_about_the_same_release_does_not_take_the_button_away()
    {
        // Главная поломка обновления: повторная проверка упиралась в лимит API, запасной путь
        // находил ту же версию без файлов — и затирал ею полную. Кнопка «Обновить» пропадала, а
        // с ней и автообновление.
        var (visible, kept) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var button = (Button)window.FindName("UpdateNowButton")!;
            try
            {
                window.LatestRelease = NewerRelease();
                window.LoadUpdatesUi();
                Call(window, "ApplyUpdateResult", new UpdateCheckResult
                {
                    Latest = NewerRelease() with { Assets = [] },
                    UpdateAvailable = true
                });
                return (button.Visibility, window.LatestRelease?.WindowsBuild is not null);
            }
            finally
            {
                window.LatestRelease = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.True(kept);
    }

    [Fact]
    public void A_failed_recheck_keeps_the_found_release_on_the_card()
    {
        var (pill, visible) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var pillText = (TextBlock)window.FindName("UpdateStatePillText")!;
            var button = (Button)window.FindName("UpdateNowButton")!;
            try
            {
                window.LatestRelease = NewerRelease();
                Call(window, "ApplyUpdateResult", UpdateCheckResult.Failed("GitHub answered 503."));
                return (pillText.Text, button.Visibility);
            }
            finally
            {
                Set(window, "_lastCheckError", null);
                window.LatestRelease = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.Equal(Loc.Get("S.Updates.Pill.Available"), pill);
        Assert.Equal(Visibility.Visible, visible);
    }

    [Fact]
    public void A_failed_check_with_nothing_found_says_why_instead_of_up_to_date()
    {
        // Неудачная автопроверка красила пилюлю в «Не удалось», а строка оставалась «Установлена
        // версия …»; открытая позже страница и вовсе говорила «Последняя версия».
        var (pill, status, openRelease) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var pillText = (TextBlock)window.FindName("UpdateStatePillText")!;
            var statusText = (TextBlock)window.FindName("UpdateStatusText")!;
            var open = (Button)window.FindName("OpenReleaseButton")!;
            try
            {
                window.LatestRelease = null;
                Call(window, "ApplyUpdateResult", UpdateCheckResult.Failed("GitHub answered 503."));
                window.LoadUpdatesUi();
                return (pillText.Text, statusText.Text, open.Visibility);
            }
            finally
            {
                Set(window, "_lastCheckError", null);
                window.LoadUpdatesUi();
            }
        });

        Assert.Equal(Loc.Get("S.Updates.Pill.Failed"), pill);
        Assert.Equal("GitHub answered 503.", status);
        Assert.Equal(Visibility.Visible, openRelease);
    }

    [Fact]
    public void A_release_without_a_build_says_where_to_get_it()
    {
        var (status, visible) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var statusText = (TextBlock)window.FindName("UpdateStatusText")!;
            var button = (Button)window.FindName("UpdateNowButton")!;
            try
            {
                window.LatestRelease = NewerRelease() with { Assets = [] };
                window.LoadUpdatesUi();
                return (statusText.Text, button.Visibility);
            }
            finally
            {
                window.LatestRelease = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.Equal(Loc.Format("S.Updates.AvailableNoBuild", "99.0.0", RuntimeContext.AppVersion), status);
        Assert.Equal(Visibility.Collapsed, visible);
    }

    [Fact]
    public void A_found_release_does_not_keep_the_process_alive_when_auto_update_is_off()
    {
        // Невидимый процесс после закрытия окна допустим только по согласию человека — галке
        // «Автообновление». Без неё (здесь окно и вовсе без служб) найденная версия ждёт кнопки.
        var pending = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            try
            {
                window.LatestRelease = NewerRelease();
                return (window.UpdatePendingForExit, window.ShouldDeferClose);
            }
            finally
            {
                window.LatestRelease = null;
                window.LoadUpdatesUi();
            }
        });

        Assert.False(pending.Item1);
        Assert.False(pending.Item2);
    }

    [Fact]
    public void A_second_launch_during_the_background_update_brings_the_window_back()
    {
        // Человек закрыл программу, пока докачивалось обновление, и тут же открыл снова.
        // Второй запуск отдаёт запрос этому процессу, и тот обязан показать окно, а не молча
        // выйти — иначе оба процесса исчезнут, и человек останется ни с чем.
        var (revived, exiting, hidden, generation) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var before = Get<int>(window, "_exitGeneration");
            try
            {
                Set(window, "_hiddenForExit", true);
                Set(window, "_exiting", true);
                var result = Call<bool>(window, "ReviveFromBackgroundExit");
                return (result, Get<bool>(window, "_exiting"), Get<bool>(window, "_hiddenForExit"),
                    Get<int>(window, "_exitGeneration") - before);
            }
            finally
            {
                Set(window, "_hiddenForExit", false);
                Set(window, "_exiting", false);
                window.Show();
            }
        });

        Assert.True(revived);
        Assert.False(exiting);
        Assert.False(hidden);

        // Новый номер попытки: прежний выход, дождавшись загрузки, обязан отступить.
        Assert.Equal(1, generation);
    }

    [Fact]
    public void A_second_launch_after_the_swap_began_asks_for_the_new_version()
    {
        var (revived, relaunch) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            try
            {
                Set(window, "_hiddenForExit", true);
                Set(window, "_swapStarted", true);
                var result = Call<bool>(window, "ReviveFromBackgroundExit");
                return (result, Get<bool>(window, "_relaunchAfterExit"));
            }
            finally
            {
                Set(window, "_hiddenForExit", false);
                Set(window, "_swapStarted", false);
                Set(window, "_relaunchAfterExit", false);
            }
        });

        Assert.False(revived);
        Assert.True(relaunch);
    }

    private const System.Reflection.BindingFlags Hidden =
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, Hidden)!.SetValue(target, value);

    private static T Call<T>(object target, string method) =>
        (T)target.GetType().GetMethod(method, Hidden)!.Invoke(target, null)!;

    private static void Call(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, arguments);

    /// <summary>Сборка, будто бы уже скачанная и ждущая выхода. На диск ничего не кладётся.</summary>
    [Fact]
    public void A_second_window_closes_by_itself_and_never_shuts_the_application_down()
    {
        // Закрытие окна, поднятого тестом рядом с общим, откладывалось ради обновления, а через
        // несколько секунд FinishExitAsync гасил всё приложение — и общий поток интерфейса умирал
        // посреди чужого теста. Выход с обновлением — дело только окна, которому принадлежит
        // приложение.
        var (deferred, stillOpen, shuttingDown) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.Show();
            window.Staged = StagedBuild();
            var held = window.ShouldDeferClose;

            window.RequestExit();
            return (held, window.IsVisible, Application.Current.Dispatcher.HasShutdownStarted);
        });

        Assert.False(deferred);
        Assert.False(stillOpen);
        Assert.False(shuttingDown);
    }

    [Fact]
    public void Release_notes_show_in_the_update_question_only_when_there_are_some()
    {
        // H4: что именно ставится, человек читает до установки, а не на странице релиза потом.
        var (shown, text, hidden) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var panel = (FrameworkElement)window.FindName("UpdateConfirmNotesPanel")!;
            var box = (RichTextBox)window.FindName("UpdateConfirmNotes")!;

            window.ShowConfirmNotes("## Fixes\n\n- The tray menu closes on click");
            var visible = panel.Visibility;
            var content = new System.Windows.Documents.TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text;

            window.ShowConfirmNotes(null);
            return (visible, content, panel.Visibility);
        });

        Assert.Equal(Visibility.Visible, shown);
        Assert.Contains("The tray menu closes on click", text, StringComparison.Ordinal);
        Assert.Equal(Visibility.Collapsed, hidden);
    }

    [Fact]
    public void The_beta_toggle_reflects_the_setting_and_no_previous_version_means_no_rollback_button()
    {
        var (beta, rollback) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            window.LatestRelease = null;
            window.LoadUpdatesUi();
            return (((CheckBox)window.FindName("BetaChannelToggle")!).IsChecked,
                    ((Button)window.FindName("RollbackButton")!).Visibility);
        });

        Assert.False(beta);

        // Рядом с testhost прошлой версии программы нет — кнопке отката там нечего вернуть.
        Assert.Equal(
            UpdateInstaller.PreviousVersionPath(Environment.ProcessPath) is null ? Visibility.Collapsed : Visibility.Visible,
            rollback);
    }

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
                    new string('b', 64))
            ]);
}
