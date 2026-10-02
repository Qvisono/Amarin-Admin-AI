using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Подстраница «Голосовой ввод» на странице Behavior (D14). Как у страниц настроек: правда в
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

    /// <summary>Что-то поменялось — строка-ссылка на странице перечитывает своё значение.</summary>
    internal event Action? Changed;

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var settings = services.Settings;
            EngineCombo.SelectedItem = EngineCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string)item.Tag == settings.VoiceEngine.ToString()) ?? EngineCombo.Items[0];
            ModelBox.Text = settings.VoiceModel ?? "";
            LanguageBox.Text = settings.VoiceLanguage ?? "";
            var installed = OperatingSystem.IsWindows() ? LocalSpeech.Languages() : [];
            LocalLanguagesText.Text = installed.Count == 0
                ? Loc.Get("S.Voice.LocalNone")
                : Loc.Format("S.Voice.LocalList", string.Join(", ", installed.Select(culture => culture.DisplayName)));
            ApplyEngine(settings.VoiceEngine);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Значение строки-ссылки: выбранный способ или «не настроен», если распознавать нечем.</summary>
    internal static string Summary(AppSettings settings, bool available) =>
        !available
            ? Loc.Get("S.Voice.NotSetUp")
            : Loc.Get(settings.VoiceEngine switch
            {
                VoiceEngine.Local => "S.Voice.EngineLocal",
                VoiceEngine.Cloud => "S.Voice.EngineCloud",
                _ => "S.Voice.EngineAuto"
            });

    private VoiceEngine SelectedEngine =>
        EngineCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<VoiceEngine>(tag, out var engine)
            ? engine
            : VoiceEngine.Auto;

    /// <summary>Облачная модель нужна всем, кроме «На этом ПК»: там её строка — лишний вопрос.</summary>
    private void ApplyEngine(VoiceEngine engine) =>
        CloudRow.Visibility = engine == VoiceEngine.Local ? Visibility.Collapsed : Visibility.Visible;

    private void Save(Action<AppSettings> change)
    {
        if (_loading || _services is null)
        {
            return;
        }

        change(_services.Settings);
        _services.SettingsStore.Save(_services.Settings);
        Changed?.Invoke();
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CloudRow is null)
        {
            // SelectionChanged приходит из InitializeComponent, до полей.
            return;
        }

        var engine = SelectedEngine;
        ApplyEngine(engine);
        Save(settings => settings.VoiceEngine = engine);
    }

    private void ModelBox_LostFocus(object sender, RoutedEventArgs e) =>
        Save(settings => settings.VoiceModel = string.IsNullOrWhiteSpace(ModelBox.Text) ? null : ModelBox.Text.Trim());

    private void LanguageBox_LostFocus(object sender, RoutedEventArgs e) =>
        Save(settings => settings.VoiceLanguage = string.IsNullOrWhiteSpace(LanguageBox.Text) ? null : LanguageBox.Text.Trim());
}
