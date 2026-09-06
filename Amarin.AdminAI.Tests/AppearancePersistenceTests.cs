using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Appearance settings survive a round trip through settings.json, and a hand-edited file cannot
/// leave the window in a state the user has no way to click out of.
/// </summary>
public sealed class AppearancePersistenceTests
{
    [Fact]
    public void Appearance_round_trips_through_the_store()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            var settings = store.Load();

            settings.Theme = AppTheme.Amethyst;
            settings.Appearance.Enabled = true;
            settings.Appearance.BackdropMode = BackdropMode.Image;
            settings.Appearance.BackgroundImagePath = "background.jpg";
            settings.Appearance.GradientColors = ["#101010", "#A0A0A0", "#FFFFFF"];
            settings.Appearance.GradientMotion = BackdropMotion.Aurora;
            settings.Appearance.ImageFit = BackdropFit.Tile;
            settings.Appearance.GlassOpacity = 0.4;
            settings.Appearance.FontFamily = "Rubik";
            settings.Appearance.CompactComposer = true;
            settings.Appearance.CompactWidthPercent = 45;
            store.Save(settings);

            var loaded = new AppSettingsStore(root).Load();

            Assert.Equal(AppTheme.Amethyst, loaded.Theme);
            Assert.True(loaded.Appearance.Enabled);
            Assert.Equal(BackdropMode.Image, loaded.Appearance.BackdropMode);
            Assert.Equal("background.jpg", loaded.Appearance.BackgroundImagePath);
            Assert.Equal(["#101010", "#A0A0A0", "#FFFFFF"], loaded.Appearance.GradientColors);
            Assert.Equal(BackdropMotion.Aurora, loaded.Appearance.GradientMotion);
            Assert.Equal(BackdropFit.Tile, loaded.Appearance.ImageFit);
            Assert.Equal(0.4, loaded.Appearance.GlassOpacity);
            Assert.Equal("Rubik", loaded.Appearance.FontFamily);
            Assert.True(loaded.Appearance.CompactComposer);
            Assert.Equal(45, loaded.Appearance.CompactWidthPercent);

            // Untouched fields keep their shipped defaults.
            Assert.True(loaded.Appearance.AnimationsEnabled);
            Assert.Equal(1070, loaded.Appearance.ChatColumnWidth);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Settings_written_before_the_feature_existed_get_defaults()
    {
        var root = NewTempRoot();
        try
        {
            // Exactly what a pre-1.14 settings.json looks like: no "appearance" member at all.
            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                """{ "autoScroll": true, "theme": "dark", "uiScalePercent": 125 }""");

            var loaded = new AppSettingsStore(root).Load();

            Assert.NotNull(loaded.Appearance);
            Assert.False(loaded.Appearance.Enabled);
            Assert.Equal(BackdropMode.None, loaded.Appearance.BackdropMode);
            Assert.Equal(125, loaded.UiScalePercent);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_hand_edited_file_is_clamped_on_load_and_rewritten()
    {
        var root = NewTempRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                """
                {
                  "theme": "dark",
                  "appearance": {
                    "enabled": true,
                    "glassOpacity": 0.0,
                    "imageBlur": 9000,
                    "accentColor": "not a colour",
                    "gradientColors": ["#zzzzzz"]
                  }
                }
                """);

            var store = new AppSettingsStore(root);
            var loaded = store.Load();

            Assert.Equal(0.15, loaded.Appearance.GlassOpacity);
            Assert.Equal(80, loaded.Appearance.ImageBlur);
            Assert.Equal("", loaded.Appearance.AccentColor);
            Assert.Equal(2, loaded.Appearance.GradientColors.Count);

            // Load rewrites the file when it had to correct something, so the next launch is clean.
            var reloaded = new AppSettingsStore(root).Load();
            Assert.Equal(0.15, reloaded.Appearance.GlassOpacity);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // temp leftovers are acceptable
        }
    }
}
