using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выпадашки: одна открытая за раз и закрытие по клику мимо. Раньше их гасил
/// <c>StaysOpen="False"</c>, который не видит клика в чужом окне попапа — они копились.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PopupManagerTests
{
    private readonly WpfFixture _wpf;

    public PopupManagerTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Registering_takes_closing_away_from_wpf()
    {
        var staysOpen = _wpf.Ui.Invoke(() =>
        {
            var popup = new Popup { StaysOpen = false, Child = new Border() };
            PopupManager.Register(popup);
            return popup.StaysOpen;
        });

        Assert.True(staysOpen);
    }

    [Fact]
    public void Opening_one_popup_closes_the_others()
    {
        var (first, second) = _wpf.Ui.Invoke(() =>
        {
            var window = new Window();
            var host = new Grid();
            window.Content = host;

            var (popupA, toggleA) = NewPopup(host);
            var (popupB, toggleB) = NewPopup(host);
            window.Show();

            toggleA.IsChecked = true;
            toggleB.IsChecked = true;

            var result = (popupA.IsOpen, popupB.IsOpen);
            window.Close();
            return result;
        });

        Assert.False(first);
        Assert.True(second);
    }

    [Fact]
    public void Closing_a_popup_releases_its_button()
    {
        var checkedAfter = _wpf.Ui.Invoke(() =>
        {
            var window = new Window();
            var host = new Grid();
            window.Content = host;

            var (_, toggle) = NewPopup(host);
            window.Show();

            toggle.IsChecked = true;
            PopupManager.CloseAll();

            var result = toggle.IsChecked == true;
            window.Close();
            return result;
        });

        Assert.False(checkedAfter);
    }

    [Fact]
    public void A_click_somewhere_else_in_the_window_closes_the_popup()
    {
        var open = _wpf.Ui.Invoke(() =>
        {
            var window = new Window();
            var host = new Grid();
            var elsewhere = new Button();
            host.Children.Add(elsewhere);
            window.Content = host;

            var (popup, toggle) = NewPopup(host);
            window.Show();
            toggle.IsChecked = true;

            elsewhere.RaiseEvent(MouseDown());

            var result = popup.IsOpen;
            window.Close();
            return result;
        });

        Assert.False(open);
    }

    [Fact]
    public void A_click_inside_the_popup_leaves_it_open()
    {
        var open = _wpf.Ui.Invoke(() =>
        {
            var window = new Window();
            var host = new Grid();
            window.Content = host;

            var (popup, toggle) = NewPopup(host);
            window.Show();
            toggle.IsChecked = true;

            // Выбор модели — это клик по содержимому попапа; закрыться на нём нельзя.
            ((FrameworkElement)popup.Child).RaiseEvent(MouseDown());

            var result = popup.IsOpen;
            window.Close();
            return result;
        });

        Assert.True(open);
    }

    [Fact]
    public void Typing_in_the_model_search_box_keeps_the_picker_open()
    {
        var open = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.Show();

            var picker = (Popup)window.FindName("ModelPicker")!;
            var button = (ToggleButton)window.FindName("ModelButton")!;
            button.IsChecked = true;

            // Поиск живёт в отдельном окне попапа; фокус на нём не должен читаться
            // как уход фокуса с главного окна, иначе список закрывался бы на первом клике.
            var panel = (ModelPickerPanel)window.FindName("ChatModelPicker")!;
            var search = (TextBox)panel.FindName("ModelSearchBox")!;
            search.Focus();
            search.Text = "grok";
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

            var result = picker.IsOpen;
            window.Close();
            return result;
        });

        Assert.True(open);
    }

    private static System.Windows.Input.MouseButtonEventArgs MouseDown() =>
        new(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        };

    private static (Popup Popup, ToggleButton Toggle) NewPopup(Panel host)
    {
        var toggle = new ToggleButton();
        var popup = new Popup { Child = new Border { Width = 40, Height = 40 }, PlacementTarget = toggle };
        host.Children.Add(toggle);
        host.Children.Add(popup);

        popup.SetBinding(
            Popup.IsOpenProperty,
            new System.Windows.Data.Binding(nameof(ToggleButton.IsChecked))
            {
                Source = toggle,
                Mode = System.Windows.Data.BindingMode.TwoWay
            });

        PopupManager.Register(popup, toggle);
        return (popup, toggle);
    }
}

