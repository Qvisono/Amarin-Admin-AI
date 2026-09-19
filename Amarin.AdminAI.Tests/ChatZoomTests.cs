using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Лупа над лентой чата: приближение Ctrl с колесом и перетаскивание приближённого.
/// </summary>
/// <remarks>
/// Главное здесь — что приближение остаётся лупой, а не превращается в увеличение шрифта: ширина,
/// которой меряются сообщения, обязана остаться прежней, иначе текст перельётся заново, а вместе
/// с ним устареют запомненные высоты сообщений и вся ленивая достройка ленты. Остальное — швы,
/// на которых лупа встречается с тем, что уже двигало ленту до неё: автопрокруткой, инерцией
/// и перетаскиванием окна.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ChatZoomTests
{
    private readonly WpfFixture _wpf;

    public ChatZoomTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_transcript_hangs_in_a_zoom_host()
    {
        var ok = WithChat((_, viewer, host, panel) =>
            ReferenceEquals(viewer.Content, host) && ReferenceEquals(host.Child, panel));

        Assert.True(ok, "лента больше не висит в ChatZoomHost — лупе нечего увеличивать");
    }

    [Fact]
    public void Zooming_does_not_reflow_the_text()
    {
        var (widthBefore, widthAfter, messageBefore, messageAfter, documentBefore, documentAfter) =
            WithChat((window, _, host, panel) =>
            {
                Fill(panel, 12);
                window.UpdateLayout();

                var width = panel.ActualWidth;
                var message = ((FrameworkElement)panel.Children[0]).ActualHeight;
                var document = host.DocumentHeight;

                host.Scale = 2.0;
                window.UpdateLayout();

                return (width, panel.ActualWidth, message,
                    ((FrameworkElement)panel.Children[0]).ActualHeight, document, host.DocumentHeight);
            });

        // Ширина ленты при приближении не меняется — на ней и держится весь перенос строк.
        Assert.Equal(widthBefore, widthAfter, 3);
        Assert.Equal(messageBefore, messageAfter, 3);
        Assert.Equal(documentBefore, documentAfter, 3);
    }

    [Fact]
    public void The_scrollbar_grows_with_the_zoom()
    {
        var (before, after) = WithChat((window, viewer, host, panel) =>
        {
            Fill(panel, 12);
            window.UpdateLayout();
            var extent = viewer.ExtentHeight;

            host.Scale = 2.0;
            window.UpdateLayout();
            return (extent, viewer.ExtentHeight);
        });

        // Иначе до низа приближённой ленты было бы не домотать.
        Assert.Equal(before * 2, after, 1);
    }

    [Fact]
    public void The_host_arranges_the_transcript_unscaled()
    {
        var (documentHeight, hostHeight) = WithChat((window, _, host, panel) =>
        {
            Fill(panel, 12);
            host.Scale = 2.0;
            window.UpdateLayout();
            return (host.DocumentHeight, host.RenderSize.Height);
        });

        // Наверх — увеличенная высота, вниз — исходная. В этом весь фокус.
        Assert.Equal(documentHeight * 2, hostHeight, 1);
    }

    [Fact]
    public void A_click_lands_on_the_message_under_the_cursor()
    {
        var (atNormalSize, zoomedIn) = WithChat((window, viewer, host, panel) =>
        {
            Fill(panel, 6);
            window.UpdateLayout();

            // Ленту только что наполнили, и автопрокрутка увела её в конец — а проверка про то,
            // куда попадает клик, а не про то, где лента стоит.
            Top(window, viewer);
            var plain = MessageUnder(viewer, new Point(100, 500));

            host.Scale = 2.0;
            window.UpdateLayout();
            Top(window, viewer);
            return (plain, MessageUnder(viewer, new Point(100, 500)));
        });

        // Тот же пиксель экрана при двукратном приближении показывает вдвое более раннее место
        // ленты. WPF считает попадание через обратную матрицу — выделение, ссылки и кнопки
        // в приближённом виде на этом и держатся.
        Assert.Equal(1, atNormalSize);
        Assert.Equal(0, zoomedIn);
    }

    [Fact]
    public void Autoscroll_stands_aside_while_the_zoom_glides()
    {
        var (whileZooming, afterwards) = WithChat((window, viewer, _, panel) =>
        {
            Fill(panel, 20);
            window.UpdateLayout();
            StickToBottom(window);

            var zoom = Zoom(window);
            zoom.ZoomBy(120, new Point(400, 300));
            Assert.True(zoom.IsBusy, "жест начался, но лупа не считает себя занятой");

            viewer.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            Autoscroll(window);
            window.UpdateLayout();
            var during = viewer.VerticalOffset;

            zoom.Reset();
            viewer.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            Autoscroll(window);
            window.UpdateLayout();
            return (during, viewer.VerticalOffset);
        });

        // Во время наезда высота ленты меняется на каждом кадре: без этой уступки автопрокрутка
        // швыряла бы ленту в конец шестьдесят раз в секунду вместо того, на что человек навёлся.
        Assert.Equal(0, whileZooming, 1);

        // А как только жест кончился — следит за концом как раньше.
        Assert.True(afterwards > 1, $"автопрокрутка перестала работать вовсе: {afterwards}");
    }

    [Fact]
    public void A_fling_starts_from_where_the_transcript_stands_now()
    {
        var seeded = WithChat((window, viewer, _, panel) =>
        {
            Fill(panel, 20);
            window.UpdateLayout();

            // Колесо оставляет за прокруткой её собственную запомненную позицию.
            RaiseWheel(panel);

            // Перетаскивание пишет смещение напрямую, мимо неё.
            viewer.ScrollToVerticalOffset(1000);
            window.UpdateLayout();

            SmoothScroll.Fling(viewer, -400);
            var virtualOffset = VirtualOffset(viewer);

            SmoothScroll.Cancel(viewer);
            return virtualOffset;
        });

        // Со старой запомненной позицией инерция на первом же кадре дёрнула бы ленту обратно
        // к началу перетаскивания.
        Assert.Equal(1000, seeded, 1);
    }

    [Fact]
    public void The_first_wheel_after_the_zoom_does_not_jump()
    {
        var seeded = WithChat((window, viewer, _, panel) =>
        {
            Fill(panel, 20);
            window.UpdateLayout();

            // Обычная прокрутка: с этого мгновения у неё есть своя запомненная позиция.
            RaiseWheel(panel);

            // Жест лупы гасит инерцию и дальше ведёт ленту сам, мимо прокрутки.
            SmoothScroll.Cancel(viewer);
            viewer.ScrollToVerticalOffset(2000);
            window.UpdateLayout();

            RaiseWheel(panel);
            var from = VirtualOffset(viewer);
            SmoothScroll.Cancel(viewer);
            return from;
        });

        // Первое колесо после лупы обязано поехать оттуда, где лента стоит, а не оттуда, где её
        // застал прошлый бросок: иначе она прыгает через пол-экрана — один раз, а потом как ни
        // в чём не бывало.
        Assert.Equal(2000, seeded, 1);
    }

    [Fact]
    public void The_wheel_without_ctrl_still_scrolls_the_chat()
    {
        var (animating, scale) = WithChat((window, viewer, _, panel) =>
        {
            Fill(panel, 20);
            window.UpdateLayout();

            RaiseWheel(panel);
            var result = (SmoothScroll.IsAnimating(viewer), Zoom(window).Scale);
            SmoothScroll.Cancel(viewer);
            return result;
        });

        Assert.True(animating, "обычное колесо перестало листать чат");
        Assert.Equal(1.0, scale, 6);
    }

    [Fact]
    public void Claiming_the_wheel_at_the_window_keeps_the_chat_from_scrolling()
    {
        var (claimed, left) = WithChat((window, viewer, _, panel) =>
        {
            Fill(panel, 20);
            window.UpdateLayout();

            // Ровно то, что делает лупа, когда жест её: гасит колесо на уровне окна.
            var claimer = new MouseWheelEventHandler((_, e) => e.Handled = true);
            window.AddHandler(UIElement.PreviewMouseWheelEvent, claimer);
            RaiseWheel(panel);
            var withClaim = SmoothScroll.IsAnimating(viewer);

            window.RemoveHandler(UIElement.PreviewMouseWheelEvent, claimer);
            SmoothScroll.Cancel(viewer);

            RaiseWheel(panel);
            var withoutClaim = SmoothScroll.IsAnimating(viewer);
            SmoothScroll.Cancel(viewer);
            return (withClaim, withoutClaim);
        });

        // На этом и держится вся лупа: обработчик окна туннелем приходит раньше, а SmoothScroll
        // подписан обычным += и обработанное колесо пропускает. Подпишись он с handledEventsToo —
        // приближение выглядело бы как обычная прокрутка, и ничего больше.
        Assert.False(claimed, "перехваченное колесо всё равно прокрутило чат");
        Assert.True(left, "неперехваченное колесо перестало прокручивать чат");
    }

    [Fact]
    public void Dragging_a_zoomed_transcript_moves_it_under_the_hand()
    {
        var (panned, offsetBefore, offsetAfter, panBefore, panAfter) = Zoomed((window, viewer, host, zoom) =>
        {
            viewer.ScrollToVerticalOffset(1200);
            window.UpdateLayout();

            // Первое движение только пересекает порог: тянем от той точки, где он пройден, —
            // иначе лента дёргалась бы на эти самые четыре точки в начале каждого жеста.
            zoom.ArmPan(new Point(500, 400));
            zoom.PanTo(new Point(480, 380));
            window.UpdateLayout();

            var startOffset = viewer.VerticalOffset;
            var startPan = host.PanX;

            var moved = zoom.PanTo(new Point(400, 280));
            window.UpdateLayout();

            var result = (moved, startOffset, viewer.VerticalOffset, startPan, host.PanX);
            zoom.EndPan(fling: false);
            return result;
        });

        Assert.True(panned, "перетаскивание не началось вовсе");

        // Рука вверх на 100 — лента вверх, то есть смещение прокрутки выросло ровно на столько же.
        Assert.Equal(offsetBefore + 100, offsetAfter, 1);

        // И влево на 80 — полотно уехало влево на те же 80.
        Assert.Equal(panBefore - 80, panAfter, 1);
    }

    [Fact]
    public void The_drag_survives_the_message_losing_the_mouse()
    {
        var (panning, offsetAfter, offsetBefore) = Zoomed((window, viewer, host, zoom) =>
        {
            viewer.ScrollToVerticalOffset(1200);
            window.UpdateLayout();

            zoom.ArmPan(new Point(500, 400));
            zoom.PanTo(new Point(500, 380));
            window.UpdateLayout();
            var start = viewer.VerticalOffset;

            // Ровно то, что делает настоящее нажатие: сообщение забрало мышь себе под выделение,
            // а мы её отняли — и его LostMouseCapture всплывает через ленту.
            var message = (FrameworkElement)((StackPanel)host.Child!).Children[0];
            message.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.LostMouseCaptureEvent
            });

            var alive = zoom.IsPanning;
            var moved = zoom.PanTo(new Point(500, 280));
            window.UpdateLayout();

            var result = (alive && moved, viewer.VerticalOffset, start);
            zoom.EndPan(fling: false);
            return result;
        });

        // Пузырьковый LostMouseCapture отменял жест в то же мгновение, когда тот начинался.
        Assert.True(panning, "перетаскивание умерло, как только сообщение отдало мышь");
        Assert.Equal(offsetBefore + 100, offsetAfter, 1);
    }

    [Fact]
    public void The_drag_starts_from_where_the_transcript_stands_now()
    {
        var (jump, offsetAfter) = Zoomed((window, viewer, _, zoom) =>
        {
            // Лентой между жестами распоряжаются другие — прокрутка, автопрокрутка, достройка.
            viewer.ScrollToVerticalOffset(1500);
            window.UpdateLayout();
            var start = viewer.VerticalOffset;

            zoom.ArmPan(new Point(500, 400));
            zoom.PanTo(new Point(500, 390));
            window.UpdateLayout();

            var result = (Math.Abs(viewer.VerticalOffset - start), viewer.VerticalOffset);
            zoom.EndPan(fling: false);
            return result;
        });

        // Тянем от той точки, где пройден порог, так что сдвинуться лента здесь не должна вовсе.
        // А взяв смещение, оставшееся от прошлого жеста, она прыгнула бы на сотни точек от
        // первого же движения руки на десять.
        Assert.True(jump <= 1, $"лента прыгнула в начале перетаскивания: оказалась на {offsetAfter:F0}");
    }

    [Fact]
    public void A_coasting_transcript_is_not_dragged_back_by_the_zoom()
    {
        Window window = null!;
        ScrollViewer viewer = null!;
        ChatZoom zoom = null!;

        _wpf.Ui.Invoke(() =>
        {
            (window, viewer, _, zoom) = BuildRig();
            zoom.ZoomBy(240, new Point(500, 400));
            return 0;
        });

        try
        {
            Assert.True(Settle(() => !zoom.IsBusy), "приближение не доехало до цели");

            // Тащим строго вбок: вертикальной скорости нет, значит, и вертикального броска не
            // будет — останется только наш горизонтальный, ради которого цикл и живёт дальше.
            _wpf.Ui.Invoke(() =>
            {
                viewer.ScrollToVerticalOffset(1200);
                window.UpdateLayout();
                zoom.ArmPan(new Point(600, 400));
                zoom.PanTo(new Point(560, 400));
                return 0;
            });

            Thread.Sleep(40);

            var coasting = _wpf.Ui.Invoke(() =>
            {
                zoom.PanTo(new Point(480, 400));
                zoom.EndPan(fling: true);
                return zoom.IsBusy;
            });

            Assert.True(coasting, "горизонтальный бросок не поехал — проверять нечего");

            // Пока он едет, вертикалью распоряжается кто-то другой. Здесь это просто прокрутка.
            _wpf.Ui.Invoke(() =>
            {
                viewer.ScrollToVerticalOffset(300);
                window.UpdateLayout();
                return 0;
            });

            Settle(() => !zoom.IsBusy);
            var landed = _wpf.Ui.Invoke(() => viewer.VerticalOffset);

            // Раньше цикл лупы возвращал сюда своё число с каждым кадром, пока другой хозяин вёл
            // ленту дальше. На экране это и было два текста разом: один едет, другой стоит.
            Assert.Equal(300, landed, 1);
        }
        finally
        {
            _wpf.Ui.Invoke(() =>
            {
                window.Close();
                Mouse.OverrideCursor = null;
                Mouse.Capture(null);
                return 0;
            });
        }
    }

    [Fact]
    public void An_untouched_transcript_leaves_the_window_draggable()
    {
        var (atRest, afterReset) = WithChat((window, _, _, _) =>
        {
            var zoom = Zoom(window);
            var rest = zoom.SuppressesWindowDrag();
            zoom.Reset();
            return (rest, zoom.SuppressesWindowDrag());
        });

        // Иначе перетаскивание окна по пустому месту чата пропало бы у всех и навсегда.
        Assert.False(atRest);
        Assert.False(afterReset);
    }

    [Fact]
    public void Reset_returns_the_transcript_to_its_normal_size()
    {
        var (scale, pan, busy) = WithChat((window, _, host, panel) =>
        {
            Fill(panel, 12);
            window.UpdateLayout();

            var zoom = Zoom(window);
            zoom.ZoomBy(240, new Point(400, 300));
            host.Scale = 2.4;
            host.PanX = -300;
            window.UpdateLayout();

            zoom.Reset();
            window.UpdateLayout();
            return (host.Scale, host.PanX, zoom.IsBusy);
        });

        Assert.Equal(1.0, scale, 6);
        Assert.Equal(0, pan, 6);
        Assert.False(busy, "сброс оставил жест незакрытым");
    }

    [Fact]
    public void The_zoom_glides_home_without_losing_the_point_under_the_cursor()
    {
        const double cursorY = 300;
        const double startOffset = 600;

        MainWindow window = null!;
        ScrollViewer viewer = null!;
        ChatZoomHost host = null!;
        ChatZoom zoom = null!;

        var before = _wpf.Ui.Invoke(() => Application.Current.Windows.OfType<MainWindow>().Count());

        _wpf.Ui.Invoke(() =>
        {
            window = new MainWindow { Width = 1200, Height = 900 };
            window.Show();
            viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;
            host = (ChatZoomHost)window.FindName("ChatZoomLayer")!;
            Fill((StackPanel)window.FindName("MessagesPanel")!, 12);
            window.UpdateLayout();

            viewer.ScrollToVerticalOffset(startOffset);
            window.UpdateLayout();

            zoom = Zoom(window);
            zoom.ZoomBy(240, new Point(400, cursorY));
            return 0;
        });

        try
        {
            var settled = Settle(() => !zoom.IsBusy);
            var (scale, offset, extent, document, badge) = _wpf.Ui.Invoke(() =>
                (zoom.Scale, viewer.VerticalOffset, viewer.ExtentHeight, host.DocumentHeight,
                    ((TextBlock)window.FindName("ChatZoomBadgeText")!).Text));

            // Плашка называет тот же масштаб: без этого она молча висела бы пустой.
            Assert.Equal("144%", badge);

            Assert.True(settled, "приближение не доехало до цели и не остановилось само");

            // Два щелчка колеса — 1.2 в квадрате.
            Assert.Equal(1.44, scale, 3);

            // Главное: точка ленты, бывшая под курсором, осталась под тем же пикселем — не в
            // одном кадре, а после всего наезда, кадр за кадром. Допуск в пиксель, а не в разряд:
            // окно живёт с UseLayoutRounding, и высоты приходят округлёнными до целых точек.
            var anchor = (offset + cursorY) / scale;
            Assert.True(
                Math.Abs(anchor - (startOffset + cursorY)) <= 1,
                $"точка под курсором уехала: было {startOffset + cursorY}, стало {anchor:F2}");

            // И полоса прокрутки доросла ровно до приближённой ленты.
            Assert.True(
                Math.Abs((document * scale) - extent) <= 1,
                $"полоса прокрутки разошлась с лентой: {extent:F2} против {document * scale:F2}");
        }
        finally
        {
            _wpf.Ui.Invoke(() =>
            {
                window.Close();
                return 0;
            });
        }
    }

    // ───────────────────────── оснастка ─────────────────────────

    /// <summary>
    /// Ждёт, пока жест доедет сам.
    /// </summary>
    /// <remarks>
    /// Через настоящие кадры, а не подкруткой часов: проверять тут нечего, кроме того, что цикл
    /// отрисовки вправду крутится и вправду останавливается. Запас по времени большой — жест
    /// занимает около четверти секунды, и на медленной машине важно не поймать ложную ошибку.
    /// </remarks>
    private bool Settle(Func<bool> done)
    {
        for (var i = 0; i < 60; i++)
        {
            if (_wpf.Ui.Invoke(done))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return false;
    }

    /// <summary>
    /// Отдаёт телу своё окно с разобранной по частям лентой.
    /// </summary>
    /// <remarks>
    /// Своё, а не общее окно тестовой оснастки: лупа живёт в поле окна и трогает прокрутку чата,
    /// и соседние тесты не должны находить её в непонятном виде. Размер задан руками — видимая
    /// область должна быть заведомо выше пятисот точек, на которые целятся проверки попадания.
    /// </remarks>
    private T WithChat<T>(Func<MainWindow, ScrollViewer, ChatZoomHost, StackPanel, T> body) =>
        _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow
            {
                Width = 1200,
                Height = 900
            };
            window.Show();
            try
            {
                var viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;
                var host = (ChatZoomHost)window.FindName("ChatZoomLayer")!;
                var panel = (StackPanel)window.FindName("MessagesPanel")!;
                window.UpdateLayout();
                return body(window, viewer, host, panel);
            }
            finally
            {
                window.Close();
            }
        });

    /// <summary>Ставит в ленту «сообщения» ровно по 300 точек, помеченные своим номером.</summary>
    private static void Fill(StackPanel panel, int count)
    {
        panel.Children.Clear();
        for (var i = 0; i < count; i++)
        {
            panel.Children.Add(new Border
            {
                Height = 300,
                Tag = i,
                Background = Brushes.Gray
            });
        }
    }

    /// <summary>
    /// Отдаёт телу уже приближённую ленту — по-настоящему, кадрами, а не подстановкой масштаба.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Лента здесь собрана из голых частей, а не взята у <c>MainWindow</c>, и это не упрощение,
    /// а осторожность. Полсотни соседних классов ищут единственное окно программы через
    /// <c>Single()</c>, и лишнее <c>MainWindow</c>, забытое здесь, роняло бы их все разом — под
    /// три сотни чужих тестов, да ещё и через раз. Обычное <see cref="Window"/> в этот счёт не
    /// попадает вовсе, так что цена ошибки тут — этот тест, и только он.
    /// </para>
    /// <para>
    /// Перетаскивание включается только от <c>IsZoomed</c>, а он смотрит на нарисованный масштаб,
    /// который доезжает до цели за несколько кадров. Поставив <c>ChatZoomHost.Scale</c> руками, мы
    /// проверяли бы жест, которого в этом состоянии не бывает.
    /// </para>
    /// </remarks>
    private T Zoomed<T>(Func<Window, ScrollViewer, ChatZoomHost, ChatZoom, T> body)
    {
        Window window = null!;
        ScrollViewer viewer = null!;
        ChatZoomHost host = null!;
        ChatZoom zoom = null!;

        _wpf.Ui.Invoke(() =>
        {
            (window, viewer, host, zoom) = BuildRig();
            zoom.ZoomBy(240, new Point(500, 400));
            return 0;
        });

        try
        {
            Assert.True(Settle(() => !zoom.IsBusy), "приближение не доехало до цели");
            Assert.True(_wpf.Ui.Invoke(() => zoom.IsZoomed), "лента так и не приблизилась");
            return _wpf.Ui.Invoke(() => body(window, viewer, host, zoom));
        }
        finally
        {
            _wpf.Ui.Invoke(() =>
            {
                window.Close();
                Mouse.OverrideCursor = null;
                Mouse.Capture(null);
                return 0;
            });
        }
    }

    /// <summary>Лента с лупой, собранная из голых частей. Только внутри потока интерфейса.</summary>
    private static (Window, ScrollViewer, ChatZoomHost, ChatZoom) BuildRig()
    {
        var panel = new StackPanel { Margin = new Thickness(27, 18, 58, 10) };
        Fill(panel, 20);

        var host = new ChatZoomHost { Child = panel };
        var viewer = new ScrollViewer
        {
            Content = host,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        SmoothScroll.SetIsEnabled(viewer, true);

        var badgeText = new TextBlock();
        var badge = new Border { Child = badgeText, Opacity = 0 };
        var root = new Grid();
        root.Children.Add(viewer);
        root.Children.Add(badge);

        var window = new Window
        {
            Width = 1200,
            Height = 900,
            Content = root,

            // Не забирать фокус: окно программы рядом живое, и соседние тесты смотрят,
            // куда он попал.
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        window.UpdateLayout();

        var zoom = new ChatZoom(window, viewer, host, badge, badgeText, () => false, () => { });
        return (window, viewer, host, zoom);
    }

    /// <summary>Ставит ленту на самое начало и дожидается раскладки.</summary>
    private static void Top(MainWindow window, ScrollViewer viewer)
    {
        viewer.ScrollToVerticalOffset(0);
        window.UpdateLayout();
    }

    /// <summary>Номер «сообщения» под точкой видимой области, или -1.</summary>
    private static int MessageUnder(ScrollViewer viewer, Point point)
    {
        if (VisualTreeHelper.HitTest(viewer, point)?.VisualHit is not DependencyObject hit)
        {
            return -1;
        }

        for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Border { Tag: int index })
            {
                return index;
            }
        }

        return -1;
    }

    private static ChatZoom Zoom(MainWindow window) =>
        (ChatZoom)typeof(MainWindow)
            .GetField("_chatZoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void StickToBottom(MainWindow window) =>
        typeof(MainWindow)
            .GetField("_stickToBottom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);

    private static void Autoscroll(MainWindow window) =>
        typeof(MainWindow)
            .GetMethod("MaybeAutoscroll", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

    /// <summary>Позиция, которую плавная прокрутка считает своей.</summary>
    private static double VirtualOffset(ScrollViewer viewer)
    {
        var slot = (DependencyProperty)typeof(SmoothScroll)
            .GetField("HookProperty", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var hook = viewer.GetValue(slot)!;
        return (double)hook.GetType()
            .GetField("_virtual", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(hook)!;
    }

    /// <summary>
    /// Колесо вниз с самого глубокого элемента — так событие идёт сверху вниз через всех, кто над
    /// ним, ровно как от настоящей мыши. Модификаторов оно не несёт, поэтому лупа его не берёт.
    /// </summary>
    private static void RaiseWheel(UIElement deepest) =>
        deepest.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent
        });
}
