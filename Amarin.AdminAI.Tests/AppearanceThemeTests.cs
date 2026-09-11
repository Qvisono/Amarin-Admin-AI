using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The palette presets and the backdrop host. A missing key in one palette shows up only when
/// the user picks that theme, so the dictionaries are checked against each other up front.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AppearanceThemeTests
{
    private readonly WpfFixture _wpf;

    public AppearanceThemeTests(WpfFixture wpf) => _wpf = wpf;

    private static ResourceDictionary LoadPalette(string name) =>
        (ResourceDictionary)Application.LoadComponent(
            new Uri($"/Amarin Admin AI;component/UI/Theme/Palette.{name}.xaml", UriKind.Relative));

    [Fact]
    public void Every_preset_defines_the_same_keys_as_Dark()
    {
        var missing = _wpf.Ui.Invoke(() =>
        {
            var baseline = LoadPalette("Dark").Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
            var problems = new List<string>();

            foreach (var preset in ThemeCatalog.Presets)
            {
                var keys = LoadPalette(preset.PaletteName).Keys.Cast<object>()
                    .Select(k => k.ToString()!)
                    .ToHashSet();

                foreach (var key in baseline.Except(keys))
                {
                    problems.Add($"{preset.PaletteName} is missing {key}");
                }

                foreach (var key in keys.Except(baseline))
                {
                    problems.Add($"{preset.PaletteName} has an extra {key}");
                }
            }

            return problems;
        });

        Assert.Empty(missing);
    }

    [Fact]
    public void Bg_Glass_mirrors_Bg_Panel_in_every_preset()
    {
        // The two are the same colour by construction; the split only exists so the composer can
        // be frosted without dragging popups and dialogs along. If they ever drift, the composer
        // stops matching the rest of the theme when the backdrop is off.
        var mismatched = _wpf.Ui.Invoke(() =>
        {
            var problems = new List<string>();
            foreach (var preset in ThemeCatalog.Presets)
            {
                var palette = LoadPalette(preset.PaletteName);
                var panel = palette["Bg.Panel"] as System.Windows.Media.SolidColorBrush;
                var glass = palette["Bg.Glass"] as System.Windows.Media.SolidColorBrush;
                if (panel is null || glass is null || panel.Color != glass.Color)
                {
                    problems.Add(preset.PaletteName);
                }
            }

            return problems;
        });

        Assert.Empty(mismatched);
    }

    [Fact]
    public void Every_preset_can_be_applied()
    {
        var problems = _wpf.Ui.Invoke(() =>
        {
            var failures = new List<string>();
            var original = ThemeManager.Current.Theme;

            foreach (var preset in ThemeCatalog.Presets)
            {
                ThemeManager.Apply(preset.Theme);

                if (ThemeManager.Current.PaletteName != preset.PaletteName)
                {
                    failures.Add($"{preset.Theme}: painted {ThemeManager.Current.PaletteName}");
                }

                if (ThemeManager.IsLight != preset.IsLight)
                {
                    failures.Add($"{preset.Theme}: IsLight {ThemeManager.IsLight}");
                }

                // A key from the palette and one from the icon set: both dictionaries must have
                // been swapped, not just the first.
                if (Application.Current.TryFindResource("Bg.Sidebar") is null)
                {
                    failures.Add($"{preset.Theme}: no Bg.Sidebar");
                }

                if (Application.Current.TryFindResource("ExportJson") is null)
                {
                    failures.Add($"{preset.Theme}: no ExportJson icon");
                }
            }

            ThemeManager.Apply(original);
            return failures;
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void System_theme_resolves_to_a_real_preset()
    {
        Assert.Equal(AppTheme.Light, ThemeCatalog.Resolve(AppTheme.System, systemIsLight: true).Theme);
        Assert.Equal(AppTheme.Dark, ThemeCatalog.Resolve(AppTheme.System, systemIsLight: false).Theme);
        Assert.Equal(AppTheme.Nord, ThemeCatalog.Resolve(AppTheme.Nord, systemIsLight: true).Theme);

        // System itself is never a card in the grid — it is the separate "follow Windows" switch.
        Assert.DoesNotContain(ThemeCatalog.Presets, p => p.Theme == AppTheme.System);
    }

    [Fact]
    public void Every_theme_except_System_has_exactly_one_card()
    {
        // A preset added to the enum but not to the catalog silently disappears from the grid,
        // and a duplicated one would give the radio group two winners.
        var expected = Enum.GetValues<AppTheme>().Where(t => t != AppTheme.System).ToList();
        var listed = ThemeCatalog.Presets.Select(p => p.Theme).ToList();

        Assert.Equal(expected.Count, listed.Count);
        Assert.Equal(expected.Count, listed.Distinct().Count());
        Assert.Empty(expected.Except(listed));
    }

    [Fact]
    public void Card_previews_are_parseable_and_readable()
    {
        var problems = new List<string>();
        foreach (var preset in ThemeCatalog.Presets)
        {
            foreach (var (name, hex) in new[]
                     {
                         ("Surface", preset.Surface), ("Raised", preset.Raised), ("Accent", preset.Accent)
                     })
            {
                if (AppearanceManager.Parse(hex) is null)
                {
                    problems.Add($"{preset.DisplayName}.{name} = '{hex}'");
                }
            }

            var surface = AppearanceManager.Parse(preset.Surface);
            var accent = AppearanceManager.Parse(preset.Accent);
            if (surface is null || accent is null)
            {
                continue;
            }

            // The accent bar has to be visible against the preview's own ground, otherwise the
            // card reads as an empty rectangle.
            var contrast = Math.Abs(AppearanceManager.Luminance(surface.Value)
                                    - AppearanceManager.Luminance(accent.Value));
            if (contrast < 0.25)
            {
                problems.Add($"{preset.DisplayName}: accent too close to surface ({contrast:F2})");
            }

            // And the preview must agree with the palette about being light or dark.
            var isLight = AppearanceManager.Luminance(surface.Value) > 0.5;
            if (isLight != preset.IsLight)
            {
                problems.Add($"{preset.DisplayName}: IsLight={preset.IsLight} but surface is {(isLight ? "light" : "dark")}");
            }
        }

        Assert.Empty(problems);
    }

    /// <summary>
    /// WCAG relative luminance — gamma-corrected, unlike <see cref="AppearanceManager.Luminance"/>,
    /// which is a cheap Rec. 709 approximation for picking black-or-white and would overstate the
    /// contrast of mid-tone accents.
    /// </summary>
    private static double Relative(System.Windows.Media.Color color)
    {
        static double Channel(byte value)
        {
            var part = value / 255.0;
            return part <= 0.03928 ? part / 12.92 : Math.Pow((part + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static double Contrast(System.Windows.Media.Color first, System.Windows.Media.Color second)
    {
        var a = Relative(first);
        var b = Relative(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    [Fact]
    public void Body_text_and_accent_buttons_stay_readable_in_every_preset()
    {
        // A new palette is four dozen hand-picked colours, and the two pairs that decide whether
        // the app is usable at all are body text on the window and the caption on a filled button.
        // The rest is a matter of taste; these two are not.
        var problems = _wpf.Ui.Invoke(() =>
        {
            var failures = new List<string>();

            foreach (var preset in ThemeCatalog.Presets)
            {
                var palette = LoadPalette(preset.PaletteName);

                System.Windows.Media.Color Color(string key) =>
                    ((System.Windows.Media.SolidColorBrush)palette[key]).Color;

                var body = Contrast(Color("Text.Body"), Color("Bg.Window"));
                if (body < 4.5)
                {
                    failures.Add($"{preset.PaletteName}: Text.Body on Bg.Window is {body:F2}:1");
                }

                var onAccent = Contrast(Color("Text.OnAccent"), Color("Accent.Fill"));
                if (onAccent < 4.5)
                {
                    failures.Add($"{preset.PaletteName}: Text.OnAccent on Accent.Fill is {onAccent:F2}:1");
                }
            }

            return failures;
        });

        Assert.Empty(problems);
    }

    [Fact]
    public void Backdrop_host_does_not_intercept_the_window_drag()
    {
        // Dragging the window works because clicks on empty chrome fall through to the root
        // Grid's own background (Grid_MouseDown). A hit-testable backdrop would break it silently.
        var (found, hitTestable) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var host = window.FindName("BackdropHost") as FrameworkElement;
            return (host is not null, host?.IsHitTestVisible ?? true);
        });

        Assert.True(found, "BackdropHost is missing from MainWindow.xaml");
        Assert.False(hitTestable);
    }

    [Fact]
    public void Composer_is_ready_for_the_compact_pill()
    {
        var (clips, bottomAligned, toolbarRowIsAuto) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var composer = (Border)window.FindName("ComposerBorder");
            var layout = (Grid)window.FindName("ComposerLayout");
            return (
                composer.ClipToBounds,
                composer.VerticalAlignment == VerticalAlignment.Bottom,
                layout.RowDefinitions[2].Height.IsAuto);
        });

        // Without the clip the toolbar paints outside the rounded pill mid-animation; without
        // Bottom the pill floats in the middle of the row; a fixed toolbar row would pin the
        // height no matter how far the toolbar shrinks.
        Assert.True(clips, "ComposerBorder must clip its children");
        Assert.True(bottomAligned, "ComposerBorder must be bottom-aligned");
        Assert.True(toolbarRowIsAuto, "the toolbar row must be Auto-sized");
    }
}
