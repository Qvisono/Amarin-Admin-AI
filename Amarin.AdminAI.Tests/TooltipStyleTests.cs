using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Почти все подсказки в программе присвоены обычной строкой, и до неявного стиля Windows
/// рисовала под них свой жёлто-белый прямоугольник системным шрифтом.
/// </summary>
/// <remarks>
/// Две подсказки сделаны вручную и задают свой <c>Template</c> прямо на объекте: предупреждение
/// о правах в шапке окна и разбивка цены над ответом. Локальное значение сильнее сеттера стиля,
/// поэтому их неявный стиль не трогает — но проверить это надо, а не надеяться: обратное
/// заметили бы только глазами и только случайно.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class TooltipStyleTests
{
    private readonly WpfFixture _wpf;

    public TooltipStyleTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    /// <summary>Подсказка, какую WPF заводит вокруг присвоенной строки.</summary>
    private static ToolTip FromString(FrameworkElement host, string text)
    {
        var probe = new Border { ToolTip = text };
        ((Panel)host.FindName("MessagesPanel")).Children.Add(probe);
        try
        {
            probe.UpdateLayout();
            var tip = new ToolTip { Content = text, PlacementTarget = probe };

            // Стиль ищется по дереву от хозяина: Resources.xaml подмешан в ресурсы окна.
            tip.Style = (Style)host.FindResource(typeof(ToolTip));
            tip.ApplyTemplate();
            return tip;
        }
        finally
        {
            ((Panel)host.FindName("MessagesPanel")).Children.Remove(probe);
        }
    }

    [Fact]
    public void A_plain_string_tooltip_gets_the_app_card()
    {
        var (hasTemplate, background, maxWidth) = _wpf.Ui.Invoke(() =>
        {
            var tip = FromString(Window(), "проба");
            return (tip.Template is not null,
                    tip.Background as SolidColorBrush,
                    tip.MaxWidth);
        });

        Assert.True(hasTemplate);

        // Фон рисует Border внутри шаблона; сама подсказка прозрачна, иначе под скруглением
        // осталась бы прямоугольная подложка.
        Assert.Equal(Colors.Transparent, background!.Color);
        Assert.Equal(320, maxWidth);
    }

    /// <summary>
    /// Длинная строка обязана переноситься: подсказки у файлов и ссылок несут путь целиком.
    /// </summary>
    [Fact]
    public void A_long_string_wraps_instead_of_running_off_the_screen()
    {
        var width = _wpf.Ui.Invoke(() =>
        {
            var tip = FromString(
                Window(),
                string.Join(" ", Enumerable.Repeat("довольно-длинное-слово", 12)));
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return tip.DesiredSize.Width;
        });

        // MaxWidth 320 плюс поля под тень; без переноса строка ушла бы на тысячи.
        Assert.InRange(width, 100, 360);
    }

    [Fact]
    public void The_administrator_warning_keeps_its_own_chrome()
    {
        var (own, styled) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var warn = (FrameworkElement)window.FindName("Warn");
            var tip = (ToolTip)warn.ToolTip;
            return (tip.Template, (ControlTemplate)((Style)window.FindResource(typeof(ToolTip)))
                .Setters.OfType<Setter>()
                .Single(s => s.Property == Control.TemplateProperty)
                .Value);
        });

        Assert.NotSame(styled, own);
    }

    [Fact]
    public void The_cost_breakdown_keeps_its_own_chrome()
    {
        var (own, styled) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var message = new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "tooltip-style-probe",
                CreatedAt = DateTime.Now,
                Cost = new VeniceCost { Usd = 0.02m, HasData = true },
                Status = AssistantStatus.Complete
            };
            var view = ChatMessageViews.CreateAssistant(window, message, new MessageActions());
            return ((view.CostChip.ToolTip as ToolTip)?.Template,
                    (ControlTemplate)((Style)window.FindResource(typeof(ToolTip)))
                        .Setters.OfType<Setter>()
                        .Single(s => s.Property == Control.TemplateProperty)
                        .Value);
        });

        Assert.NotNull(own);
        Assert.NotSame(styled, own);
    }

    /// <summary>
    /// Снимок карточки во временную папку — так же, как проверялись формулы и углы окна.
    /// Ловит то, что числами не поймать: пустой шаблон рисует пустой прямоугольник.
    /// </summary>
    [Fact]
    public void The_card_actually_renders_something()
    {
        var opaque = _wpf.Ui.Invoke(() =>
        {
            var tip = FromString(Window(), "проба подсказки");
            tip.Measure(new Size(400, 200));
            tip.Arrange(new Rect(tip.DesiredSize));
            tip.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(tip.DesiredSize.Width),
                (int)Math.Ceiling(tip.DesiredSize.Height),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(tip);

            var pixels = new int[bitmap.PixelWidth * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            return pixels.Count(p => (uint)p >> 24 > 16);
        });

        Assert.True(opaque > 200, $"нарисовано всего {opaque} непрозрачных точек");
    }

    [Fact]
    public void The_delay_and_duration_are_set_once_for_the_whole_program()
    {
        var (delay, duration) = _wpf.Ui.Invoke(() =>
        {
            ToolTipDefaults.Apply();
            var probe = new Border();
            return (ToolTipService.GetInitialShowDelay(probe), ToolTipService.GetShowDuration(probe));
        });

        Assert.Equal(220, delay);
        Assert.Equal(20000, duration);
    }
}
