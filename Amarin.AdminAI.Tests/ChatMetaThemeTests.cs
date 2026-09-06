using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Message chrome built in code has to follow the palette. A brush assigned as a literal looks
/// fine in whichever theme it was picked against and turns unreadable in the other — and because
/// the views are rebuilt on every render, nothing ever throws to point at it.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ChatMetaThemeTests
{
    private readonly WpfFixture _wpf;

    public ChatMetaThemeTests(WpfFixture wpf) => _wpf = wpf;

    private static ChatDisplayMessage Assistant() => new()
    {
        Role = "assistant",
        Id = "meta-theme-probe",
        CreatedAt = DateTime.Now,
        Text = "проба",
        ResolvedModelId = "grok-4-6",
        Status = AssistantStatus.Complete
    };

    /// <summary>Builds one assistant view under <paramref name="theme"/> and reads a colour off it.</summary>
    private uint Sample(AppTheme theme, Func<AssistantMessageView, Color> pick) =>
        _wpf.Ui.Invoke(() =>
        {
            var original = ThemeManager.Current.Theme;
            try
            {
                ThemeManager.Apply(theme);
                var window = Application.Current.Windows.OfType<MainWindow>().Single();
                var view = ChatMessageViews.CreateAssistant(window, Assistant(), new MessageActions());

                // DynamicResource is resolved through the tree, so the view has to be in one.
                var host = (Panel)window.FindName("MessagesPanel");
                host.Children.Add(view.Root);
                host.UpdateLayout();
                try
                {
                    var color = pick(view);
                    return ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                }
                finally
                {
                    host.Children.Remove(view.Root);
                }
            }
            finally
            {
                ThemeManager.Apply(original);
            }
        });

    private static Color Of(Brush brush) => ((SolidColorBrush)brush).Color;

    [Fact]
    public void Model_name_follows_the_theme()
    {
        // The bug this pins: the model name was a literal #B0B0B0, which is a pale grey barely
        // visible on the light palettes while every other item in the meta row re-coloured.
        var dark = Sample(AppTheme.Dark, v => Of(v.ModelName.Foreground));
        var light = Sample(AppTheme.Light, v => Of(v.ModelName.Foreground));

        Assert.NotEqual(dark, light);
    }

    [Fact]
    public void Model_name_stays_readable_on_both_grounds()
    {
        var problems = new List<string>();

        foreach (var preset in ThemeCatalog.Presets)
        {
            var text = Sample(preset.Theme, v => Of(v.ModelName.Foreground));
            var ground = AppearanceManager.Parse(preset.Surface);
            if (ground is null)
            {
                continue;
            }

            var textLuma = AppearanceManager.Luminance(Color.FromRgb(
                (byte)((text >> 16) & 0xFF), (byte)((text >> 8) & 0xFF), (byte)(text & 0xFF)));
            var groundLuma = AppearanceManager.Luminance(ground.Value);

            // 0.35 is chosen to be sharper than the bug: the old literal #B0B0B0 cleared 0.28
            // against the Light ground, so a looser bar would have let it through. With the
            // palette key every preset lands around 0.7.
            if (Math.Abs(textLuma - groundLuma) < 0.35)
            {
                problems.Add($"{preset.DisplayName}: {textLuma:F2} vs ground {groundLuma:F2}");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void The_logo_letter_fallback_follows_the_theme()
    {
        // It sits on AiLogoBorder, whose background is Bg.Card — a light chip on the light
        // palettes, so a fixed light grey would vanish into it.
        var dark = Sample(AppTheme.Dark, v => Of(v.LogoLetter.Foreground));
        var light = Sample(AppTheme.Light, v => Of(v.LogoLetter.Foreground));

        Assert.NotEqual(dark, light);
    }

    [Fact]
    public void No_literal_brushes_are_left_in_the_message_views()
    {
        // Cheap backstop for the whole class of bug: every colour in these builders must come
        // from the palette. The two FormattedText measuring calls pass Brushes.White and never
        // paint anything, so they are excluded by name rather than by colour.
        var source = File.ReadAllText(ProjectFile(Path.Combine("UI", "ChatMessageViews.cs")));

        Assert.DoesNotContain("Color.FromRgb", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Colors.", source, StringComparison.Ordinal);
    }

    private static string ProjectFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Amarin Admin AI", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
