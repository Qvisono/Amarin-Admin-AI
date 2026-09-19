using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Снимок страницы «Key &amp; Info» во временную папку — так же, как проверялись формулы
/// и углы окна. Ловит то, что числами не поймать: карточку, которая читается как стена.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class KeyPageShotTests
{
    private readonly WpfFixture _wpf;

    public KeyPageShotTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_page_renders_with_data()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "amarin-keypage-" + Guid.NewGuid().ToString("N") + ".png");

        var opaque = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var page = new SettingsKeyPage { Width = 520, Height = 900 };

            // В дерево окна: DynamicResource палитры разрешается по нему.
            var host = (Panel)window.FindName("MessagesPanel");
            host.Children.Add(page);
            try
            {
                page.ShowForShot(SampleReport(), SampleKeys(), Removed());
                page.HoverForShot(3);
                page.Measure(new Size(520, 900));
                page.Arrange(new Rect(0, 0, 520, 900));
                page.UpdateLayout();

                // Через VisualBrush, а не прямой Render: страница лежит внутри чужой панели,
                // и её собственное смещение уехало бы в снимок вместе с ней.
                var canvas = new DrawingVisual();
                using (var context = canvas.RenderOpen())
                {
                    context.DrawRectangle(
                        (Brush)page.FindResource("Bg.Panel"), null, new Rect(0, 0, 520, 900));
                    context.DrawRectangle(new VisualBrush(page), null, new Rect(0, 0, 520, 900));
                }

                var bitmap = new RenderTargetBitmap(520, 900, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(canvas);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(path))
                {
                    encoder.Save(stream);
                }

                var pixels = new int[520 * 900];
                bitmap.CopyPixels(pixels, 520 * 4, 0);
                return pixels.Count(pixel => (uint)pixel >> 24 > 16);
            }
            finally
            {
                host.Children.Remove(page);
            }
        });

        Assert.True(opaque > 20000, $"страница почти пуста: {opaque} точек");
        Assert.True(File.Exists(path));
    }

    private static SpendReport SampleReport()
    {
        var today = DateTime.Now.Date;
        decimal[] daily = [0.021m, 0.134m, 0.068m, 0.312m, 0.095m, 0.186m, 0.043m];
        var points = daily
            .Select((value, index) => new SpendPoint(today.AddDays(index - 6), value, 0m, 3))
            .ToList();

        return new SpendReport
        {
            Status = SpendStatus.Local,
            Period = SpendPeriod.Week,
            Points = points,
            TotalUsd = daily.Sum(),
            Models =
            [
                new SpendModelRow("Grok 4.6", "grok-4-6", 0.512m, 0m, 14),
                new SpendModelRow("Claude Sonnet 5", "claude-sonnet-5", 0.221m, 0m, 6),
                new SpendModelRow("Поиск в интернете", "web-search-request", 0.126m, 0m, 4)
            ]
        };
    }

    private static IReadOnlyList<ApiKeyEntry> SampleKeys() =>
    [
        new("environment", "VENICE_API_KEY", "VENabcdefghijklmnopqrstuvIyuC",
            ApiKeySource.Environment, true),
        new("k2", "Рабочий", "vk-second-key-abcdefghijkl3F7q", ApiKeySource.Stored, false),
        new("k3", "Старый", null, ApiKeySource.Stored, false)
    ];

    /// <summary>Убранный ключ окружения: строка под списком с предложением вернуть его.</summary>
    private static IReadOnlyList<ApiKeyEntry> Removed() =>
    [
        new("environment-openrouter", "OPENROUTER_API_KEY", "sk-or-abcdef",
            ApiKeySource.Environment, false, LlmProvider.OpenRouter)
    ];
}
