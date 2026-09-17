using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Страница «Info» и плашка версии с ссылкой на репозиторий внизу навигации настроек.
/// </summary>
/// <remarks>
/// Всё меряется на живом окне и без его показа: запускать программу ради проверки вредно —
/// окно вылезет поверх работы человека.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class SettingsInfoPageTests
{
    private readonly WpfFixture _wpf;

    public SettingsInfoPageTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Choosing_Info_in_the_navigation_shows_the_guide_and_hides_the_rest()
    {
        var (visible, isInfo) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;

            var nav = (RadioButton)window.FindName("NavInfo")!;
            nav.IsChecked = true;
            window.UpdateLayout();

            // Все страницы настроек лежат в одной ячейке и разбираются триггерами по IsChecked:
            // сломанный триггер показал бы две страницы одну поверх другой, а не ни одной.
            var page = Find<SettingsInfoPage>(overlay)!;
            var host = (Panel)VisualTreeHelper.GetParent(page);
            var shown = host.Children
                .OfType<FrameworkElement>()
                .Where(child => child is ScrollViewer or SettingsInfoPage)
                .Where(child => child.Visibility == Visibility.Visible)
                .ToList();

            var result = (shown.Count, shown.Count == 1 && shown[0] is SettingsInfoPage);

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;
            return result;
        });

        Assert.Equal(1, visible);
        Assert.True(isInfo, "видна не страница Info");
    }

    [Fact]
    public void The_version_card_stays_inside_the_navigation_column()
    {
        // При найденном обновлении MainWindow.Updates.cs переписывает номер версии длинной
        // строкой. Раньше это был одинокий TextBlock у левого края и вылезти он не мог;
        // центрированная карточка без MaxWidth и переноса уезжает под страницу настроек,
        // а увидеть это глазами можно только тогда, когда обновление и правда есть.
        var (cardWidth, columnWidth) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;

            var version = (TextBlock)window.FindName("SettingsVersionText")!;
            var card = (FrameworkElement)window.FindName("SettingsAboutCard")!;
            var restore = version.Text;

            version.Text = Loc.Format("S.Updates.SidebarBadge", "1.19.5");
            window.UpdateLayout();
            var measured = card.ActualWidth;

            version.Text = restore;
            overlay.Visibility = Visibility.Collapsed;

            // 170 — ширина колонки навигации в разметке настроек.
            return (measured, 170.0);
        });

        Assert.True(
            cardWidth > 0 && cardWidth <= columnWidth,
            $"плашка версии шириной {cardWidth} не помещается в колонку {columnWidth}");
    }

    [Fact]
    public void The_github_link_points_at_the_same_repository_as_the_updater()
    {
        // Две правды об адресе репозитория разошлись бы молча: ссылка вела бы в никуда, а
        // обновления продолжали бы работать. Браузер в тесте не открываем.
        Assert.StartsWith("https://github.com/", UpdateChecker.RepositoryUrl, StringComparison.Ordinal);
        Assert.StartsWith(UpdateChecker.RepositoryUrl, UpdateChecker.ReleasesPageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screenshot_that_has_not_been_taken_yet_falls_back_to_a_placeholder()
    {
        // Картинки гайда добавляются по мере съёмки. Обращение к отсутствующему ресурсу
        // роняет загрузку всей страницы, поэтому GuideShot ищет файл сам.
        var (shot, missing, placeholder) = _wpf.Ui.Invoke(() =>
        {
            var control = new GuideShot { FileName = "no-such-screenshot-in-the-assembly.png" };
            control.Measure(new Size(500, 500));
            control.Arrange(new Rect(0, 0, 500, 500));

            var image = FindByName<Image>(control, "Shot")!;
            var frame = FindByName<Border>(control, "Missing")!;
            return (image.Visibility, frame.Visibility, control.Placeholder);
        });

        Assert.Equal(Visibility.Collapsed, shot);
        Assert.Equal(Visibility.Visible, missing);
        Assert.Contains("no-such-screenshot-in-the-assembly.png", placeholder, StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_renders_without_throwing()
    {
        // Страница длинная и собрана из стилей, скопированных из настроек: опечатка в ключе
        // стиля видна только при отрисовке, а не при сборке.
        var height = _wpf.Ui.Invoke(() =>
        {
            var page = new SettingsInfoPage();
            page.Measure(new Size(520, 460));
            page.Arrange(new Rect(0, 0, 520, 460));
            page.UpdateLayout();
            return page.DesiredSize.Height;
        });

        Assert.True(height > 0);
    }

    private static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            var deeper = Find<T>(child);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }

    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name)
            {
                return match;
            }

            var deeper = FindByName<T>(child, name);
            if (deeper is not null)
            {
                return deeper;
            }
        }

        return null;
    }
}
