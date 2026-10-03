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

    // ───────────────────────── Раскрытие и свёртывание строк ─────────────────────────

    /// <summary>
    /// Строка вырастает от <paramref name="from"/> до своей высоты и проявляется — раскрытие
    /// папки в списке чатов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Анимируется сама высота, а не масштаб: строка обрезается, а не сплющивается, и соседи
    /// снизу едут вниз вместе с ней. Конечная высота — та, что строка получила на раскладке
    /// (шаблон задаёт её триггерами, и до раскладки её не прочесть). По окончании анимация
    /// снимается, и высоту снова задаёт стиль.
    /// </para>
    /// <para>
    /// Начальные высота и прозрачность ставятся и локальным значением: анимация вступает в силу
    /// со следующего тика часов, и до него строка разложилась бы в полную высоту — первый кадр
    /// раскрытия мигнул бы уже раскрытой папкой.
    /// </para>
    /// </remarks>
    public static void Grow(FrameworkElement element, double from, int milliseconds = 180)
    {
        var to = element.ActualHeight;
        if (!Enabled || to <= from)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        element.ClipToBounds = true;
        element.Height = from;
        element.Opacity = 0;
        var height = new DoubleAnimation(from, to, duration) { EasingFunction = Ease };
        height.Completed += (_, _) => Settle(element);
        element.BeginAnimation(FrameworkElement.HeightProperty, height);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = Ease });
    }

    /// <summary>
    /// Строка сворачивается до <paramref name="to"/> и гаснет, а потом зовёт <paramref name="done"/> —
    /// свёртывание папки. Убрать строку из списка — дело того, кто зовёт.
    /// </summary>
    public static void Shrink(FrameworkElement element, double to, Action done, int milliseconds = 160)
    {
        ArgumentNullException.ThrowIfNull(done);
        var from = element.ActualHeight;
        if (!Enabled || from <= to)
        {
            done();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        element.ClipToBounds = true;
        var height = new DoubleAnimation(from, to, duration) { EasingFunction = Ease };
        height.Completed += (_, _) => done();
        element.BeginAnimation(FrameworkElement.HeightProperty, height);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, duration) { EasingFunction = Ease });
    }

    /// <summary>
    /// Снимает с элемента рост или свёртывание: он сразу встаёт в свою высоту. Локальные высота
    /// и прозрачность, поставленные на время хода, тоже снимаются — их задаёт стиль.
    /// </summary>
    public static void Settle(FrameworkElement element)
    {
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.ClearValue(FrameworkElement.HeightProperty);
        element.ClearValue(UIElement.OpacityProperty);
        element.ClearValue(UIElement.ClipToBoundsProperty);
    }

    /// <summary>Поворот (шеврон папки) доезжает до угла, а не прыгает; без анимаций — встаёт сразу.</summary>
    public static void Turn(RotateTransform rotation, double angle, bool animate, int milliseconds = 180)
    {
        if (rotation.IsFrozen)
        {
            return;
        }

        if (!Enabled || !animate || Math.Abs(rotation.Angle - angle) < 0.5)
        {
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = angle;
            return;
        }

        var turn = new DoubleAnimation(rotation.Angle, angle, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = Ease, FillBehavior = FillBehavior.Stop };
        rotation.Angle = angle;
        rotation.BeginAnimation(RotateTransform.AngleProperty, turn);
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
