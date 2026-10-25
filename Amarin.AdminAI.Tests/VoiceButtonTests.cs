using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Микрофон у поля ввода появляется, как только сохранена настройка голоса, с которой есть чем
/// распознавать, — даже если поле сохранилось уже после закрытия настроек (Esc сразу после ввода).
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class VoiceButtonTests
{
    private readonly WpfFixture _wpf;

    public VoiceButtonTests(WpfFixture wpf) => _wpf = wpf;

    private static readonly FieldInfo ServicesField =
        typeof(MainWindow).GetField("_services", BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>
    /// Названия языков собирает рабочий поток: на потоке окна ICU держал прогрев настроек около
    /// 0,2 с сразу за первым кадром, и колонка чатов в это время не листалась (1.37.0-beta.4/5).
    /// Список при этом заполняется весь, а выбор пункта при заполнении не считается правкой.
    /// </summary>
    [Fact]
    public async Task Language_names_are_built_off_the_window_thread_and_fill_the_list_without_a_save()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-voice-" + Guid.NewGuid().ToString("N"));
        var result = await _wpf.Ui.Invoke(async () =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var previous = ServicesField.GetValue(window);
            using var services = UiServices.Build(root, "k", new HttpClientHandler());
            services.Settings.VoiceLanguage = "de-DE";
            ServicesField.SetValue(window, services);
            var block = (VoiceSettingsBlock)window.FindSetting("VoiceSettings");
            var saves = 0;
            void OnChanged() => saves++;
            block.Changed += OnChanged;
            try
            {
                block.Load(services);
                await VoiceSettingsBlock.NativeNamesReady;
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                var combo = (ComboBox)block.FindName("LanguageCombo");
                return (
                    Count: combo.Items.Count,
                    Selected: (combo.SelectedItem as ComboBoxItem)?.Tag as string,
                    Saves: saves,
                    Builder: VoiceSettingsBlock.NativeNamesThread,
                    Window: Environment.CurrentManagedThreadId);
            }
            finally
            {
                block.Changed -= OnChanged;
                ServicesField.SetValue(window, previous);
            }
        });

        Assert.NotEqual(result.Window, result.Builder);
        Assert.Equal(15, result.Count);
        Assert.Equal("de-DE", result.Selected);
        Assert.Equal(0, result.Saves);
    }

    [Fact]
    public void Saving_a_usable_voice_setting_shows_the_microphone_without_reopening_settings()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-voice-" + Guid.NewGuid().ToString("N"));
        var shown = _wpf.Ui.Invoke(() =>
        {
            var installed = OperatingSystem.IsWindows() ? LocalSpeech.Languages() : [];
            if (installed.Count == 0)
            {
                return (Before: "skip", After: "skip");
            }

            // Язык, для которого распознавателя нет: с ним «На этом ПК» распознавать нечем.
            var missing = new[] { "ru-RU", "ja-JP", "pt-BR", "ko-KR" }
                .FirstOrDefault(name => installed.All(culture => culture.TwoLetterISOLanguageName != name[..2]));
            if (missing is null)
            {
                return (Before: "skip", After: "skip");
            }

            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var previous = ServicesField.GetValue(window);
            using var services = UiServices.Build(root, "k", new HttpClientHandler());
            services.Settings.VoiceEngine = VoiceEngine.Local;
            services.Settings.VoiceLanguage = missing;
            ServicesField.SetValue(window, services);
            try
            {
                var block = (VoiceSettingsBlock)window.FindSetting("VoiceSettings");
                block.Load(services);
                typeof(MainWindow).GetMethod("UpdateMicAvailability", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var before = window.MicHost.Visibility.ToString();

                // Выбрал язык, для которого в Windows есть распознаватель (а «Авто» без ключа — не выход).
                var combo = (ComboBox)block.FindName("LanguageCombo");
                combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                    .First(item => ((string)item.Tag).StartsWith(installed[0].TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
                return (Before: before, After: window.MicHost.Visibility.ToString());
            }
            finally
            {
                ServicesField.SetValue(window, previous);
                window.MicHost.Visibility = Visibility.Collapsed;
            }
        });

        if (shown.Before == "skip")
        {
            return;
        }

        Assert.Equal(nameof(Visibility.Collapsed), shown.Before);
        Assert.Equal(nameof(Visibility.Visible), shown.After);
    }
}
