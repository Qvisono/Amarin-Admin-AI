using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The pure half of the Appearance feature: the settings' own clamping, the glass alpha map and
/// the compact composer's decision function. No WPF, so these run in parallel with everything else.
/// </summary>
public sealed class AppearanceSettingsTests
{
    [Fact]
    public void Defaults_are_inert()
    {
        var appearance = new AppearanceSettings();

        // The whole feature has to be invisible until it is switched on, otherwise upgrading
        // would silently restyle everyone's window.
        Assert.False(appearance.Enabled);
        Assert.Equal(BackdropMode.None, appearance.BackdropMode);
        Assert.False(appearance.CompactComposer);
        Assert.Equal("", appearance.AccentColor);
        Assert.False(appearance.Normalize());
    }

    [Fact]
    public void Normalize_clamps_every_numeric_field()
    {
        var appearance = new AppearanceSettings
        {
            MotionSpeed = 99,
            ImageBrightness = -5,
            ImageSaturation = 40,
            ImageBlur = 5000,
            GlassOpacity = 0,
            GlassFrost = 9,
            CornerRadius = 400,
            ChatColumnWidth = 1,
            CompactDelayMs = 0,
            CompactWidthPercent = 500,
            CompactHoverRadius = 0
        };

        Assert.True(appearance.Normalize());

        Assert.Equal(3.0, appearance.MotionSpeed);
        Assert.Equal(0.2, appearance.ImageBrightness);
        Assert.Equal(2.0, appearance.ImageSaturation);
        Assert.Equal(80, appearance.ImageBlur);
        Assert.Equal(0.15, appearance.GlassOpacity);
        Assert.Equal(0.6, appearance.GlassFrost);
        Assert.Equal(20, appearance.CornerRadius);
        Assert.Equal(480, appearance.ChatColumnWidth);
        Assert.Equal(300, appearance.CompactDelayMs);
        Assert.Equal(100, appearance.CompactWidthPercent);
        Assert.Equal(40, appearance.CompactHoverRadius);
    }

    [Fact]
    public void Normalize_reports_no_change_when_already_valid()
    {
        var appearance = new AppearanceSettings { GlassOpacity = 0.5, CornerRadius = 12 };

        // Values already inside their ranges must not be reported as corrected, or
        // AppSettingsStore.Load would rewrite settings.json on every single launch.
        Assert.False(appearance.Normalize());
        Assert.Equal(0.5, appearance.GlassOpacity);
        Assert.Equal(12, appearance.CornerRadius);
    }

    [Fact]
    public void Normalize_wraps_the_gradient_angle()
    {
        var appearance = new AppearanceSettings { GradientAngle = -90 };
        appearance.Normalize();
        Assert.Equal(270, appearance.GradientAngle);

        appearance.GradientAngle = 725;
        appearance.Normalize();
        Assert.Equal(5, appearance.GradientAngle);
    }

