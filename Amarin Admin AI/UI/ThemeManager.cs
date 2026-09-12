using System.Windows;
using Amarin.Core;
using Microsoft.Win32;

namespace Amarin.UI;

/// <summary>
/// Swaps the application-level palette, icon and vendor-logo dictionaries.
/// They live at index 0..2 of <see cref="Application.Resources"/> so that popups,
/// tooltips and the model picker user controls resolve the same keys as the main window.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static Application? _application;
    private static AppTheme _theme = AppTheme.Dark;
    private static bool _systemHooked;
    private static string _paletteName = "";
    private static string _iconSuffix = "";

    /// <summary>Theme actually painted right now, after resolving <see cref="AppTheme.System"/>.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>The preset actually painted right now, after resolving <see cref="AppTheme.System"/>.</summary>
    public static ThemePresetInfo Current { get; private set; } = ThemeCatalog.Find(AppTheme.Dark);

    /// <summary>Raised on the UI thread after the dictionaries were swapped.</summary>
    public static event Action? EffectiveThemeChanged;

    public static void Initialize(Application application, AppTheme theme)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;

        EnsureSlots(application);
        Apply(theme);
    }

    /// <summary>
    /// 0 palette, 1 icons, 2 vendor logos, 3 the appearance overrides written by
    /// <see cref="AppearanceManager"/> — last dictionary wins, so 3 sits on top of 0.
    /// </summary>
    internal const int OverrideSlot = 3;

    /// <summary>
    /// 4 — строки интерфейса, их подменяет <see cref="LanguageManager"/>. Отдельным слотом
    /// поверх остальных: ключи у него свои (<c>S.*</c>), с цветами не пересекаются, а слот 3
    /// сдвигать нельзя — <see cref="AppearanceManager"/> пишет в него по номеру.
    /// </summary>
    internal const int StringsSlot = 4;

    internal static void EnsureSlots(Application application)
    {
        var dictionaries = application.Resources.MergedDictionaries;
        while (dictionaries.Count <= StringsSlot)
        {
            dictionaries.Add(new ResourceDictionary());
        }
    }

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        var preset = ThemeCatalog.Resolve(theme, IsSystemLight());

        EnsureSystemHook();

        if (_application is null)
        {
            return;
        }

        EnsureSlots(_application);

        // Skip the swap when nothing changes, but always run it the first time.
        var dictionaries = _application.Resources.MergedDictionaries;
        if (dictionaries[0].Count > 0 && preset.PaletteName == _paletteName)
        {
            return;
        }

        using var timer = PerfLog.Measure("theme_apply");

        IsLight = preset.IsLight;
        Current = preset;
        _paletteName = preset.PaletteName;
        var suffix = preset.IsLight ? "Light" : "Dark";

        // Каждое присваивание в MergedDictionaries — свой обход дерева с инвалидацией всех
        // DynamicResource, а их здесь под восемь сотен. BeginInit/EndInit откладывает
        // оповещение до конца, и на смену темы приходится один обход вместо трёх.
        var resources = _application.Resources;
        resources.BeginInit();
        try
        {
            dictionaries[0] = Load($"Palette.{preset.PaletteName}");

            // Иконки и логотипы зависят только от светлоты. Между двумя тёмными пресетами это
            // те же самые 87 КБ BAML, и раньше они разбирались заново на каждое переключение.
            if (suffix != _iconSuffix || dictionaries[1].Count == 0)
            {
                _iconSuffix = suffix;
                dictionaries[1] = Load($"Icons.{suffix}");
                dictionaries[2] = Load($"AiLogos.{suffix}");
            }
        }
        finally
        {
            resources.EndInit();
        }

        // Brushes follow through DynamicResource, but ImageSources assigned from code
        // (model logos) hold the old object and must be re-fetched.
        EffectiveThemeChanged?.Invoke();
    }

    // Assembly-qualified: a bare "/UI/Theme/..." pack URI resolves against Application.ResourceAssembly,
    // which is not this assembly when the app is hosted (test runner, designer).
    private static readonly string PackPrefix =
        "pack://application:,,,/" +
        Uri.EscapeDataString(typeof(ThemeManager).Assembly.GetName().Name ?? "") +
        ";component/UI/Theme/";

    private static readonly Dictionary<string, ResourceDictionary> Loaded = new(StringComparer.Ordinal);

    /// <summary>
    /// Разбор словаря стоит дорого — у <c>AiLogos.*</c> это 73 КБ векторных <c>Geometry</c> —
    /// а содержимое у них неизменное, поэтому экземпляр переиспользуется.
    /// </summary>
    /// <remarks>
    /// Экземпляр общий: тот, кто запишет что-нибудь прямо в словарь из
    /// <c>Application.Resources.MergedDictionaries[0]</c>, испортит палитру всем последующим
    /// применениям темы, а не только своему. Тему меняют через <see cref="Apply"/>.
    /// </remarks>
    private static ResourceDictionary Load(string name)
    {
        if (Loaded.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri(PackPrefix + name + ".xaml", UriKind.Absolute)
        };
        Loaded[name] = dictionary;
        return dictionary;
    }

    /// <summary>
    /// Палитра «на свой страх» для окон, которые могут открыться до <see cref="Initialize"/> —
    /// сейчас это <see cref="CrashWindow"/> при сбое на старте. Без неё каждый DynamicResource
    /// вернёт null, и окно нарисуется прозрачным на прозрачном фоне, то есть «не появится».
    /// Такой словарь кладут в ресурсы самого окна, и только когда приложение палитру ещё не
    /// подставило: на уровне окна он перекрыл бы общую тему и она перестала бы переключаться.
    /// </summary>
    internal static ResourceDictionary LoadFallbackPalette() => Load("Palette.Dark");

    private static bool IsSystemLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureSystemHook()
    {
        if (_systemHooked)
        {
            return;
        }

        _systemHooked = true;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || _theme != AppTheme.System)
        {
            return;
        }

        // SystemEvents raises on its own thread.
        _application?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
    }
}
