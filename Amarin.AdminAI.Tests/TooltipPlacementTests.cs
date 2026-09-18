using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Подсказка у значка «не от администратора» при масштабе больше 100 % оставалась маленькой
/// и уезжала от значка.
/// </summary>
/// <remarks>
/// Замеры показали, в чём дело: окно подсказки WPF заводит при DPI монитора и на подделанный
/// <c>WM_DPICHANGED</c>, который шлёт <see cref="UiScale"/>, не отзывается. Карточка так и
/// оставалась 230 пикселей при 100, 150 и 250 % — то есть на укрупнённом интерфейсе выглядела
/// втрое мельче положенного. Размещение же считается от ширины карточки, поэтому у края экрана
/// слишком узкая карточка вставала ещё и не туда.
/// </remarks>
public sealed class TooltipPlacementMathTests
{
    private static readonly Point NoOffset = new(0, 0);

    /// <summary>
    /// Обе величины приходят в единицах окна подсказки, поэтому при 150 % значок шириной 32
    /// приходит как 48. Формула центрирует прямо в этих единицах — приводить нечего.
    /// </summary>
    [Theory]
    [InlineData(100, 32, 230)]
    [InlineData(150, 48, 345)]
    [InlineData(250, 80, 575)]
    public void The_card_is_centred_under_the_icon(int percent, double icon, double card)
    {
        var placed = UiScale.PlaceBelowCenter(new Size(card, 55 * percent / 100.0), new Size(icon, icon), NoOffset);

        Assert.Single(placed);
        Assert.Equal(icon / 2.0, placed[0].Point.X + card / 2.0, 3);
        Assert.Equal(icon, placed[0].Point.Y, 3);
    }

    /// <summary>Значения из старого теста — поведение при 100 % не изменилось.</summary>
    [Fact]
    public void The_hundred_percent_case_is_untouched()
    {
        var placed = UiScale.PlaceBelowCenter(new Size(230, 80), new Size(32, 32), new Point(0, 6));

        Assert.Equal(-99, placed[0].Point.X, 3);
        Assert.Equal(38, placed[0].Point.Y, 3);
    }
}

/// <summary>
/// Живое окно: карточка и растёт вместе с масштабом, и остаётся под значком.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TooltipPlacementWindowTests
{
    private readonly WpfFixture _wpf;

    public TooltipPlacementWindowTests(WpfFixture wpf) => _wpf = wpf;

    private sealed record Shot(double IconCentre, double TipCentre, double TipWidth);

    private Shot Measure(int percent) =>
        _wpf.Ui.Invoke(() =>
        {
            var window = new MainWindow();
            new WindowInteropHelper(window).EnsureHandle();

            // Подальше от правого края экрана, чтобы карточку не подпирала его граница: WPF тогда
            // двигает попап внутрь, и мерялось бы это, а не наша формула. Manual обязателен —
            // разметка просит CenterScreen, и он бы перекрыл Left.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 60;
            window.Top = 60;
            window.Show();
            var root = (FrameworkElement)window.FindName("ScaledRoot");
            var warn = (FrameworkElement)window.FindName("Warn");

            try
            {
                UiScale.Apply(window, root, percent);

                // Значок скрыт, когда программа и правда запущена от администратора.
                warn.Visibility = Visibility.Visible;
                UiScale.AttachCenteredBelowTooltip(warn);
                window.UpdateLayout();

                var tooltip = (ToolTip)warn.ToolTip;
                tooltip.PlacementTarget = warn;
                tooltip.IsOpen = true;
                try
                {
                    tooltip.UpdateLayout();
                    var source = (HwndSource)PresentationSource.FromVisual(tooltip)!;
                    GetWindowRect(source.Handle, out var tip);

                    var left = warn.PointToScreen(new Point(0, 0));
                    var right = warn.PointToScreen(new Point(warn.ActualWidth, 0));
                    return new Shot(
                        (left.X + right.X) / 2.0,
                        (tip.Left + tip.Right) / 2.0,
                        tip.Right - tip.Left);
                }
                finally
                {
                    tooltip.IsOpen = false;
                }
            }
            finally
            {
                // Масштаб — статика на всю программу: оставленные 150 % развалили бы соседний тест.
                UiScale.Apply(window, root, 100);
                window.Close();
            }
        });

    [Theory]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(250)]
    public void The_warning_tooltip_stays_centred_under_its_icon(int percent)
    {
        var shot = Measure(percent);

        // Допуск — округление до целого пикселя с обеих сторон плюс рамка окна подсказки.
        Assert.InRange(shot.TipCentre, shot.IconCentre - 4, shot.IconCentre + 4);
    }

    /// <summary>
    /// То самое «остаётся маленькой»: до правки ширина была 230 при любом масштабе.
    /// Теперь карточка растёт — но не в меру интерфейса, а до потолка.
    /// </summary>
    [Fact]
    public void The_card_grows_with_the_ui_scale_but_only_up_to_the_ceiling()
    {
        var hundred = Measure(100).TipWidth;
        var hundredFifty = Measure(150).TipWidth;
        var twoFifty = Measure(250).TipWidth;

        Assert.True(hundredFifty > hundred, "при 150 % карточка обязана стать крупнее");
        Assert.InRange(hundredFifty, hundred, hundred * UiScale.MaxTooltipScale + 2);

        // При 250 % карточка на 230 заняла бы 575 пикселей — больше половины окна.
        Assert.InRange(twoFifty, hundred, hundred * UiScale.MaxTooltipScale + 2);
    }

    /// <summary>
    /// Мельче заводского размера подсказка не становится: при 80 % читать её было бы нечем.
    /// </summary>
    [Fact]
    public void The_card_never_shrinks_below_its_normal_size() =>
        Assert.InRange(Measure(80).TipWidth, Measure(100).TipWidth - 2, double.MaxValue);

    /// <summary>
    /// Потолок не должен оторвать плашку от значка: ширина карточки участвует в расчёте
    /// центра, и «маленькая карточка у большого значка» — ровно тот случай, который эту
    /// подсказку и уводил в сторону.
    /// </summary>
    [Theory]
    [InlineData(80)]
    [InlineData(150)]
    [InlineData(250)]
    public void A_capped_card_still_hugs_its_icon(int percent)
    {
        var shot = Measure(percent);

        Assert.InRange(shot.TipCentre, shot.IconCentre - 4, shot.IconCentre + 4);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
}
