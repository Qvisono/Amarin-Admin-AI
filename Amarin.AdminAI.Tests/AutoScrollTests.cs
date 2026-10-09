using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Автопрокрутка средней кнопкой (1.33.0): метка на месте нажатия, скорость от расстояния до неё,
/// режимы «щелчок» и «держу» и всё, что её заканчивает.
/// </summary>
/// <remarks>
/// Кадры ведутся через <c>SmoothScroll.AutoScrollFrame</c> с подставленной мышью: положение мыши
/// в поднятом событии и в <c>Mouse.GetPosition</c> — у настоящего курсора, а окно теста стоит
/// там, где его поставила система.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class AutoScrollTests
{
    private readonly WpfFixture _wpf;

    public AutoScrollTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Speed_is_zero_near_the_mark_and_grows_with_distance_in_its_direction()
    {
        Assert.Equal(0, SmoothScroll.AutoScrollSpeed(0));
        Assert.Equal(0, SmoothScroll.AutoScrollSpeed(SmoothScroll.AutoDeadZone));
        Assert.Equal(0, SmoothScroll.AutoScrollSpeed(-SmoothScroll.AutoDeadZone));

        var near = SmoothScroll.AutoScrollSpeed(40);
        var far = SmoothScroll.AutoScrollSpeed(200);
        Assert.True(near > 0);
        Assert.True(far > near * 3, $"{near} → {far}");
        Assert.Equal(-near, SmoothScroll.AutoScrollSpeed(-40));

        // Потолок: у края огромного экрана лента не превращается в мельтешение.
        Assert.Equal(SmoothScroll.AutoScrollSpeed(5000), SmoothScroll.AutoScrollSpeed(20000));
    }

    [Fact]
    public void Moving_below_the_mark_scrolls_down_and_above_it_scrolls_back_up()
    {
        var (down, back) = WithPage(3000, (host, page) =>
        {
            Assert.True(SmoothScroll.BeginAutoScroll(page, new Point(100, 100)));
            Frames(page, new Point(100, 220), 30);
            host.UpdateLayout();
            var afterDown = page.VerticalOffset;

            Frames(page, new Point(100, 20), 10);
            host.UpdateLayout();
            return (afterDown, page.VerticalOffset);
        });

        Assert.True(down > 100, $"down {down}");
        Assert.True(back < down, $"back {back} after {down}");
    }

    [Fact]
    public void Within_the_dead_zone_nothing_moves()
    {
        var offset = WithPage(3000, (host, page) =>
        {
            SmoothScroll.BeginAutoScroll(page, new Point(100, 100));
            Frames(page, new Point(103, 108), 30);
            host.UpdateLayout();
            return page.VerticalOffset;
        });

        Assert.Equal(0, offset);
    }

    [Fact]
    public void The_edge_is_an_edge_without_a_rubber_band()
    {
        var (offset, max) = WithPage(1200, (host, page) =>
        {
            SmoothScroll.BeginAutoScroll(page, new Point(100, 100));
            Frames(page, new Point(100, 290), 400);
            host.UpdateLayout();
            return (page.VerticalOffset, page.ScrollableHeight);
        });

        Assert.Equal(max, offset, 1);
    }

    [Fact]
    public void A_list_with_nothing_to_scroll_does_not_start_it()
    {
        var started = WithPage(100, (_, page) => SmoothScroll.BeginAutoScroll(page, new Point(50, 50)));

        Assert.False(started);
    }

    [Fact]
    public void While_it_runs_the_list_counts_as_moving_and_shows_the_mark()
    {
        var (moving, animating, marker, movingAfter, markerAfter) = WithPage(3000, (_, page) =>
        {
            SmoothScroll.BeginAutoScroll(page, new Point(100, 100));
            var during = (SmoothScroll.GetIsInMotion(page), SmoothScroll.IsAnimating(page), SmoothScroll.AutoScrollMarkerOf(page) is not null);
            SmoothScroll.EndAutoScroll(page);
            return (during.Item1, during.Item2, during.Item3, SmoothScroll.GetIsInMotion(page), SmoothScroll.AutoScrollMarkerOf(page) is not null);
        });

        Assert.True(moving);
        Assert.True(animating);
        Assert.True(marker);
        Assert.False(movingAfter);
        Assert.False(markerAfter);
    }

    [Fact]
    public void A_quick_click_keeps_it_going_and_a_held_drag_ends_on_release()
    {
        var (afterClick, afterDrag) = WithPage(3000, (_, page) =>
        {
            SmoothScroll.BeginAutoScroll(page, new Point(100, 100), held: true);
            SmoothScroll.ReleaseAutoScrollButton(page);
            var clicked = SmoothScroll.IsAutoScrolling(page);
            SmoothScroll.EndAutoScroll(page);

            SmoothScroll.BeginAutoScroll(page, new Point(100, 100), held: true);
            Frames(page, new Point(100, 180), 3);
            SmoothScroll.ReleaseAutoScrollButton(page);
            return (clicked, SmoothScroll.IsAutoScrolling(page));
        });

        Assert.True(afterClick);
        Assert.False(afterDrag);
    }

    [Fact]
    public void A_middle_press_inside_the_list_starts_it_and_any_next_press_ends_it_unanswered()
    {
        var (started, endedByLeft, leftHandled) = WithPage(3000, (_, page) =>
        {
            var inner = (FrameworkElement)((StackPanel)page.Content).Children[0];
            inner.RaiseEvent(Press(MouseButton.Middle));
            var began = SmoothScroll.IsAutoScrolling(page);

            var left = Press(MouseButton.Left);
            inner.RaiseEvent(left);
            return (began, !SmoothScroll.IsAutoScrolling(page), left.Handled);
        });

        Assert.True(started);
        Assert.True(endedByLeft);
        Assert.True(leftHandled);
    }

    [Fact]
    public void Escape_ends_it_and_goes_no_further()
    {
        var (ended, handled) = WithPage(3000, (host, page) =>
        {
            SmoothScroll.BeginAutoScroll(page, new Point(100, 100));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            host.RaiseEvent(escape);
            return (!SmoothScroll.IsAutoScrolling(page), escape.Handled);
        });

        Assert.True(ended);
        Assert.True(handled);
    }

    [Fact]
    public void The_chat_the_chat_list_and_settings_pages_scroll_with_the_middle_button()
    {
        var (chat, list, pages) = _wpf.Ui.Invoke(() =>
        {
            var main = Application.Current.Windows.OfType<MainWindow>().Single();
            string[] names =
            [
                "AppearancePageScroll",
                "GeneralPageScroll",
                "ProfilePageScroll",
                "ModelsPageScroll",
                "PromptsPageScroll",
                "DataPageScroll",
                "AboutPageScroll"
            ];

            return (
                SmoothScroll.GetAutoScroll((ScrollViewer)main.FindSetting("ChatScrollViewer")!),
                SmoothScroll.GetAutoScroll((ScrollViewer)main.FindSetting("SideBarScrollViewer")!),
                names.All(name => main.FindSetting(name) is ScrollViewer viewer && SmoothScroll.GetAutoScroll(viewer)) &&
                new SettingsKeyPage().FindName("KeyPageScroll") is ScrollViewer key && SmoothScroll.GetAutoScroll(key) &&
                new SettingsSecurityPage().FindName("PageScroll") is ScrollViewer security && SmoothScroll.GetAutoScroll(security));
        });

        Assert.True(chat);
        Assert.True(list);
        Assert.True(pages);
    }

    private static void Frames(ScrollViewer page, Point pointer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            SmoothScroll.AutoScrollFrame(page, pointer, 1.0 / 60);
        }
    }

    private static MouseButtonEventArgs Press(MouseButton button) =>
        new(Mouse.PrimaryDevice, 0, button) { RoutedEvent = UIElement.PreviewMouseDownEvent };

    private T WithPage<T>(double contentHeight, Func<Window, ScrollViewer, T> body) =>
        _wpf.Ui.Invoke(() =>
        {
            var page = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { new Border { Height = contentHeight, Background = System.Windows.Media.Brushes.Transparent } } }
            };

            var host = new Window
            {
                Width = 400,
                Height = 300,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = new AdornerDecorator { Child = page }
            };

            SmoothScroll.SetIsEnabled(page, true);
            SmoothScroll.SetAutoScroll(page, true);
            try
            {
                host.Show();
                host.UpdateLayout();
                return body(host, page);
            }
            finally
            {
                SmoothScroll.EndAutoScroll(page);
                SmoothScroll.SetIsEnabled(page, false);
                host.Close();
            }
        });
}
