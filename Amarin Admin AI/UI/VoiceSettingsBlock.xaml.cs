using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок «Голосовой ввод» на странице Behavior (D14). Как у страниц настроек: правда в
/// <see cref="AppSettings"/>, контролы только отражают её и пишут изменения под <c>_loading</c>.
/// </summary>
public partial class VoiceSettingsBlock : UserControl
{
    private AppServices? _services;
    private bool _loading;

    public VoiceSettingsBlock()
    {
        InitializeComponent();
        ModelBox.TextChanged += (_, _) => ModelPlaceholder.Visibility = ModelBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        LanguageBox.TextChanged += (_, _) => LanguagePlaceholder.Visibility = LanguageBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var settings = services.Settings;
            (settings.VoiceEngine switch
            {
                VoiceEngine.Local => EngineLocal,
                VoiceEngine.Cloud => EngineCloud,
                _ => EngineAuto
            }).IsChecked = true;
            ModelBox.Text = settings.VoiceModel ?? "";
            LanguageBox.Text = settings.VoiceLanguage ?? "";
            var installed = OperatingSystem.IsWindows() ? LocalSpeech.Languages() : [];
            LocalLanguagesText.Text = installed.Count == 0
                ? Loc.Get("S.Voice.LocalNone")
                : Loc.Format("S.Voice.LocalList", string.Join(", ", installed.Select(culture => culture.Name)));
        }
        finally
        {
            _loading = false;
        }
    }

    private void Save(Action<AppSettings> change)
    {
        if (_loading || _services is null)
        {
            return;
        }

        change(_services.Settings);
        _services.SettingsStore.Save(_services.Settings);
    }

    private void Engine_Checked(object sender, RoutedEventArgs e) =>
        Save(settings => settings.VoiceEngine = EngineLocal.IsChecked == true
            ? VoiceEngine.Local
            : EngineCloud.IsChecked == true ? VoiceEngine.Cloud : VoiceEngine.Auto);

    private void ModelBox_LostFocus(object sender, RoutedEventArgs e) =>
        Save(settings => settings.VoiceModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? null : ModelBox.Text.Trim());

    private void LanguageBox_LostFocus(object sender, RoutedEventArgs e) =>
        Save(settings => settings.VoiceLanguage = string.IsNullOrWhiteSpace(LanguageBox.Text) ? null : LanguageBox.Text.Trim());
}
