using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Amarin.UI;

/// <summary>
/// Короткие переходы интерфейса: проявление страницы, въезд подстраницы, ход бегунка тумблера.
/// </summary>
/// <remarks>
/// Одна точка на всю программу, чтобы галка «Анимации» в Appearance гасила их все разом, а не
/// каждую по своему флагу. Переходы короткие (до 160 мс) и не держат ввод: страница, на которую
/// ещё «въезжают», уже кликабельна.
/// </remarks>
internal static class UiMotion
{
    /// <summary>Повторяет <c>AppearanceSettings.AnimationsEnabled</c>; ставит окно при применении оформления.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly IEasingFunction Ease = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private static CubicEase Frozen(CubicEase ease)
    {
        ease.Freeze();
        return ease;
    }

    /// <summary>Проявление с небольшим сдвигом: <paramref name="dx"/> и <paramref name="dy"/> — откуда въезжает.</summary>
    public static void Enter(UIElement element, double dx = 0, double dy = 0, int milliseconds = 150)
    {
        if (!Enabled || !element.IsVisible)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = Ease });
        if (dx == 0 && dy == 0)
        {
            return;
        }

        // Свой TranslateTransform, а не общий: элемент мог прийти со своим RenderTransform
        // (поворот шеврона), и подменить его значило бы сломать чужую разметку.
        if (element.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            element.RenderTransform = shift;
        }

        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, duration) { EasingFunction = Ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, duration) { EasingFunction = Ease });
    }

    // ───────────────────────── Проявление страницы ─────────────────────────

    /// <summary>
    /// Страница настроек проявляется, когда становится видимой: смена пункта навигации без
    /// перехода выглядела бы скачком, а с ним — как нажатие, на которое ответили.
    /// </summary>
    public static readonly DependencyProperty FadeOnShowProperty = DependencyProperty.RegisterAttached(
        "FadeOnShow", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnFadeOnShowChanged));

    public static bool GetFadeOnShow(DependencyObject element) => (bool)element.GetValue(FadeOnShowProperty);

    public static void SetFadeOnShow(DependencyObject element, bool value) => element.SetValue(FadeOnShowProperty, value);

    private static void OnFadeOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsVisibleChanged -= FadeOnVisible;
        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += FadeOnVisible;
        }
    }

    private static void FadeOnVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is UIElement element && (bool)e.NewValue)
        {
            Enter(element, dy: 6, milliseconds: 140);
        }
    }

    // ───────────────────────── Ход бегунка тумблера ─────────────────────────

    /// <summary>
    /// Бегунок тумблера едет, а не прыгает. Анимация вешается на событие смены, а не на триггер
    /// шаблона: триггер с EnterActions срабатывает и при первом применении шаблона, и каждый
    /// включённый тумблер «переключался» бы на глазах при каждом открытии настроек.
    /// </summary>
    /// <remarks>
    /// Положение задаёт шаблон (выравнивание бегунка), а здесь только сдвиг, который гаснет до
    /// нуля: если анимацию выключить или она не успеет, бегунок всё равно стоит там, где надо.
    /// </remarks>
    public static readonly DependencyProperty SlideThumbProperty = DependencyProperty.RegisterAttached(
        "SlideThumb", typeof(bool), typeof(UiMotion), new PropertyMetadata(false, OnSlideThumbChanged));

    public static bool GetSlideThumb(DependencyObject element) => (bool)element.GetValue(SlideThumbProperty);

    public static void SetSlideThumb(DependencyObject element, bool value) => element.SetValue(SlideThumbProperty, value);

    /// <summary>Сколько бегунок проходит по дорожке: 36 − 14 − 3·2.</summary>
    private const double ThumbTravel = 16;

    private static void OnSlideThumbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton toggle)
        {
            return;
        }

        toggle.Checked -= SlideOnChange;
        toggle.Unchecked -= SlideOnChange;
        if ((bool)e.NewValue)
        {
            toggle.Checked += SlideOnChange;
            toggle.Unchecked += SlideOnChange;
        }
    }

    private static void SlideOnChange(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle || !Enabled || !toggle.IsVisible ||
            toggle.Template?.FindName("Thumb", toggle) is not FrameworkElement thumb)
        {
            return;
        }

        // Включили — бегунок уже справа, и въезжает туда из левого положения; выключили — наоборот.
        var from = toggle.IsChecked == true ? -ThumbTravel : ThumbTravel;
        if (thumb.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
        {
            shift = new TranslateTransform();
            thumb.RenderTransform = shift;
        }

        shift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(130)) { EasingFunction = Ease });
    }
}
