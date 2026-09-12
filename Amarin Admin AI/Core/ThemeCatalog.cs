namespace Amarin.Core;

/// <summary>
/// One palette preset: which dictionary to load, whether icons and vendor logos should use
/// their light or dark variant, and the three colours the settings card previews with.
/// </summary>
/// <param name="Theme">The persisted enum value.</param>
/// <param name="DisplayName">Label on the Appearance card.</param>
/// <param name="PaletteName">File name under <c>UI/Theme</c>, without the extension.</param>
/// <param name="IsLight">Icons, vendor logos and the system-theme hook follow this.</param>
/// <param name="Surface">Preview: window background.</param>
/// <param name="Raised">Preview: a panel on top of it.</param>
/// <param name="Accent">Preview: the accent stripe.</param>
public readonly record struct ThemePresetInfo(
    AppTheme Theme,
    string DisplayName,
    string PaletteName,
    bool IsLight,
    string Surface,
    string Raised,
    string Accent);

/// <summary>
/// The palettes shipped with the app. <see cref="AppTheme.System"/> is not listed here —
/// it resolves to <see cref="AppTheme.Light"/> or <see cref="AppTheme.Dark"/> at paint time.
/// </summary>
public static class ThemeCatalog
{
    /// <summary>
    /// Dark presets first, then light, each group running warm-to-cool, so the grid in the
    /// settings reads as a spectrum rather than an arbitrary pile.
    /// </summary>
    public static IReadOnlyList<ThemePresetInfo> Presets { get; } =
    [
        new(AppTheme.Light, "Light", "Light", true, "#F6F6F7", "#ECECEF", "#1B1B20"),
        new(AppTheme.Dark, "Dark", "Dark", false, "#0E0E0E", "#1F1F1F", "#EAEAEA"),
        new(AppTheme.Obsidian, "Obsidian", "Obsidian", false, "#000000", "#151515", "#F2F2F2"),
        new(AppTheme.Graphite, "Graphite", "Graphite", false, "#1C1C1E", "#323234", "#0A72E6"),
        new(AppTheme.Midnight, "Midnight", "Midnight", false, "#0C0F16", "#181D2C", "#8FB2FF"),
        new(AppTheme.Nord, "Nord", "Nord", false, "#151A20", "#222A33", "#88C0D0"),
        new(AppTheme.Cobalt, "Cobalt", "Cobalt", false, "#080F16", "#101F2C", "#5AB6FF"),
        new(AppTheme.Ocean, "Ocean", "Ocean", false, "#061014", "#102A33", "#4FD1C5"),
        new(AppTheme.Slate, "Slate", "Slate", false, "#101215", "#1E2228", "#B4C2D2"),
        new(AppTheme.Amethyst, "Amethyst", "Amethyst", false, "#100C18", "#1C1630", "#C7A8FF"),
        new(AppTheme.Plum, "Plum", "Plum", false, "#120A12", "#291D2E", "#E4A0D0"),
        new(AppTheme.Neon, "Neon", "Neon", false, "#08070C", "#1A1725", "#FF4FD8"),
        new(AppTheme.Rose, "Rosé", "Rose", false, "#130A0E", "#311E27", "#FF9EC4"),
        new(AppTheme.Quartz, "Quartz", "Quartz", false, "#121013", "#242126", "#FF6FA8"),
        new(AppTheme.Crimson, "Crimson", "Crimson", false, "#120F0F", "#251E1F", "#FF8E8E"),
        new(AppTheme.Ember, "Ember", "Ember", false, "#12100E", "#24211E", "#FFB067"),
        new(AppTheme.Ochre, "Ochre", "Ochre", false, "#1D1B17", "#302D26", "#D79921"),
        new(AppTheme.Rust, "Rust", "Rust", false, "#121110", "#242221", "#E2652A"),
        new(AppTheme.Emerald, "Emerald", "Emerald", false, "#0A1310", "#141F1B", "#6FE0B0"),
        new(AppTheme.Forest, "Forest", "Forest", false, "#07100B", "#12291C", "#8FD694"),
        new(AppTheme.Terminal, "Terminal", "Terminal", false, "#101211", "#1D2320", "#3BD16F"),
        new(AppTheme.Silver, "Silver", "Silver", true, "#F2F2F7", "#EBEBF0", "#006FEB"),
        new(AppTheme.Steel, "Steel", "Steel", true, "#F4F6F8", "#E7ECF1", "#3F6E9C"),
        new(AppTheme.Frost, "Frost", "Frost", true, "#F2F6FA", "#E7EEF5", "#1B4F87"),
        new(AppTheme.Sakura, "Sakura", "Sakura", true, "#FAF3F6", "#F4E5EC", "#A83A6B"),
        new(AppTheme.Mint, "Mint", "Mint", true, "#F2F8F5", "#E6F1EC", "#17694F"),
        new(AppTheme.Paper, "Paper", "Paper", true, "#EAE5DC", "#E1DBD0", "#2E2A26"),
        new(AppTheme.Sand, "Sand", "Sand", true, "#FAF6EF", "#F0E9DC", "#8A5F33"),
        new(AppTheme.Sepia, "Sepia", "Sepia", true, "#F6F2ED", "#F1E9E0", "#6B4A28"),
        new(AppTheme.Ink, "Ink", "Ink", true, "#F4F1EA", "#F2EFE7", "#B33A2B"),
        new(AppTheme.Contrast, "Contrast", "Contrast", true, "#FFFFFF", "#EBEBEB", "#0B4FA8")
    ];

    /// <summary>
    /// Resolves <paramref name="theme"/> to the preset that should actually be painted.
    /// <see cref="AppTheme.System"/> and unknown values fall back through
    /// <paramref name="systemIsLight"/> to Light or Dark.
    /// </summary>
    public static ThemePresetInfo Resolve(AppTheme theme, bool systemIsLight)
    {
        foreach (var preset in Presets)
        {
            if (preset.Theme == theme)
            {
                return preset;
            }
        }

        return Find(systemIsLight ? AppTheme.Light : AppTheme.Dark);
    }

    public static ThemePresetInfo Find(AppTheme theme)
    {
        foreach (var preset in Presets)
        {
            if (preset.Theme == theme)
            {
                return preset;
            }
        }

        return Presets[1];
    }
}
