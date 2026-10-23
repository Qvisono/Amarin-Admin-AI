using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Блок <c>```chart</c> в ответе рисуется диаграммой, недописанный и негодный остаются кодом, а
/// подсказка под курсором попадает в нужный столбец.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatChartViewTests
{
    private readonly WpfFixture _wpf;

    public ChatChartViewTests(WpfFixture wpf) => _wpf = wpf;

    private const string Bar = """{"type":"bar","title":"Продажи по месяцам","labels":["Январь","Февраль","Март"],"series":[{"name":"Север","values":[120,80,150]},{"name":"Юг","values":[90,null,60]}]}""";

    private const string Pie = """{"type":"pie","title":"Доли","labels":["Север","Юг","Запад"],"series":[{"name":"Сумма","values":[50,30,20]}]}""";

    private IReadOnlyList<Block> Render(string markdown) => _wpf.Ui.Invoke(() =>
    {
        var window = Application.Current.Windows.OfType<MainWindow>().Single();
        var view = ChatMessageViews.CreateAssistant(
            window,
            new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "c1",
                Text = markdown,
                CreatedAt = DateTime.Now,
                Status = AssistantStatus.Complete
            });
        return (IReadOnlyList<Block>)view.Body.Document.Blocks.ToList();
    });

    private bool HasChart(IReadOnlyList<Block> blocks) => _wpf.Ui.Invoke(() =>
        blocks.OfType<BlockUIContainer>().Any(container => container.Child is Border { Child: ChatChartView }));

    [Fact]
    public void A_chart_block_becomes_a_chart()
    {
        Assert.True(HasChart(Render("Итоги:\n\n```chart\n" + Bar + "\n```\n\nКонец.")));
    }

    [Theory]
    [InlineData("```chart\n{\"type\":\"bar\",\"labels\":[\"a\"],\"series\":[{\"values\":[1")]
    [InlineData("```chart\nне диаграмма\n```")]
    [InlineData("```json\n{\"type\":\"bar\",\"labels\":[\"a\"],\"series\":[{\"values\":[1]}]}\n```")]
    public void An_unfinished_or_foreign_block_stays_code(string markdown)
    {
        var blocks = Render(markdown);

        Assert.False(HasChart(blocks));
        Assert.NotEmpty(blocks);
    }

    [Fact]
    public void Hovering_a_column_picks_its_label_and_a_pie_slice_its_share()
    {
        var (bar, pie) = _wpf.Ui.Invoke(() =>
        {
            static ChatChartView View(string json)
            {
                Assert.True(ChartSpec.TryParse(json, out var spec));
                var card = Place(ChatChartView.Create(spec));
                return (ChatChartView)((Border)card).Child;
            }

            var barView = View(Bar);
            var pieView = View(Pie);
            return (barView, pieView);
        });

        var hits = _wpf.Ui.Invoke(() => new[]
        {
            bar.HitIndex(new Point(bar.ActualWidth - 5, 100)),
            bar.HitIndex(new Point(2, 100)),
            pie.HitIndex(new Point(110, 60)),
            pie.HitIndex(new Point(pie.ActualWidth - 2, 60))
        });

        Assert.Equal([2, -1, 0, -1], hits);
    }

    [Fact]
    public void The_chart_draws_in_both_themes()
    {
        var folder = Environment.GetEnvironmentVariable("AMARIN_CHART_SHOTS");
        _wpf.Ui.Invoke(() =>
        {
            foreach (var json in (string[])[Bar, Pie])
            {
                Assert.True(ChartSpec.TryParse(json, out var spec));
                var card = Place(ChatChartView.Create(spec));
                var view = (ChatChartView)((Border)card).Child;
                view.HoverForShot(0);
                card.UpdateLayout();
                Assert.True(card.ActualHeight > 150, $"height {card.ActualHeight}");

                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(card.ActualWidth), (int)Math.Ceiling(card.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(card);
                if (folder is { Length: > 0 })
                {
                    Directory.CreateDirectory(folder);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(folder, "chart-" + spec.Type + ".png"));
                    encoder.Save(file);
                }
            }

            return 0;
        });
    }

    /// <summary>Кладёт карточку в дерево главного окна на время проверки: кисти темы берутся оттуда.</summary>
    private static FrameworkElement Place(FrameworkElement card)
    {
        var window = Application.Current.Windows.OfType<MainWindow>().Single();
        var host = new Border { Width = 560, Child = card };
        host.Resources.MergedDictionaries.Add(window.Resources);
        host.Measure(new Size(560, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        return card;
    }
}
