using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Значки меню (1.35.0): у меню сортировки чатов значок у каждого пункта, а галочка выбранного —
/// справа; у меню Розы значки берутся из своего словаря, который читается не при запуске окна.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class MenuIconTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-menu-icons-" + Guid.NewGuid().ToString("N"));

    public MenuIconTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Every_sort_and_new_item_has_an_icon_and_the_check_sits_on_the_right()
    {
        var (icons, checks) = _wpf.Ui.Invoke(() =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            var window = new MainWindow
            {
                Width = 1100,
                Height = 800,
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            window.AttachServices(services);
            window.Show();
            try
            {
                typeof(MainWindow).GetMethod("ListOptionsButton_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [window, new RoutedEventArgs()]);
                var menu = OpenMenus().Single();
                var items = menu.Items.OfType<MenuItem>().Where(item => item.IsHitTestVisible && item.Focusable).ToList();
                var withIcons = items.Count(item => MenuIcon.GetData(item) is not null);

                // Галочка — в колонке справа от подписи, а не перед ней.
                var marks = items.Select(item => item.Header).OfType<Grid>()
                    .Select(grid => grid.Children.OfType<TextBlock>().Single(text => text.Text is "✓" or ""))
                    .Select(mark => (Grid.GetColumn(mark), mark.Text))
                    .ToList();
                menu.IsOpen = false;
                return (withIcons, marks);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(6, icons);
        Assert.Equal(4, checks.Count);
        Assert.All(checks, mark => Assert.Equal(2, mark.Item1));
        Assert.Single(checks, mark => mark.Text == "✓");
    }

    /// <summary>
    /// Все значки, которые называет меню Розы, есть в его словаре и разбираются в геометрию — по тому
    /// же адресу с именем сборки, что и в программе.
    /// </summary>
    [Fact]
    public void The_fox_menu_icons_exist_in_their_own_dictionary()
    {
        var source = File.ReadAllText(Path.Combine(SourceTree.RepositoryRoot, "Amarin Admin AI", "UI", "MainWindow.FoxHost.cs"));
        var keys = Regex.Matches(source, @"""(FoxIcon\.[A-Za-z]+)""").Select(match => match.Groups[1].Value).Distinct().ToList();
        Assert.True(keys.Count >= 12, $"значков в меню всего {keys.Count}");

        var missing = _wpf.Ui.Invoke(() =>
        {
            var uri = new Uri("pack://application:,,,/" + Uri.EscapeDataString(typeof(MainWindow).Assembly.GetName().Name ?? "") +
                              ";component/UI/FoxIcons.xaml", UriKind.Absolute);
            var icons = new ResourceDictionary { Source = uri };
            return keys.Where(key => icons[key] is not Geometry).ToList();
        });

        Assert.Empty(missing);
    }

    /// <summary>Словарь значков Розы не подключён к окну: до пятой секунды от Розы ничего не грузится.</summary>
    [Fact]
    public void The_fox_icons_are_not_merged_into_the_window()
    {
        var xaml = File.ReadAllText(Path.Combine(SourceTree.RepositoryRoot, "Amarin Admin AI", "UI", "MainWindow.xaml"));
        var resources = File.ReadAllText(Path.Combine(SourceTree.RepositoryRoot, "Amarin Admin AI", "UI", "Resources.xaml"));
        Assert.DoesNotContain("FoxIcons.xaml", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FoxIcons.xaml", resources, StringComparison.Ordinal);
    }

    private static List<ContextMenu> OpenMenus() =>
        [.. PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
            .Select(source => source.RootVisual)
            .OfType<DependencyObject>()
            .SelectMany(root => root is ContextMenu own ? [own] : Descendants<ContextMenu>(root))
            .Where(menu => menu.IsOpen)];

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