/// <summary>
/// Развёрнутое окно не должно вылезать за экран и накрывать панель задач — с собственным
/// оформлением Windows отдаёт ему монитор плюс невидимую рамку.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class MaximizedWindowTests
{
    private readonly WpfFixture _wpf;

    public MaximizedWindowTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void A_maximized_window_stays_inside_the_work_area()
    {
        var (left, top, width, height, work) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.Show();
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();

            var bounds = (window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            var area = SystemParameters.WorkArea;
            window.Close();
            return (bounds.Left, bounds.Top, bounds.ActualWidth, bounds.ActualHeight, area);
        });

        // Допуск в один пиксель: границы приходят из аппаратных пикселей через масштаб экрана.
        Assert.True(left >= work.Left - 1, $"левый край {left} при рабочей области {work}");
        Assert.True(top >= work.Top - 1, $"верхний край {top} при рабочей области {work}");
        Assert.True(width <= work.Width + 1, $"ширина {width} при рабочей области {work}");
        Assert.True(height <= work.Height + 1, $"высота {height} при рабочей области {work}");
    }

    [Fact]
    public void A_window_whose_handle_was_made_before_it_was_shown_still_fits()
    {
        var (rect, work) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();

            // Ровно порядок запуска приложения: UiScale зовёт EnsureHandle из AttachServices,
            // то есть окно создаётся до Show. При этом SourceInitialized проходит раньше, чем
            // дерево визуалов привязано к источнику, и перехватчик, который искали по визуалу,
            // молча терялся — развёрнутое окно уезжало на невидимую рамку за края экрана.
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Show();
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            GetWindowRect(handle, out var actual);
            var area = WorkArea(handle);
            window.Close();
            return (actual, area);
        });

        Assert.Equal((work.left, work.top, work.right, work.bottom), (rect.left, rect.top, rect.right, rect.bottom));
    }

    [Fact]
    public void A_maximized_window_is_pulled_back_when_something_oversizes_it()
    {
        var (rect, work) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.Show();
            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var area = WorkArea(handle);

            // Ровно то, на что жалуются: окно на невидимую рамку больше экрана со всех сторон.
            SetWindowPos(
                handle,
                IntPtr.Zero,
                area.left - 8,
                area.top - 8,
                area.right - area.left + 16,
                area.bottom - area.top + 16,
                SWP_NOZORDER | SWP_NOACTIVATE);

            GetWindowRect(handle, out var actual);
            window.Close();
            return (actual, area);
        });

        Assert.Equal((work.left, work.top, work.right, work.bottom), (rect.left, rect.top, rect.right, rect.bottom));
    }

    [Fact]
    public void The_window_edge_is_a_real_resize_border()
    {
        // Растягивание отдано системе: WindowChrome отвечает на WM_NCHITTEST по кромке сам.
        // Нулевая толщина (как было) означала бы, что окно не растягивается вовсе — раньше это
        // прикрывал слой прозрачных Border с ручной пересылкой WM_NCLBUTTONDOWN, теперь его нет.
        var (thickness, caption, gripsLeft) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(window);
            var leftovers = window.FindName("ResizeGrips") is not null;
            window.Close();
            return (chrome.ResizeBorderThickness, chrome.CaptionHeight, leftovers);
        });

        Assert.True(thickness.Left >= 4, "кромка уже 4 DIP — в неё не попасть мышью");
        Assert.True(thickness.Top >= 4 && thickness.Right >= 4 && thickness.Bottom >= 4);

        // Заголовок остаётся нулевым: перетаскивание окна делает WindowMoveBehavior, и ненулевая
        // высота отобрала бы у него верхнюю полосу вместе со всем, что на ней стоит.
        Assert.Equal(0, caption);
        Assert.False(gripsLeft, "старый слой полосок остался в разметке");
    }

    [Fact]
    public void The_window_answers_with_a_minimum_it_can_actually_be_laid_out_at()
    {
        // Отвечая на WM_GETMINMAXINFO, перехватчик ставит handled — и этим отключает штатную
        // обработку WPF, которая переносила MinWidth в ptMinTrackSize. Пока он не заполнял эти
        // два поля сам, минимума у окна не было: система давала сжать его до своего собственного,
        // и разметка обрезалась. Спрашиваем окно тем же сообщением, которым спрашивает Windows.
        var (answer, expected) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Show();

            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var scale = System.Windows.Interop.HwndSource.FromHwnd(handle)!.CompositionTarget!.TransformToDevice;
            var want = (
                x: (int)Math.Ceiling(window.MinWidth * scale.M11),
                y: (int)Math.Ceiling(window.MinHeight * scale.M22));

            var got = AskForMinMax(handle);
            window.Close();
            return (got, want);
        });

        Assert.Equal(expected.x, answer.ptMinTrackSize.x);
        Assert.Equal(expected.y, answer.ptMinTrackSize.y);

        // И это не случайно совпавший ноль: минимум должен быть настоящим.
        Assert.True(answer.ptMinTrackSize.x >= 800);
        Assert.True(answer.ptMinTrackSize.y >= 500);
    }

    [Fact]
    public void The_minimum_stays_the_same_size_on_screen_at_every_ui_scale()
    {
        // UiScale масштабирует интерфейс подделанным DPI и заодно переписывает MinWidth окна
        // (850 / factor). Минимум обязан считаться через CompositionTarget.TransformToDevice,
        // который этот подделанный DPI отражает: иначе при 250 % окно либо переставало сжиматься
        // до разумного размера, либо сжималось до нечитаемого.
        var (atHundred, atTwoFifty) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            window.Show();
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var root = (FrameworkElement)window.FindName("ScaledRoot");

            try
            {
                UiScale.Apply(window, root, 100);
                var hundred = AskForMinMax(handle).ptMinTrackSize;

                UiScale.Apply(window, root, 250);
                var big = AskForMinMax(handle).ptMinTrackSize;
                return (hundred, big);
            }
            finally
            {
                // Статическое состояние: масштаб общий на программу, и оставленные 250 % развалили
                // бы следующий тест в этой же коллекции.
                UiScale.Apply(window, root, 100);
                window.Close();
            }
        });

        // Допуск в пиксель — округление вверх с обеих сторон.
        Assert.InRange(atTwoFifty.x, atHundred.x - 2, atHundred.x + 2);
        Assert.InRange(atTwoFifty.y, atHundred.y - 2, atHundred.y + 2);
    }

    /// <summary>Спрашивает окно ровно тем сообщением, которым его спрашивает Windows.</summary>
    private static MINMAXINFO AskForMinMax(IntPtr handle)
    {
        var size = System.Runtime.InteropServices.Marshal.SizeOf<MINMAXINFO>();
        var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            System.Runtime.InteropServices.Marshal.StructureToPtr(default(MINMAXINFO), buffer, fDeleteOld: false);
            SendMessage(handle, WM_GETMINMAXINFO, IntPtr.Zero, buffer);
            return System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(buffer);
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
        }
    }

    private static RECT WorkArea(IntPtr handle)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(handle, 2), ref info);
        return info.rcWork;
    }

    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const int WM_GETMINMAXINFO = 0x0024;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
