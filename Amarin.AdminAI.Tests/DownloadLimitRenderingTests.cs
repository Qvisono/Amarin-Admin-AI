using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Строка предела помещается в колонку настроек без перекрытий в обеих темах и языках.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DownloadLimitRenderingTests
{
    private readonly WpfFixture _wpf;

    public DownloadLimitRenderingTests(WpfFixture wpf) => _wpf = wpf;

    [Theory]
    [InlineData("Dark", "ru")]
    [InlineData("Dark", "en")]
    [InlineData("Light", "ru")]
    [InlineData("Light", "en")]
    public void The_download_limit_row_fits_the_settings_column(string theme, string language)
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-download-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            _wpf.Ui.Invoke(() =>
            {
                using var services = UiServices.Build(root, "test-key", new HttpClientHandler());
                services.Settings.DownloadMaxSizeMb = 2048;
                var page = new SettingsSecurityPage();
                page.Attach(services);
                page.Load(services.Settings);
                page.ShowDomains(["microsoft.com", "github.com"]);
                var sources = (StackPanel)page.FindName("SourcesSub");
                var field = (StackPanel)page.FindName("DownloadMaxSizeField");
                var box = (TextBox)page.FindName("DownloadMaxSizeBox");
                var toggle = (CheckBox)page.FindName("DownloadSizeLimitToggle");
                var row = (Grid)field.Parent;
                var label = (StackPanel)row.Children[0];
                var title = (TextBlock)label.Children[0];

                // Ресурсы живут только на этом хосте: снимок не меняет тему и язык общего окна
                // и не вызывает Loaded главного окна со служебными сетевыми запросами.
                var host = new Border { Width = 510, UseLayoutRounding = true };
                host.Resources.MergedDictionaries.Add(page.Resources);
                host.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"/Amarin Admin AI;component/UI/Theme/Palette.{theme}.xaml", UriKind.Relative)
                });
                var strings = LanguageManager.LoadBuiltIn(language);
                host.Resources.MergedDictionaries.Add(strings);
                host.SetResourceReference(Border.BackgroundProperty, "Bg.Window");
                ((Panel)sources.Parent).Children.Remove(sources);
                sources.Visibility = Visibility.Visible;
                host.Child = sources;
                host.Measure(new Size(host.Width, double.PositiveInfinity));
                host.Arrange(new Rect(0, 0, host.Width, host.DesiredSize.Height));
                host.UpdateLayout();

                Assert.Equal(strings["S.Security.DownloadLimit"], title.Text);
                Assert.Equal("2048", box.Text);
                var labelBounds = Bounds(label, host);
                var fieldBounds = Bounds(field, host);
                var toggleBounds = Bounds(toggle, host);
                Assert.True(fieldBounds.Left - labelBounds.Right >= 8, "Подпись перекрывается полем размера.");
                Assert.True(toggleBounds.Left - fieldBounds.Right >= 8, "Поле размера перекрывается переключателем.");
                foreach (var element in new FrameworkElement[] { label, field, box, toggle })
                {
                    var bounds = Bounds(element, host);
                    Assert.True(bounds.Width > 0 && bounds.Height > 0, $"{element.Name}: пустой элемент.");
                    Assert.True(bounds.Left >= 0 && bounds.Top >= 0 &&
                        bounds.Right <= host.ActualWidth + 0.5 && bounds.Bottom <= host.ActualHeight + 0.5,
                        $"{element.Name}: элемент выходит за границы колонки настроек.");
                }

                var text = new FormattedText(title.Text, CultureInfo.GetCultureInfo(language), title.FlowDirection,
                    new Typeface(title.FontFamily, title.FontStyle, title.FontWeight, title.FontStretch),
                    title.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(title).PixelsPerDip);
                Assert.True(text.Width <= title.ActualWidth + 0.5, "Заголовок ограничения обрезан.");

                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth),
                    (int)Math.Ceiling(host.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(host);
                var directory = Environment.GetEnvironmentVariable("AMARIN_DOWNLOAD_SHOTS");
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(directory, $"download-limit-{theme.ToLowerInvariant()}-{language}.png"));
                    encoder.Save(file);
                }

                return true;
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) =>
        element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));
}
