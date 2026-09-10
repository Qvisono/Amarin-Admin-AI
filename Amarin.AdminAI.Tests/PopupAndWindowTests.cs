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
    public void Resize_grips_are_out_of_the_way_while_maximized()
    {
        var (maximized, restored) = _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            window.Show();

            window.WindowState = WindowState.Maximized;
            window.UpdateLayout();
            var whileMaximized = ((UIElement)window.FindName("ResizeGrips")).Visibility;

            window.WindowState = WindowState.Normal;
            window.UpdateLayout();
            var whileNormal = ((UIElement)window.FindName("ResizeGrips")).Visibility;

            window.Close();
            return (whileMaximized, whileNormal);
        });

        Assert.Equal(Visibility.Collapsed, maximized);
        Assert.Equal(Visibility.Visible, restored);
    }

    private static RECT WorkArea(IntPtr handle)
    {
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(handle, 2), ref info);
        return info.rcWork;
    }

    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;

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
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
