using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Навигация настроек: у каждого пункта свой знак и своя плитка, подписи — на языке интерфейса.
/// </summary>
/// <remarks>
/// Знаки и цвета живут в SettingsNavIcons.xaml, и ошибиться там легко незаметно: скопировать
/// соседний путь, взять ту же кисть или цвет, на котором белый знак не виден. Глазами это
/// ловит лист значков UiShotTests (фильтр <c>icons</c>), а здесь — то, что можно посчитать.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SettingsNavIconTests
{
    private readonly WpfFixture _wpf;

    public SettingsNavIconTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    [Fact]
    public void Every_item_has_its_own_mark_on_the_24_grid()
    {
        var marks = _wpf.Ui.Invoke(() => SettingsNavNames.All
            .Select(name => (Name: name, Geometry: ((RadioButton)Window().FindSetting(name)!).Tag as Geometry))
            .Select(item => (item.Name, Data: item.Geometry?.ToString(), Bounds: item.Geometry?.Bounds ?? Rect.Empty))
            .ToList());

        Assert.All(marks, mark => Assert.False(string.IsNullOrEmpty(mark.Data), mark.Name + ": нет знака"));

        // Пустые фигуры по углам держат сетку 24×24: без них Stretch растянул бы знак до краёв
        // плитки, и знаки вышли бы разного размера.
        Assert.All(marks, mark => Assert.Equal(new Rect(0, 0, 24, 24), mark.Bounds));
        Assert.Equal(marks.Count, marks.Select(mark => mark.Data).Distinct().Count());
    }

    [Fact]
    public void Every_tile_has_its_own_colour_and_the_white_mark_reads_on_it()
    {
        var colours = _wpf.Ui.Invoke(() => SettingsNavNames.All
            .Select(name => (name, ((SolidColorBrush)((RadioButton)Window().FindSetting(name)!).Background).Color))
            .ToList());

        Assert.Equal(colours.Count, colours.Select(item => item.Color).Distinct().Count());

        // Знак белый: на светлой плитке он растворялся бы. 3:1 — порог WCAG для значков.
        Assert.All(colours, item => Assert.True(Contrast(Colors.White, item.Color) >= 3.0,
            $"{item.name}: контраст белого знака {Contrast(Colors.White, item.Color):0.00}"));

        // Соседи по колонке различимы по тону, а не только по оттенку одного цвета.
        for (var i = 1; i < colours.Count; i++)
        {
            var (a, b) = (colours[i - 1].Color, colours[i].Color);
            if (Saturation(a) < 0.2 || Saturation(b) < 0.2)
            {
                continue;
            }

            var gap = Math.Abs(Hue(a) - Hue(b));
            gap = Math.Min(gap, 360 - gap);
            Assert.True(gap >= 25, $"{colours[i - 1].name} и {colours[i].name}: тона ближе {gap:0}°");
        }
    }

    [Fact]
    public void Russian_navigation_speaks_russian()
    {
        // Прежде навигация русского интерфейса была английской целиком: GENERAL, Account, Data Controls.
        var labels = _wpf.Ui.Invoke(() =>
        {
            var nav = (Panel)LogicalTreeHelper.GetParent((DependencyObject)Window().FindSetting("NavGeneral")!);
            return nav.Children.OfType<FrameworkElement>()
                .Select(child => child switch
                {
                    RadioButton radio => radio.Content as string,
                    TextBlock text => text.Text,
                    _ => null
                })
                .Where(text => text is not null)
                .ToList();
        });

        Assert.Equal(SettingsNavNames.All.Length + 3, labels.Count);
        Assert.All(labels, label => Assert.DoesNotMatch("[A-Za-z]", label!));
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Saturation(Color c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        return max == 0 ? 0 : (max - min) / max;
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (d == 0)
        {
            return 0;
        }

        var h = max == r ? ((g - b) / d) % 6 : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }
}
