using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;
using ShapePath = System.Windows.Shapes.Path;

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

        // Потолок MaxWidth 320; без переноса строка ушла бы на тысячи.
        Assert.InRange(width, 100, 320);
    }

    /// <summary>Первый Border в дереве шаблона — карточка подсказки.</summary>
    private static T? Descendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit)
            {
                return hit;
            }

            if (Descendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static Border StyledCard(FrameworkElement host)
    {
        var tip = FromString(host, "проба");
        tip.ApplyTemplate();
        return Descendant<Border>(tip)
               ?? throw new InvalidOperationException("в шаблоне подсказки нет карточки");
    }

    /// <summary>Карточка разбивки цены — эталон, к которому приведены остальные плашки.</summary>
    private static (Border Card, ShapePath Arrow) CostCard()
    {
        var stack = (Grid)CostBreakdownTooltip.CreateEmpty().Content;
        return (stack.Children.OfType<Border>().Single(),
                stack.Children.OfType<ShapePath>().Single());
    }

    /// <summary>
    /// Тени у карточки больше нет — и обрезаться нечему.
    /// </summary>
    /// <remarks>
    /// Раньше её рисовал Border внутри шаблона, а поля вокруг держали для неё место. Размытие 16
    /// с отступом 4 выносит тень на ~10.8 пикселя вправо и вниз при запасе 8 и 10, поля входят
    /// в размер подсказки, и окно попапа резало тень прямой линией по правому краю.
    /// </remarks>
    [Fact]
    public void The_card_has_no_shadow_left_to_clip()
    {
        var (effect, margin) = _wpf.Ui.Invoke(() =>
        {
            var card = StyledCard(Window());
            return (card.Effect, card.Margin);
        });

        Assert.Null(effect);
        Assert.Equal(default, margin);
    }

    /// <summary>
    /// Три плашки, которые человек видит рядом, обязаны быть одной формы. Форма описана дважды —
    /// разметкой в <c>Resources.xaml</c> и кодом в <c>CostBreakdownTooltip</c>, переиспользовать
    /// одно в другом нечем, — поэтому совпадение и проверяется, а не подразумевается.
    /// </summary>
    [Fact]
    public void The_plain_card_has_the_same_shape_as_the_price_card()
    {
        // Всё читается внутри вызова: снаружи свойства DependencyObject недоступны — потоку,
        // из которого их спросили, объект не принадлежит.
        var (styled, cost) = _wpf.Ui.Invoke(() =>
        {
            var tip = FromString(Window(), "проба");
            tip.ApplyTemplate();
            var card = Descendant<Border>(tip)
                       ?? throw new InvalidOperationException("в шаблоне подсказки нет карточки");
            var arrow = Descendant<ShapePath>(tip)
                        ?? throw new InvalidOperationException("в шаблоне подсказки нет стрелки");
            var (price, priceArrow) = CostCard();
            return (Shape(card, arrow), Shape(price, priceArrow));
        });

        Assert.Equal(cost, styled);
    }

    /// <summary>Форма плашки одной строкой — так расхождение видно в самом сообщении теста.</summary>
    /// <remarks>
    /// Выравнивание стрелки входит в сравнение не для красоты: подсказка встаёт по центру под
    /// целью, и стрелка, сдвинутая к краю в одной из двух плашек, показывала бы мимо.
    /// </remarks>
    private static string Shape(Border card, ShapePath arrow) =>
        $"radius={card.CornerRadius} padding={card.Padding} border={card.BorderThickness} " +
        $"arrow={arrow.Data} align={arrow.HorizontalAlignment} offset={arrow.Margin}";

    /// <summary>
    /// Карточка занимает окно подсказки целиком.
    /// </summary>
    /// <remarks>
    /// Прозрачной полосы по краям быть не должно: именно она и была тем «обрезанием», которое
    /// человек видел — поля под тень съедали по 8 пикселей слева и справа, а тень в них
    /// не помещалась. Числами это не поймать, поэтому снимок.
    /// </remarks>
    [Fact]
    public void The_card_reaches_the_edges_of_its_window()
    {
        var (left, right) = _wpf.Ui.Invoke(() =>
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

            // Середина карточки по высоте: выше неё строка под стрелку, ниже — скруглённые углы.
            var row = (6 + bitmap.PixelHeight) / 2;
            var start = row * bitmap.PixelWidth;
            return ((uint)pixels[start] >> 24, (uint)pixels[start + bitmap.PixelWidth - 1] >> 24);
        });

        Assert.True(left > 16, $"слева пусто: прозрачность {left}");
        Assert.True(right > 16, $"справа пусто: прозрачность {right}");
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
