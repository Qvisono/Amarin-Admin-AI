using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Подстраница «Голосовой ввод» на странице Behavior (D14). Как у страниц настроек: правда в
/// <see cref="AppSettings"/>, контролы только отражают её и пишут изменения под <c>_loading</c>.
/// </summary>
/// <remarks>
/// Модели распознавания и озвучки — из каталогов Venice и OpenRouter (<see cref="AudioModelCatalog"/>),
/// только тех провайдеров, к которым есть ключ: модель без ключа выбрать можно было бы, а работать
/// она бы не стала. Каталоги спрашиваются, когда подстраницу показали, а не при прогреве настроек:
/// сеть не нужна человеку, который сюда не заходит.
/// </remarks>
public partial class VoiceSettingsBlock : UserControl
{
    /// <summary>Языки речи в списке; язык из настроек, которого здесь нет, добавляется к ним.</summary>
    private static readonly string[] Languages =
        ["ru-RU", "en-US", "uk-UA", "be-BY", "kk-KZ", "de-DE", "fr-FR", "es-ES", "it-IT", "pt-BR", "pl-PL", "tr-TR", "zh-CN", "ja-JP"];

    private AppServices? _services;
    private bool _loading;
    private bool _catalogsAsked;

    public VoiceSettingsBlock()
    {
        InitializeComponent();
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
            {
                Detached.Run(LoadCatalogsAsync(), "voice_catalogs");
            }
        };
    }

    /// <summary>Что-то поменялось — строка-ссылка на странице и микрофон пересчитываются.</summary>
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
            FillModels();
            FillLanguages();
            FillSpeechModels();
            FillVoices();

            // Скорость, выставленная не из списка (руками в файле), садится на ближайший пункт.
            SpeechRateCombo.SelectedItem = SpeechRateCombo.Items.OfType<ComboBoxItem>()
                .MinBy(item => Math.Abs(int.Parse((string)item.Tag, CultureInfo.InvariantCulture) - settings.SpeechRate));
            ApplyEngine(settings.VoiceEngine);
            UpdateStatus();
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

    private bool HasKey(LlmProvider provider) =>
        _services is { } services && !string.IsNullOrWhiteSpace(services.Keys.DefaultFor(provider).Secret);

    private bool HasKeyFor(string model) =>
        _services is { } services && !string.IsNullOrWhiteSpace(services.Keys.CredentialFor(model, null).Secret);

    /// <summary>Модели провайдеров с ключом, уже загруженные в каталог.</summary>
    private IEnumerable<AudioModel> Models(AudioKind kind) =>
        _services is not { } services
            ? []
            : AudioModelCatalog.Providers.Where(HasKey).SelectMany(provider => services.AudioModels.Cached(kind, provider) ?? []);

    /// <summary>«Whisper Large V3 · Venice»: одна и та же модель бывает у обоих провайдеров.</summary>
    private string Display(AudioKind kind, string id)
    {
        var name = _services?.AudioModels.Find(kind, id)?.Name ?? ModelRef.Bare(id);
        return name + " · " + ProviderSpec.For(ModelRef.Of(id)).Name;
    }

    private static ComboBoxItem Item(string tag, string text) => new() { Tag = tag, Content = text };

    private static void Select(ComboBox combo, string? tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals((string)item.Tag, tag ?? "", StringComparison.OrdinalIgnoreCase)) ?? combo.Items[0];

    private void FillModels()
    {
        if (_services is not { } services)
        {
            return;
        }

        var chosen = services.Settings.VoiceModel?.Trim() ?? "";
        ModelCombo.Items.Clear();
        var automatic = VoiceAvailability.CloudModel(new AppSettings(), HasKeyFor);
        ModelCombo.Items.Add(Item("", automatic is null ? Loc.Get("S.Voice.EngineAuto") : Loc.Format("S.Voice.ModelAuto", Display(AudioKind.Transcription, automatic))));
        foreach (var model in Models(AudioKind.Transcription))
        {
            ModelCombo.Items.Add(Item(model.Id, Display(AudioKind.Transcription, model.Id)));
        }

        if (chosen.Length > 0 && !ModelCombo.Items.OfType<ComboBoxItem>().Any(item => string.Equals((string)item.Tag, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            ModelCombo.Items.Add(Item(chosen, Display(AudioKind.Transcription, chosen)));
        }

        Select(ModelCombo, chosen);
    }

    /// <summary>
    /// Названия языков списка на самих языках — один раз за процесс и на рабочем потоке.
    /// </summary>
    /// <remarks>
    /// ICU при первом обращении грузит данные каждого языка: на четырнадцать языков это около
    /// 0,2 с. «Общие» заполняются в прогреве настроек на потоке окна сразу за первым кадром, и в
    /// 1.37.0-beta.4/5 эти 0,2 с стояли там одним куском: колонка чатов в первую секунду после
    /// запуска не листалась, хотя подстраница «Голосовой ввод» свёрнута и списка не видно.
    /// </remarks>
    private static readonly Lazy<Task<Dictionary<string, string>>> NativeNames = new(() => Task.Run(BuildNativeNames));

    /// <summary>Поток, на котором собраны названия, — для теста: поток окна собирать их не должен.</summary>
    internal static int NativeNamesThread { get; private set; }

    /// <summary>Названия готовы — тесту, чтобы дождаться заполнения списка.</summary>
    internal static Task NativeNamesReady => NativeNames.Value;

    private static Dictionary<string, string> BuildNativeNames()
    {
        NativeNamesThread = Environment.CurrentManagedThreadId;
        return Languages.ToDictionary(name => name, Culture, StringComparer.OrdinalIgnoreCase);
    }

    private void FillLanguages()
    {
        var names = NativeNames.Value;
        if (!names.IsCompleted)
        {
            // Список лежит на свёрнутой подстранице: заполнится, когда названия будут готовы.
            Detached.Run(FillLanguagesWhenReady(names), "voice_languages");
            return;
        }

        // Не собрались (такого не ждём) — посчитаются на месте, как до выноса на рабочий поток.
        var known = names.IsCompletedSuccessfully ? names.Result : null;
        var chosen = _services?.Settings.VoiceLanguage?.Trim() ?? "";
        LanguageCombo.Items.Clear();
        LanguageCombo.Items.Add(Item("", Loc.Get("S.Voice.LanguageEmpty")));
        foreach (var name in chosen.Length > 0 && !Languages.Contains(chosen, StringComparer.OrdinalIgnoreCase) ? [.. Languages, chosen] : Languages)
        {
            LanguageCombo.Items.Add(Item(name, known is not null && known.TryGetValue(name, out var shown) ? shown : Culture(name)));
        }

        Select(LanguageCombo, chosen);
    }

    /// <summary>
    /// Заполняет список, когда названия собраны. Под <c>_loading</c>: выбор пункта при заполнении —
    /// не выбор человека, и настройка не должна перезаписаться.
    /// </summary>
    private async Task FillLanguagesWhenReady(Task names)
    {
        await names.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
        var loading = _loading;
        _loading = true;
        try
        {
            FillLanguages();
        }
        finally
        {
            _loading = loading;
        }
    }

    private static string Culture(string name)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(name);
            return char.ToUpper(culture.NativeName[0], culture) + culture.NativeName[1..];
        }
        catch (CultureNotFoundException)
        {
            return name;
        }
    }

    private void FillSpeechModels()
    {
        var chosen = _services?.Settings.SpeechModel?.Trim() ?? "";
        SpeechModelCombo.Items.Clear();
        SpeechModelCombo.Items.Add(Item("", Loc.Get("S.Speech.EngineWindows")));
        foreach (var model in Models(AudioKind.Speech))
        {
            SpeechModelCombo.Items.Add(Item(model.Id, Display(AudioKind.Speech, model.Id)));
        }

        if (chosen.Length > 0 && !SpeechModelCombo.Items.OfType<ComboBoxItem>().Any(item => string.Equals((string)item.Tag, chosen, StringComparison.OrdinalIgnoreCase)))
        {
            SpeechModelCombo.Items.Add(Item(chosen, Display(AudioKind.Speech, chosen)));
        }

        Select(SpeechModelCombo, chosen);
    }

    /// <summary>
    /// Голоса того, чем читают: у Windows — установленные голоса, у модели — её голоса из каталога.
    /// Выбранный, но пропавший голос остаётся в списке: открытие страницы не должно молча сбрасывать
    /// выбор человека.
    /// </summary>
    private void FillVoices()
    {
        if (_services is not { } services)
        {
            return;
        }

        var model = services.Settings.SpeechModel?.Trim() ?? "";
        var chosen = services.Settings.SpeechVoice ?? "";
        var voices = model.Length == 0
            ? OperatingSystem.IsWindows() ? [.. SpeechOutput.Voices()] : new List<string>()
            : [.. services.AudioModels.Find(AudioKind.Speech, model)?.Voices ?? []];
        if (chosen.Length > 0 && !voices.Contains(chosen, StringComparer.OrdinalIgnoreCase))
        {
            voices.Add(chosen);
        }

        SpeechVoiceCombo.Items.Clear();
        SpeechVoiceCombo.Items.Add(Item("", Loc.Get("S.Speech.VoiceAuto")));
        foreach (var voice in voices)
        {
            SpeechVoiceCombo.Items.Add(Item(voice, voice));
        }

        Select(SpeechVoiceCombo, chosen);
    }

    /// <summary>Строка под «Распознаванием»: что распознаёт сейчас, или почему распознавать нечем.</summary>
    private void UpdateStatus()
    {
        if (_services is not { } services)
        {
            return;
        }

        var settings = services.Settings;
        var culture = VoiceAvailability.SpeechCulture(settings, LanguageManager.Current);
        var local = OperatingSystem.IsWindows() ? (Func<CultureInfo, bool>)(item => LocalSpeech.Find(item) is not null) : _ => false;
        StatusText.Text = VoiceAvailability.Route(settings, culture, local, HasKeyFor) switch
        {
            VoiceRoute.Local => Loc.Format("S.Voice.StatusLocal", culture.DisplayName),
            VoiceRoute.Cloud when VoiceAvailability.CloudModel(settings, HasKeyFor) is { } model =>
                Loc.Format("S.Voice.StatusCloud", Display(AudioKind.Transcription, model)),
            VoiceRoute.NoLocal => Loc.Format("S.Voice.StatusNoLocal", culture.DisplayName),
            _ => Loc.Get("S.Voice.StatusNoKey")
        };
    }

    /// <summary>Каталоги провайдеров с ключом; пришли — списки собираются заново, выбор остаётся.</summary>
    private async Task LoadCatalogsAsync()
    {
        if (_services is not { } services || _catalogsAsked)
        {
            return;
        }

        _catalogsAsked = true;
        var loads = AudioModelCatalog.Providers.Where(HasKey)
            .SelectMany(provider => new[] { AudioKind.Transcription, AudioKind.Speech }.Select(kind => services.AudioModels.LoadAsync(kind, provider, CancellationToken.None)))
            .ToList();
        var lists = await Task.WhenAll(loads);

        // Неудачная загрузка не запоминается: следующий показ подстраницы спросит снова.
        _catalogsAsked = lists.All(list => list.Count > 0);
        _loading = true;
        try
        {
            FillModels();
            FillSpeechModels();
            FillVoices();
            UpdateStatus();
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
        UpdateStatus();
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

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelCombo.SelectedItem is ComboBoxItem { Tag: string model })
        {
            Save(settings => settings.VoiceModel = model.Length == 0 ? null : model);
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is ComboBoxItem { Tag: string language })
        {
            Save(settings => settings.VoiceLanguage = language.Length == 0 ? null : language);
        }
    }

    private void SpeechModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SpeechModelCombo.SelectedItem is not ComboBoxItem { Tag: string model })
        {
            return;
        }

        // Голос принадлежит модели: голос Windows модель не примет, и наоборот.
        Save(settings =>
        {
            settings.SpeechModel = model.Length == 0 ? null : model;
            settings.SpeechVoice = null;
        });
        _loading = true;
        try
        {
            FillVoices();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SpeechVoiceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeechVoiceCombo.SelectedItem is ComboBoxItem { Tag: string voice })
        {
            Save(settings => settings.SpeechVoice = voice.Length == 0 ? null : voice);
        }
    }

    private void SpeechRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeechRateCombo.SelectedItem is ComboBoxItem { Tag: string rate })
        {
            Save(settings => settings.SpeechRate = int.Parse(rate, CultureInfo.InvariantCulture));
        }
    }
}
