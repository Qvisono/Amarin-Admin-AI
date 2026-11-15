using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class PolzaRateUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-polza-rate-" + Guid.NewGuid().ToString("N"));

    public PolzaRateUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void The_rate_is_optional_validated_and_saved_only_for_the_active_profile()
    {
        _wpf.Ui.Invoke(() =>
        {
            using var services = UiServices.Build(_root, "", new BalanceHandler());
            var page = new SettingsKeyPage();
            page.Attach(services);
            page.RefreshKeyRows();
            var panel = (FrameworkElement)page.FindName("PolzaRatePanel");
            var box = (TextBox)page.FindName("PolzaRateBox");
            var error = (FrameworkElement)page.FindName("PolzaRateError");
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            services.KeyStore.Add("Polza", "polza-ui-test-secret", LlmProvider.Polza);
            page.RefreshKeyRows();
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Equal("", box.Text);

            Enter("95,50");
            Assert.Equal(95.5m, services.Settings.RublesPerUsd);
            Enter("100.25");
            Assert.Equal(100.25m, services.SettingsStore.Load().RublesPerUsd);
            foreach (var invalid in new[] { "0", "-10", "rate", "1,2.3" })
            {
                Enter(invalid);
                Assert.Equal(100.25m, services.Settings.RublesPerUsd);
                Assert.Equal(Visibility.Visible, error.Visibility);
            }

            Enter("");
            Assert.Null(services.Settings.RublesPerUsd);
            Assert.Equal(Visibility.Collapsed, error.Visibility);
            Enter("90");
            var other = Path.Combine(_root, "other");
            services.UseProfile(other);
            Enter("110");
            Assert.Equal(90m, new AppSettingsStore(_root).Load().RublesPerUsd);
            Assert.Equal(110m, new AppSettingsStore(other).Load().RublesPerUsd);
            return true;

            void Enter(string value)
            {
                box.Text = value;
                box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            }
        });
    }

    [Fact]
    public void The_polza_rate_card_renders_in_both_languages_and_themes()
    {
        _wpf.Ui.Invoke(() =>
        {
            var language = LanguageManager.Current;
            var theme = ThemeManager.Current.Theme;
            try
            {
                using var services = UiServices.Build(_root, "", new BalanceHandler());
                services.KeyStore.Add("Polza", "polza-ui-shot-secret", LlmProvider.Polza);
                services.Settings.RublesPerUsd = 95.5m;
                foreach (var code in new[] { "ru", "en" })
                {
                    LanguageManager.Apply(code);
                    foreach (var palette in new[] { AppTheme.Dark, AppTheme.Light })
                    {
                        ThemeManager.Apply(palette);
                        var page = new SettingsKeyPage();
                        page.Attach(services);
                        page.RefreshKeyRows();
                        page.Measure(new Size(540, 1200));
                        page.Arrange(new Rect(0, 0, 540, 1200));
                        page.UpdateLayout();
                        var panel = (FrameworkElement)page.FindName("PolzaRatePanel");
                        Assert.True(panel.ActualWidth > 400);
                        Assert.True(panel.ActualHeight > 50);
                        Save(panel, code + "-" + palette.ToString().ToLowerInvariant() + ".png");
                    }
                }
            }
            finally
            {
                LanguageManager.Apply(language);
                ThemeManager.Apply(theme);
            }

            return true;
        });
    }

    private static void Save(FrameworkElement visual, string name)
    {
        var folder = Environment.GetEnvironmentVariable("AMARIN_POLZA_SHOTS");
        if (string.IsNullOrWhiteSpace(folder)) { return; }
        Directory.CreateDirectory(folder);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var area = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            var offset = VisualTreeHelper.GetOffset(visual);
            context.DrawRectangle(new VisualBrush(visual)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(offset.X, offset.Y, area.Width, area.Height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = area
            }, null, area);
        }

        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * 2),
            (int)Math.Ceiling(visual.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var visible = 0;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 0) { visible++; }
        }

        Assert.True(visible > bitmap.PixelWidth * bitmap.PixelHeight / 4,
            "Снимок карточки курса не должен быть пустым.");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder, name));
        encoder.Save(stream);
    }

    private sealed class BalanceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"available":"1250"}""", Encoding.UTF8, "application/json")
            });
    }
}
