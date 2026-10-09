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
/// Кадры ведутся через <c>SmoothScroll.PanScrollFrame</c> с подставленной мышью: положение мыши
/// в поднятом событии и в <c>Mouse.GetPosition</c> — у настоящего курсора, а окно теста стоит
/// там, где его поставила система.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class PanScrollTests
{
    private readonly WpfFixture _wpf;

    public PanScrollTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Speed_is_zero_near_the_mark_and_grows_with_distance_in_its_direction()
    {
        Assert.Equal(0, SmoothScroll.PanScrollSpeed(0));
        Assert.Equal(0, SmoothScroll.PanScrollSpeed(SmoothScroll.PanDeadZone));
        Assert.Equal(0, SmoothScroll.PanScrollSpeed(-SmoothScroll.PanDeadZone));

        var near = SmoothScroll.PanScrollSpeed(40);
        var far = SmoothScroll.PanScrollSpeed(200);
        Assert.True(near > 0);
        Assert.True(far > near * 3, $"{near} → {far}");
        Assert.Equal(-near, SmoothScroll.PanScrollSpeed(-40));

        // Потолок — только защита от бесконечности, и на очень большом удалении он одинаков.
        Assert.Equal(SmoothScroll.PanScrollSpeed(5000), SmoothScroll.PanScrollSpeed(20000));
    }

    [Fact]
    public void Near_the_mark_it_reads_line_by_line_and_far_away_it_flies()
    {
        // В паре сантиметров от метки — несколько строк в секунду; в полуэкране — «безумная»
        // скорость, десятки тысяч точек в секунду (до 1.33.0 потолок был 7000).
        Assert.InRange(SmoothScroll.PanScrollSpeed(30), 20, 400);
        Assert.InRange(SmoothScroll.PanScrollSpeed(150), 1000, 4000);

        var rushStart = SmoothScroll.PanDeadZone + SmoothScroll.PanRushStart;
        Assert.True(SmoothScroll.PanScrollSpeed(rushStart + 240) > SmoothScroll.PanScrollSpeed(rushStart) * 5);
        Assert.Equal(SmoothScroll.PanMaxSpeed, SmoothScroll.PanScrollSpeed(600));

        var previous = 0.0;
        for (var offset = 0; offset <= 800; offset += 10)
        {
            var speed = SmoothScroll.PanScrollSpeed(offset);
            Assert.True(speed >= previous, $"speed dropped at {offset}");
            previous = speed;
        }
    }

    [Fact]
    public void A_slow_frame_does_not_cut_the_speed()
    {
        // На тяжёлом чате кадры реже 30 в секунду: путь за кадр — по его настоящему времени.
        var moved = WithPage(200_000, (host, page) =>
        {
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            SmoothScroll.PanScrollFrame(page, new Point(100, 300), 0.08);
            host.UpdateLayout();
            return page.VerticalOffset;
        });

        Assert.True(moved > SmoothScroll.PanScrollSpeed(200) * 0.07, moved.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Resting_inside_the_dead_zone_is_not_motion()
    {
        var (resting, moving) = WithPage(3000, (_, page) =>
        {
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            SmoothScroll.PanScrollFrame(page, new Point(100, 104), 1.0 / 60);
            var still = SmoothScroll.GetIsInMotion(page);
            SmoothScroll.PanScrollFrame(page, new Point(100, 180), 1.0 / 60);
            return (still, SmoothScroll.GetIsInMotion(page));
        });

        Assert.False(resting);
        Assert.True(moving);
    }

    [Fact]
    public void Moving_below_the_mark_scrolls_down_and_above_it_scrolls_back_up()
    {
        var (down, back) = WithPage(3000, (host, page) =>
        {
            Assert.True(SmoothScroll.BeginPanScroll(page, new Point(100, 100)));
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
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
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
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            Frames(page, new Point(100, 290), 400);
            host.UpdateLayout();
            return (page.VerticalOffset, page.ScrollableHeight);
        });

        Assert.Equal(max, offset, 1);
    }

    [Fact]
    public void A_list_with_nothing_to_scroll_does_not_start_it()
    {
        var started = WithPage(100, (_, page) => SmoothScroll.BeginPanScroll(page, new Point(50, 50)));

        Assert.False(started);
    }

    [Fact]
    public void While_it_runs_the_list_counts_as_moving_and_shows_the_mark()
    {
        var (moving, animating, marker, movingAfter, markerAfter) = WithPage(3000, (_, page) =>
        {
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            var during = (SmoothScroll.GetIsInMotion(page), SmoothScroll.IsAnimating(page), SmoothScroll.PanScrollMarkerOf(page) is not null);
            SmoothScroll.EndPanScroll(page);
            return (during.Item1, during.Item2, during.Item3, SmoothScroll.GetIsInMotion(page), SmoothScroll.PanScrollMarkerOf(page) is not null);
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
            SmoothScroll.BeginPanScroll(page, new Point(100, 100), held: true);
            SmoothScroll.ReleasePanScrollButton(page);
            var clicked = SmoothScroll.IsPanScrolling(page);
            SmoothScroll.EndPanScroll(page);

            SmoothScroll.BeginPanScroll(page, new Point(100, 100), held: true);
            Frames(page, new Point(100, 180), 3);
            SmoothScroll.ReleasePanScrollButton(page);
            return (clicked, SmoothScroll.IsPanScrolling(page));
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
            var began = SmoothScroll.IsPanScrolling(page);

            var left = Press(MouseButton.Left);
            inner.RaiseEvent(left);
            return (began, !SmoothScroll.IsPanScrolling(page), left.Handled);
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
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            host.RaiseEvent(escape);
            return (!SmoothScroll.IsPanScrolling(page), escape.Handled);
        });

        Assert.True(ended);
        Assert.True(handled);
    }

    [Fact]
    public void Escape_reaches_the_pan_before_the_windows_own_handler()
    {
        // Главное окно своим PreviewKeyDown останавливает Esc-ом ответ. Подписка автопрокрутки
        // на окно шла после него, и Esc под меткой останавливал ответ, а прокрутка шла дальше.
        var (ended, windowSawIt) = WithPage(3000, (host, page) =>
        {
            var seen = false;
            host.PreviewKeyDown += (_, e) =>
            {
                seen = true;
                e.Handled = true;
            };

            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            host.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Escape)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            return (!SmoothScroll.IsPanScrolling(page), seen);
        });

        Assert.True(ended);
        Assert.False(windowSawIt);
    }

    [Fact]
    public void Another_key_ends_it_and_still_does_its_own_job()
    {
        var (ended, windowSawIt) = WithPage(3000, (host, page) =>
        {
            var seen = false;
            host.PreviewKeyDown += (_, _) => seen = true;

            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            host.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(host)!, 0, Key.Tab)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            });
            return (!SmoothScroll.IsPanScrolling(page), seen);
        });

        Assert.True(ended);
        Assert.True(windowSawIt);
    }

    [Fact]
    public void A_right_click_ends_it_without_opening_a_context_menu()
    {
        var (ended, releaseSwallowed) = WithPage(3000, (_, page) =>
        {
            var inner = (FrameworkElement)((StackPanel)page.Content).Children[0];
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));

            inner.RaiseEvent(Press(MouseButton.Right));
            var release = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseUpEvent };
            inner.RaiseEvent(release);
            return (!SmoothScroll.IsPanScrolling(page), release.Handled);
        });

        Assert.True(ended);
        Assert.True(releaseSwallowed);
    }

    [Fact]
    public void The_wheel_ends_it()
    {
        var ended = WithPage(3000, (_, page) =>
        {
            var inner = (FrameworkElement)((StackPanel)page.Content).Children[0];
            SmoothScroll.BeginPanScroll(page, new Point(100, 100));
            inner.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            return !SmoothScroll.IsPanScrolling(page);
        });

        Assert.True(ended);
    }

    [Fact]
    public void The_cursor_that_was_there_before_comes_back()
    {
        var restored = WithPage(3000, (_, page) =>
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                SmoothScroll.BeginPanScroll(page, new Point(100, 100));
                SmoothScroll.EndPanScroll(page);
                return Mouse.OverrideCursor == Cursors.Wait;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        });

        Assert.True(restored);
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
                SmoothScroll.GetPanScroll((ScrollViewer)main.FindSetting("ChatScrollViewer")!),
                SmoothScroll.GetPanScroll((ScrollViewer)main.FindSetting("SideBarScrollViewer")!),
                names.All(name => main.FindSetting(name) is ScrollViewer viewer && SmoothScroll.GetPanScroll(viewer)) &&
                new SettingsKeyPage().FindName("KeyPageScroll") is ScrollViewer key && SmoothScroll.GetPanScroll(key) &&
                new SettingsSecurityPage().FindName("PageScroll") is ScrollViewer security && SmoothScroll.GetPanScroll(security));
        });

        Assert.True(chat);
        Assert.True(list);
        Assert.True(pages);
    }

    private static void Frames(ScrollViewer page, Point pointer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            SmoothScroll.PanScrollFrame(page, pointer, 1.0 / 60);
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
            SmoothScroll.SetPanScroll(page, true);
            try
            {
                host.Show();
                host.UpdateLayout();
                return body(host, page);
            }
            finally
            {
                SmoothScroll.EndPanScroll(page);
                SmoothScroll.SetIsEnabled(page, false);
                host.Close();
            }
        });
}
