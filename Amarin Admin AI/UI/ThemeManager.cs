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

    private static void EnsureSlots(Application application)
    {
        var dictionaries = application.Resources.MergedDictionaries;
        while (dictionaries.Count <= OverrideSlot)
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

        IsLight = preset.IsLight;
        Current = preset;
        _paletteName = preset.PaletteName;
        var suffix = preset.IsLight ? "Light" : "Dark";
        dictionaries[0] = Load($"Palette.{preset.PaletteName}");
        dictionaries[1] = Load($"Icons.{suffix}");
        dictionaries[2] = Load($"AiLogos.{suffix}");

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

    private static ResourceDictionary Load(string name) => new()
    {
        Source = new Uri(PackPrefix + name + ".xaml", UriKind.Absolute)
    };

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
