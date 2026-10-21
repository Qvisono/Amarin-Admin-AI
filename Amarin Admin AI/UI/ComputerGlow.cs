using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Amarin.Computer;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Свечение по краям всех мониторов, пока агент держит мышь: видно издалека, что компьютером
/// сейчас управляет ИИ.
/// </summary>
/// <remarks>
/// <para>
/// Четыре узкие полосы на монитор, а не одно прозрачное окно во весь экран: прозрачное окно WPF
/// перерисовывается целиком на каждом кадре анимации, и на мониторе 4K это заметная доля
/// процессора. Полосы стоят по полным границам монитора — поверх панели задач тоже.
/// </para>
/// <para>
/// Сквозные для мыши (<c>WS_EX_TRANSPARENT</c>): иначе нажатие у края, в панель задач, досталось бы
/// полосе, а правило «окна самой программы не трогать» отказало бы агенту. Не попадают в снимки
/// (<c>WDA_EXCLUDEFROMCAPTURE</c>): модель видит экран без них.
/// </para>
/// </remarks>
internal sealed class ComputerGlow
{
    /// <summary>Толщина свечения в точках при масштабе 100 %.</summary>
    private const double Thickness = 28;

    private readonly List<GlowStrip> _strips = [];

    internal IReadOnlyList<Window> Strips => _strips;

    /// <summary>Зажигает свечение на всех мониторах.</summary>
    public void Show() => Show(WindowsComputer.AllMonitors());

    /// <summary>Зажигает свечение на этих мониторах. Параметром — ради тестов.</summary>
    internal void Show(IEnumerable<ComputerMonitor> monitors)
    {
        Hide();
        foreach (var monitor in monitors)
        {
            var bounds = monitor.Bounds;
            var thickness = (int)Math.Round(Thickness * monitor.Scale);
            Add(new ScreenRect(bounds.Left, bounds.Top, bounds.Width, thickness), Edge.Top);
            Add(new ScreenRect(bounds.Left, bounds.Bottom - thickness, bounds.Width, thickness), Edge.Bottom);
            Add(new ScreenRect(bounds.Left, bounds.Top, thickness, bounds.Height), Edge.Left);
            Add(new ScreenRect(bounds.Right - thickness, bounds.Top, thickness, bounds.Height), Edge.Right);
        }
    }

    /// <summary>
    /// Снова поверх всех: нажатие в панель задач поднимает её над остальными окнами «поверх всех».
    /// </summary>
    public void KeepOnTop()
    {
        foreach (var strip in _strips)
        {
            strip.Place();
        }
    }

    public void Hide()
    {
        foreach (var strip in _strips)
        {
            strip.Close();
        }

        _strips.Clear();
    }

    private void Add(ScreenRect area, Edge edge)
    {
        var strip = new GlowStrip(area, edge);
        strip.Place();
        strip.Show();
        _strips.Add(strip);
    }

    private enum Edge
    {
        Top,
        Bottom,
        Left,
        Right
    }

    /// <summary>Одна полоса: цвет акцента темы, к середине экрана сходит на нет.</summary>
    private sealed class GlowStrip : Window
    {
        private readonly ScreenRect _area;

        public GlowStrip(ScreenRect area, Edge edge)
        {
            _area = area;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Focusable = false;
            IsHitTestVisible = false;
            Title = "Amarin";

            // Цвет — из темы, растушёвка — маской прозрачности: у маски значит только альфа, и
            // правило «цвета только через ресурсы» она не нарушает.
            var glow = new Border { OpacityMask = Fade(edge) };
            glow.SetResourceReference(Border.BackgroundProperty, "Accent.Fill");
            Content = glow;
            Breathe(glow);
        }

        /// <summary>Ставит полосу на её место поверх всех окон, в физических пикселях.</summary>
        public void Place()
        {
            var handle = new WindowInteropHelper(this).EnsureHandle();
            _ = ComputerNative.SetWindowPos(handle, ComputerNative.HwndTopmost, _area.Left, _area.Top, _area.Width, _area.Height,
                ComputerNative.SwpNoActivate | ComputerNative.SwpShowWindow);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var handle = new WindowInteropHelper(this).Handle;
            ComputerNative.Overlay(handle, passThrough: true);
            HwndSource.FromHwnd(handle)?.AddHook(KeepPlace);
        }

        /// <summary>
        /// Смена масштаба монитора: WPF применил бы предложенный Windows размер, а полоса стоит в
        /// физических пикселях по границе монитора и меняться не должна.
        /// </summary>
        private IntPtr KeepPlace(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == ComputerNative.WmDpiChanged)
            {
                _ = ComputerNative.SetWindowPos(hwnd, ComputerNative.HwndTopmost, _area.Left, _area.Top, _area.Width, _area.Height,
                    ComputerNative.SwpNoActivate);
                handled = true;
            }

            return IntPtr.Zero;
        }

        private static LinearGradientBrush Fade(Edge edge)
        {
            var (start, end) = edge switch
            {
                Edge.Top => (new Point(0, 0), new Point(0, 1)),
                Edge.Bottom => (new Point(0, 1), new Point(0, 0)),
                Edge.Left => (new Point(0, 0), new Point(1, 0)),
                _ => (new Point(1, 0), new Point(0, 0))
            };
            var brush = new LinearGradientBrush
            {
                StartPoint = start,
                EndPoint = end,
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(230, 0, 0, 0), 0),
                    new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.35),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)
                ]
            };
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// «Дыхание» яркости. Частота кадров снижена: свечению плавности 24 кадров хватает, а
        /// прозрачное окно перерисовывается на каждом. Без анимаций свечение просто горит.
        /// </summary>
        private static void Breathe(UIElement glow)
        {
            if (!UiMotion.Enabled)
            {
                glow.Opacity = 0.85;
                return;
            }

            var animation = new DoubleAnimation(0.45, 1.0, TimeSpan.FromSeconds(1.6))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(animation, 24);
            glow.BeginAnimation(OpacityProperty, animation);
        }
    }
}