    [Theory]
    [InlineData("#abc", "#AABBCC")]
    [InlineData("abc", "#AABBCC")]
    [InlineData("#1e90ff", "#1E90FF")]
    [InlineData("1E90FF", "#1E90FF")]
    [InlineData("#FF1E90FF", "#1E90FF")]
    [InlineData("  #1e90ff  ", "#1E90FF")]
    public void NormalizeHex_accepts_every_reasonable_form(string input, string expected) =>
        Assert.Equal(expected, AppearanceSettings.NormalizeHex(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nope")]
    [InlineData("#12")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    public void NormalizeHex_rejects_garbage(string? input) =>
        Assert.Equal("", AppearanceSettings.NormalizeHex(input));

    [Fact]
    public void Normalize_keeps_the_gradient_usable()
    {
        var appearance = new AppearanceSettings { GradientColors = ["nonsense", "#fff"] };
        appearance.Normalize();

        // A gradient needs two stops to be a gradient; the garbage one is replaced rather than
        // leaving the backdrop with a single colour it cannot interpolate.
        Assert.Equal(2, appearance.GradientColors.Count);
        Assert.Contains("#FFFFFF", appearance.GradientColors);

        appearance.GradientColors = ["#111111", "#222222", "#333333", "#444444", "#555555", "#666666", "#777777"];
        appearance.Normalize();
        Assert.Equal(5, appearance.GradientColors.Count);
    }

    [Fact]
    public void Clone_is_deep_for_the_gradient_list()
    {
        var appearance = new AppearanceSettings { GradientColors = ["#111111", "#222222"] };
        var copy = appearance.Clone();
        copy.GradientColors.Add("#333333");

        Assert.Equal(2, appearance.GradientColors.Count);
        Assert.Equal(3, copy.GradientColors.Count);
    }

    // ───────────────────────── стекло ─────────────────────────

    [Fact]
    public void Glass_at_full_opacity_reproduces_the_palette()
    {
        // The invariant that makes "enabled but neutral" safe: nothing is thinned at 1.0.
        foreach (var key in AppearanceManager.GlassKeys)
        {
            Assert.Equal(1.0, AppearanceManager.GlassAlpha(key, 1.0), 6);
        }
    }

    [Fact]
    public void Glass_never_touches_popups_or_the_window_ground()
    {
        // Bg.Panel backs AllowsTransparency popups and every modal dialog body — thinning it
        // would show the desktop through them. Bg.Window sits under the backdrop entirely.
        Assert.DoesNotContain("Bg.Panel", AppearanceManager.GlassKeys);
        Assert.DoesNotContain("Bg.Window", AppearanceManager.GlassKeys);

        Assert.Equal(1.0, AppearanceManager.GlassAlpha("Bg.Panel", 0.15));
        Assert.Equal(1.0, AppearanceManager.GlassAlpha("Bg.Window", 0.15));
        Assert.Equal(1.0, AppearanceManager.GlassAlpha("Text.Primary", 0.15));
    }

    [Fact]
    public void Glass_thins_big_surfaces_more_than_small_ones()
    {
        var sidebar = AppearanceManager.GlassAlpha("Bg.Sidebar", 0.5);
        var card = AppearanceManager.GlassAlpha("Bg.Card", 0.5);
        var raised = AppearanceManager.GlassAlpha("Bg.Raised", 0.5);
        var elevated = AppearanceManager.GlassAlpha("Bg.Elevated", 0.5);

        Assert.True(sidebar < card, $"sidebar {sidebar} should be thinner than card {card}");
        Assert.True(card < raised, $"card {card} should be thinner than raised {raised}");
        Assert.True(raised < elevated, $"raised {raised} should be thinner than elevated {elevated}");

        // Even at the floor nothing becomes fully invisible.
        foreach (var key in AppearanceManager.GlassKeys)
        {
            var alpha = AppearanceManager.GlassAlpha(key, 0.15);
            Assert.InRange(alpha, 0.15, 1.0);
        }
    }

    // ───────────────────────── компактный ввод ─────────────────────────

    [Fact]
    public void Compact_collapses_only_when_nothing_asks_for_attention()
    {
        Assert.True(ComposerCompactMode.ShouldCollapse(
            enabled: true,
            isEmpty: true,
            toolbarFocused: false,
            hasAttachments: false,
            pointerNear: false));
    }

    [Fact]
    public void Compact_collapses_an_empty_field_even_while_it_holds_the_caret()
    {
        // Focus in the text box is not a reason to stay open — an empty focused field is exactly
        // the idle state worth folding. The box stays on screen in the pill, so the caret is kept.
        Assert.True(ComposerCompactMode.ShouldCollapse(
            enabled: true,
            isEmpty: true,
            toolbarFocused: false,
            hasAttachments: false,
            pointerNear: false));

        // Text, on the other hand, always holds it open.
        Assert.False(ComposerCompactMode.ShouldCollapse(
            enabled: true,
            isEmpty: false,
            toolbarFocused: false,
            hasAttachments: false,
            pointerNear: false));
    }

    [Theory]
    // Each row flips exactly one input; every one of them must keep the composer open.
    [InlineData(false, true, false, false, false)] // выключено
    [InlineData(true, false, false, false, false)] // есть текст
    [InlineData(true, true, true, false, false)]   // фокус на тулбаре
    [InlineData(true, true, false, true, false)]   // есть вложения
    [InlineData(true, true, false, false, true)]   // курсор рядом
    public void Compact_stays_open_when_any_single_signal_is_set(
        bool enabled,
        bool isEmpty,
        bool toolbarFocused,
        bool hasAttachments,
        bool pointerNear) =>
        Assert.False(ComposerCompactMode.ShouldCollapse(
            enabled, isEmpty, toolbarFocused, hasAttachments, pointerNear));

    [Fact]
    public void Compact_folds_away_while_the_model_is_answering()
    {
        // A running turn used to hold the composer open. It is the opposite of what is wanted:
        // the toolbar is disabled anyway and the streaming answer is what needs the room.
        Assert.True(ComposerCompactMode.ShouldCollapse(
            enabled: true,
            isEmpty: true,
            toolbarFocused: false,
            hasAttachments: false,
            pointerNear: false));
    }

    [Fact]
    public void Compact_defaults_are_quick_and_close()
    {
        var appearance = new AppearanceSettings();
        Assert.Equal(800, appearance.CompactDelayMs);
        Assert.Equal(40, appearance.CompactHoverRadius);

        // The default hover radius sits on the slider's own minimum, so it must survive clamping.
        Assert.False(appearance.Normalize());
        Assert.Equal(40, appearance.CompactHoverRadius);
    }
}
