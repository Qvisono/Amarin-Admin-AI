using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Плавная прокрутка на страницах настроек и то, кому достаётся колесо.
/// </summary>
/// <remarks>
/// Страница настроек — первый в программе плавный скролл, внутри которого есть что прокручивать
/// отдельно: список разрешённых источников, поля промптов, раскрытые выпадашки. Хук слушает
/// <c>PreviewMouseWheel</c>, а тот туннелирующий — идёт сверху вниз, — и страница успевала забрать
/// колесо раньше всех них. Поэтому события здесь поднимаются с самого глубокого элемента, как их
/// поднимает мышь: колесо, отправленное сразу во внутренний список, этой ошибки не поймало бы.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class SmoothScrollTests
{
    private readonly WpfFixture _wpf;

    public SmoothScrollTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Every_settings_page_scrolls_smoothly()
    {
        var enabled = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            string[] names =
            [
                "SideBarScrollViewer",
                "ChatScrollViewer",
                "AppearancePageScroll",
                "BehaviorPageScroll",
                "CustomizePageScroll",
                "DataPageScroll",
                "AllowedDomainsScroll"
            ];

            return names
                .Where(name => window.FindName(name) is ScrollViewer viewer && SmoothScroll.GetIsEnabled(viewer))
                .ToArray();
        });

        Assert.Equal(7, enabled.Length);
    }

    [Fact]
    public void The_info_page_scrolls_smoothly_too()
    {
        // Отдельный UserControl со своим кодом — включается у себя, а не из главного окна.
        var enabled = _wpf.Ui.Invoke(() =>
        {
            var page = new SettingsInfoPage();
            return page.FindName("InfoPageScroll") is ScrollViewer viewer && SmoothScroll.GetIsEnabled(viewer);
        });

        Assert.True(enabled);
    }

    [Fact]
    public void The_prompt_boxes_scroll_smoothly()
    {
        // Поля промптов прокручиваются своим ScrollViewer из шаблона, и страница ему уступает —
        // а значит, плавность надо включать там же, иначе текст листается рывками.
        var (enabled, animating) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var box = (TextBox)window.FindName("MainPromptTextBox")!;
            var saved = box.Text;
            try
            {
                box.ApplyTemplate();
                var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box)!;

                box.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 60).Select(i => "строка " + i));
                box.UpdateLayout();

                var took = RaiseWheel(host);
                return (SmoothScroll.GetIsEnabled(host), took && SmoothScroll.IsAnimating(host));
            }
            finally
            {
                box.Text = saved;
            }
        });

        Assert.True(enabled, "у поля промпта должен быть плавный скролл");
        Assert.True(animating, "колесо над полем должно уходить в инерцию, а не в рывок");
    }

    [Fact]
    public void The_dropdowns_in_the_settings_scroll_smoothly()
    {
        // Список выпадашки живёт в шаблоне DarkComboBox — по имени его не достать, поэтому
        // плавность включена прямо там, и разом у всех выпадашек настроек.
        var enabled = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var combo = (ComboBox)window.FindName("UiScaleComboBox")!;

            // Раскрывать выпадашку не надо, и лучше не надо: раскрытая забирает мышь на себя,
            // а окно здесь одно на всю коллекцию тестов. Popup со своим содержимым создаётся
            // вместе с шаблоном, и свойство на нём уже стоит.
            combo.ApplyTemplate();
            return FindChild<Popup>(combo) is { Child: { } child } &&
                   FindChild<ScrollViewer>(child) is { } viewer &&
                   SmoothScroll.GetIsEnabled(viewer);
        });

        Assert.True(enabled);
    }

    [Fact]
    public void A_nested_list_gets_the_wheel_before_the_page_does()
    {
        // Ровно та ошибка: колесо над списком разрешённых источников крутило страницу за ним.
        var (inner, outer) = _wpf.Ui.Invoke(() => Wheel(atBottom: false, smoothInner: true));

        Assert.True(inner, "колесо должен забрать вложенный список");
        Assert.False(outer, "страница в этот момент стоять на месте");
    }

    [Fact]
    public void A_nested_list_hands_the_wheel_over_once_it_runs_out()
    {
        var (inner, outer) = _wpf.Ui.Invoke(() => Wheel(atBottom: true, smoothInner: true));

        Assert.False(inner);
        Assert.True(outer, "докрутив список до низа, колесо обязано уйти странице за ним");
    }

    [Fact]
    public void A_nested_scroller_of_its_own_keeps_the_wheel_too()
    {
        // Вложенному скроллу без плавности страница тоже обязана уступить — и не прокрутиться
        // сама, и не проглотить событие, а пропустить его дальше, к обычному WPF.
        var (handled, outer) = _wpf.Ui.Invoke(() =>
        {
            var (took, animating) = Wheel(atBottom: false, smoothInner: false);
            return (took, animating);
        });

        Assert.False(handled, "событие должно дойти до обычного скролла внутри");
        Assert.False(outer);
    }

    [Fact]
    public void A_dropdown_over_the_page_keeps_the_wheel()
    {
        // Выпадашка живёт в своём окне, но маршрут события идёт через страницу под ней: без
        // отдельной проверки страница забирала колесо у раскрытого списка моделей и масштаба.
        var (sawIt, animating) = _wpf.Ui.Invoke(() =>
        {
            var page = NewSmoothPage(out var host);
            var seen = 0;
            page.PreviewMouseWheel += (_, _) => seen++;

            var list = new ScrollViewer
            {
                Height = 80,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 600 }
            };
            // Popup объявляют внутри страницы, как в разметке: тогда маршрут события из него и
            // проходит через неё — то самое, из-за чего страница забирала колесо у выпадашки.
            var popup = new Popup { PlacementTarget = page, Child = list, Width = 200, Height = 80 };
            ((StackPanel)page.Content).Children.Insert(0, popup);

            try
            {
                host.Show();
                host.UpdateLayout();
                popup.IsOpen = true;
                host.UpdateLayout();

                RaiseWheel((Border)list.Content);
                return (seen, SmoothScroll.IsAnimating(page));
            }
            finally
            {
                popup.IsOpen = false;
                SmoothScroll.SetIsEnabled(page, false);
                host.Close();
            }
        });

        Assert.Equal(1, sawIt);
        Assert.False(animating, "страница под выпадашкой крутиться не должна");
    }

    /// <summary>
    /// Одна зарубка колеса вниз с самого глубокого элемента внутреннего списка. Возвращает, кто
    /// из двух скроллов после неё поехал.
    /// </summary>
    /// <remarks>
    /// Каждый вызов собирает дерево заново: после первой же зарубки хук уходит в инерцию, а во
    /// время неё край намеренно краем не считается — там работает резинка.
    /// </remarks>
    private static (bool Inner, bool Outer) Wheel(bool atBottom, bool smoothInner)
    {
        var page = NewSmoothPage(out var host);
        var inner = new ScrollViewer
        {
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Height = 600 }
        };
        ((StackPanel)page.Content).Children.Insert(0, inner);

        try
        {
            host.Show();
            if (smoothInner)
            {
                SmoothScroll.SetIsEnabled(inner, true);
            }

            host.UpdateLayout();
            inner.ScrollToVerticalOffset(atBottom ? inner.ScrollableHeight : inner.ScrollableHeight / 2);
            host.UpdateLayout();

            var handled = RaiseWheel((Border)inner.Content);
            return smoothInner
                ? (SmoothScroll.IsAnimating(inner), SmoothScroll.IsAnimating(page))
                : (handled, SmoothScroll.IsAnimating(page));
        }
        finally
        {
            // Хук держит CompositionTarget.Rendering, пока идёт инерция: снимаем его руками,
            // иначе он переживёт окно и будет дёргать закрытый ScrollViewer каждый кадр.
            SmoothScroll.SetIsEnabled(inner, false);
            SmoothScroll.SetIsEnabled(page, false);
            host.Close();
        }
    }

    /// <summary>Страница настроек в миниатюре: плавный скролл с высоким содержимым.</summary>
    private static ScrollViewer NewSmoothPage(out Window host)
    {
        var page = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { new Border { Height = 900 } } }
        };

        host = new Window
        {
            Width = 400,
            Height = 300,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            Content = page
        };

        SmoothScroll.SetIsEnabled(page, true);
        return page;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                return found;
            }

            if (FindChild<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>
    /// Колесо вниз с самого глубокого элемента — так событие идёт сверху вниз через всех, кто
    /// над ним, ровно как от настоящей мыши.
    /// </summary>
    private static bool RaiseWheel(UIElement deepest)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        };

        deepest.RaiseEvent(args);
        return args.Handled;
    }
}
