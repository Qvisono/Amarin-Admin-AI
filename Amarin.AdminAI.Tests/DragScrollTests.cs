using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Колонку чатов и страницы настроек листают ещё и зажатой кнопкой — той же физикой, что колесо.
/// </summary>
/// <remarks>
/// Жест ведётся через <c>SmoothScroll.ArmDrag</c>/<c>DragTo</c>: положение мыши в поднятом
/// <c>RaiseEvent</c> событии берётся у настоящего курсора, и подставить его нельзя.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class DragScrollTests
{
    private readonly WpfFixture _wpf;

    public DragScrollTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_sidebar_and_every_settings_page_scroll_by_dragging()
    {
        var (window, keyPage, infoPage) = _wpf.Ui.Invoke(() =>
        {
            var main = Application.Current.Windows.OfType<MainWindow>().Single();
            string[] names =
            [
                "SideBarScrollViewer",
                "AppearancePageScroll",
                "BehaviorPageScroll",
                "CustomizePageScroll",
                "DataPageScroll"
            ];

            var onWindow = names.All(name =>
                main.FindName(name) is ScrollViewer viewer && SmoothScroll.GetDragScroll(viewer));
            var key = new SettingsKeyPage().FindName("KeyPageScroll") is ScrollViewer k && SmoothScroll.GetDragScroll(k);
            var info = new SettingsInfoPage().FindName("InfoPageScroll") is ScrollViewer i && SmoothScroll.GetDragScroll(i);
            return (onWindow, key, info);
        });

        Assert.True(window);
        Assert.True(keyPage);
        Assert.True(infoPage);
    }

    [Fact]
    public void The_chat_feed_does_not_scroll_by_dragging()
    {
        // В ленте левая кнопка выделяет текст и таскает приближённое лупой.
        var dragging = _wpf.Ui.Invoke(() =>
        {
            var main = Application.Current.Windows.OfType<MainWindow>().Single();
            return SmoothScroll.GetDragScroll((ScrollViewer)main.FindName("ChatScrollViewer")!);
        });

        Assert.False(dragging);
    }

    [Fact]
    public void Dragging_moves_the_content_with_the_hand()
    {
        var offset = WithPage(900, (host, page) =>
        {
            SmoothScroll.ArmDrag(page, new Point(100, 200));
            var dragged = SmoothScroll.DragTo(page, new Point(100, 120));
            SmoothScroll.EndDrag(page, fling: false);
            host.UpdateLayout();
            return dragged ? page.VerticalOffset : double.NaN;
        });

        Assert.Equal(80, offset, 1);
    }

    [Fact]
    public void A_small_wobble_is_still_a_click()
    {
        var dragged = WithPage(900, (_, page) =>
        {
            SmoothScroll.ArmDrag(page, new Point(100, 200));
            var moved = SmoothScroll.DragTo(page, new Point(101, 199));
            SmoothScroll.EndDrag(page, fling: false);
            return moved;
        });

        Assert.False(dragged);
    }

    [Fact]
    public void A_sideways_gesture_is_left_to_whoever_started_it()
    {
        // Выделение текста, ползунок — рука пошла вбок, и это не прокрутка.
        var dragged = WithPage(900, (_, page) =>
        {
            SmoothScroll.ArmDrag(page, new Point(100, 200));
            var first = SmoothScroll.DragTo(page, new Point(160, 190));
            var then = SmoothScroll.DragTo(page, new Point(160, 100));
            return first || then;
        });

        Assert.False(dragged);
    }

    [Fact]
    public void Nothing_to_scroll_means_no_drag()
    {
        // Иначе нажатие на пустом месте короткой колонки отнималось бы у перетаскивания окна.
        var dragged = WithPage(100, (_, page) =>
        {
            SmoothScroll.ArmDrag(page, new Point(100, 200));
            return SmoothScroll.DragTo(page, new Point(100, 100));
        });

        Assert.False(dragged);
    }

    [Fact]
    public void A_quick_release_flings_on_with_inertia()
    {
        var (animating, dragging) = WithPage(3000, (_, page) =>
        {
            SmoothScroll.ArmDrag(page, new Point(100, 250));
            SmoothScroll.DragTo(page, new Point(100, 240));

            // Скорость меряется по времени между движениями — без паузы их разделяли бы
            // микросекунды, и бросок посчитался бы из ничего.
            Thread.Sleep(20);
            SmoothScroll.DragTo(page, new Point(100, 160));
            SmoothScroll.EndDrag(page, fling: true);
            return (SmoothScroll.IsAnimating(page), SmoothScroll.IsDragging(page));
        });

        Assert.True(animating);
        Assert.False(dragging);
    }

    [Fact]
    public void Pulling_past_the_top_stretches_and_springs_back()
    {
        var (offset, animating) = WithPage(900, (host, page) =>
        {
            SmoothScroll.ArmDrag(page, new Point(100, 100));
            SmoothScroll.DragTo(page, new Point(100, 180));
            SmoothScroll.EndDrag(page, fling: false);
            host.UpdateLayout();
            return (page.VerticalOffset, SmoothScroll.IsAnimating(page));
        });

        Assert.Equal(0, offset, 1);
        Assert.True(animating);
    }

    [Fact]
    public void A_press_in_a_text_box_is_not_a_drag()
    {
        var dragged = WithPage(900, (host, page) =>
        {
            var box = new TextBox { Height = 40 };
            ((StackPanel)page.Content).Children.Insert(0, box);
            host.UpdateLayout();

            box.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });

            var cursor = Mouse.GetPosition(page);
            return SmoothScroll.DragTo(page, new Point(cursor.X, cursor.Y + 300));
        });

        Assert.False(dragged);
    }

    [Fact]
    public void Gliding_to_an_edge_reports_arrival_even_when_the_wheel_cuts_in()
    {
        var (gliding, arrivedBeforeWheel, arrivedAfterWheel, stillGliding) = WithPage(3000, (_, page) =>
        {
            var arrived = false;
            SmoothScroll.GlideTo(page, ScrollEdge.Bottom, () => arrived = true);
            var started = SmoothScroll.IsGliding(page);
            var before = arrived;

            page.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent
            });

            return (started, before, arrived, SmoothScroll.IsGliding(page));
        });

        Assert.True(gliding);
        Assert.False(arrivedBeforeWheel);
        Assert.True(arrivedAfterWheel);
        Assert.False(stillGliding);
    }

    [Fact]
    public void Gliding_to_where_the_list_already_is_arrives_at_once()
    {
        var arrived = WithPage(3000, (_, page) =>
        {
            var done = false;
            SmoothScroll.GlideTo(page, ScrollEdge.Top, () => done = true);
            return done && !SmoothScroll.IsGliding(page);
        });

        Assert.True(arrived);
    }

    [Fact]
    public void The_jump_pill_shows_on_a_long_chat_and_greys_out_the_edge_it_is_at()
    {
        var (shown, up, down, emptyShown) = _wpf.Ui.Invoke(() =>
        {
            const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var field = typeof(MainWindow).GetField("_session", hidden)!;
            var original = field.GetValue(window);
            void Call(string name) => typeof(MainWindow).GetMethod(name, hidden)!.Invoke(window, null);

            var pill = (FrameworkElement)window.FindName("ScrollJumpPill")!;
            var top = (Button)window.FindName("ScrollTopButton")!;
            var bottom = (Button)window.FindName("ScrollBottomButton")!;
            var viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;
            try
            {
                var chat = new ChatSession { Id = "jump-pill", Title = "Длинный разговор" };
                for (var i = 0; i < 40; i++)
                {
                    chat.Messages.Add(new ChatDisplayMessage
                    {
                        Role = i % 2 == 0 ? "user" : "assistant",
                        Id = "m" + i,
                        Text = string.Join(' ', Enumerable.Repeat("слово", 120))
                    });
                }

                field.SetValue(window, chat);
                Call("RenderSession");
                window.UpdateLayout();
                viewer.ScrollToTop();
                window.UpdateLayout();
                Call("UpdateScrollJump");
                var longChat = (pill.IsHitTestVisible, top.IsEnabled, bottom.IsEnabled);

                field.SetValue(window, new ChatSession { Id = "jump-empty", Title = "Пусто" });
                Call("RenderSession");
                window.UpdateLayout();
                Call("UpdateScrollJump");
                return (longChat.Item1, longChat.Item2, longChat.Item3, pill.IsHitTestVisible);
            }
            finally
            {
                field.SetValue(window, original);
                Call("RenderSession");
            }
        });

        Assert.True(shown);
        Assert.False(up);
        Assert.True(down);
        Assert.False(emptyShown);
    }

    private T WithPage<T>(double contentHeight, Func<Window, ScrollViewer, T> body) =>
        _wpf.Ui.Invoke(() =>
        {
            var page = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { new Border { Height = contentHeight } } }
            };

            var host = new Window
            {
                Width = 400,
                Height = 300,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = page
            };

            SmoothScroll.SetIsEnabled(page, true);
            SmoothScroll.SetDragScroll(page, true);
            try
            {
                host.Show();
                host.UpdateLayout();
                return body(host, page);
            }
            finally
            {
                // Хук держит CompositionTarget.Rendering, пока идёт инерция: снимаем его руками.
                SmoothScroll.SetIsEnabled(page, false);
                host.Close();
            }
        });
}
