using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Сворачивает пустое и оставленное без внимания поле ввода в полоску и разворачивает его, как
/// только человек к нему тянется: освободившиеся строки отдаются переписке.
/// </summary>
/// <remarks>
/// <para>
/// Свою <c>Height</c> поле не анимирует никогда. Анимация держит значение на своём уровне
/// приоритета навсегда, и последующее <c>Height = double.NaN</c> (вернуть <c>Auto</c>) молча
/// игнорировалось бы — вложения перестали бы растить поле. Поэтому раскладку ведут изнутри:
/// высота панели кнопок, минимум строки ввода и поля, — а размер поле берёт по детям.
/// </para>
/// <para>
/// Перед записью локального значения анимация свойства снимается (<c>BeginAnimation(prop, null)</c>),
/// а у каждого перехода свой номер поколения: запоздавшее завершение прерванного сворачивания не
/// отменит разворот, который его прервал.
/// </para>
/// </remarks>
internal sealed class ComposerCompactMode
{
    private const double PillHeight = 46;
    private const double ExpandedInputMinHeight = 45;
    private const double CompactInputMinHeight = 26;
    private const double MinimumPillWidth = 260;

    private static readonly Thickness ExpandedPadding = new(14, 10, 14, 8);
    private static readonly Thickness CompactPadding = new(16, 9, 16, 9);
    private static readonly Thickness ExpandedInputMargin = new(0, 0, 0, 6);
    private static readonly Thickness CompactInputMargin = new(0, 0, 0, 0);

    private static readonly Duration Transition = new(TimeSpan.FromMilliseconds(210));

    private readonly Window _window;
    private readonly Border _composer;
    private readonly RowDefinition _composerRow;
    private readonly Grid _layout;
    private readonly Grid _inputRow;
    private readonly RowDefinition _inputRowDef;
    private readonly Grid _toolbar;
    private readonly TextBlock _placeholder;
    private readonly TextBox _input;
    private readonly DispatcherTimer _timer = new();

    // Снимаются с разметки один раз, до первой анимации. Возврат именно к ним, а не к NaN и не к
    // числу, повторённому в этом файле, не даёт циклу «свернуть — развернуть» тихо менять размер поля.
    private readonly double _expandedToolbarHeight;
    private readonly double _expandedComposerMaxWidth;
    private readonly double _expandedRowMinHeight;

    private AppearanceSettings _settings = new();
    private bool _collapsed;
    private bool _hasAttachments;
    private bool _pointerNear;
    private int _generation;

    public ComposerCompactMode(
        Window window,
        Border composer,
        RowDefinition composerRow,
        Grid layout,
        Grid inputRow,
        RowDefinition inputRowDef,
        Grid toolbar,
        TextBlock placeholder,
        TextBox input)
    {
        _window = window;
        _composer = composer;
        _composerRow = composerRow;
        _layout = layout;
        _inputRow = inputRow;
        _inputRowDef = inputRowDef;
        _toolbar = toolbar;
        _placeholder = placeholder;
        _input = input;

        _expandedToolbarHeight = double.IsNaN(toolbar.Height) ? 36 : toolbar.Height;
        _expandedComposerMaxWidth = double.IsPositiveInfinity(composer.MaxWidth) ? 1070 : composer.MaxWidth;
        _expandedRowMinHeight = composerRow.MinHeight;

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();

            // Проверяем заново: за время ожидания человек мог вернуть указатель или начать печатать.
            if (!_collapsed && WantsCollapse())
            {
                Collapse(_settings.AnimationsEnabled);
            }
        };

        _input.TextChanged += (_, _) => Evaluate();
        _toolbar.IsKeyboardFocusWithinChanged += (_, _) => Evaluate();
        _composer.MouseEnter += (_, _) =>
        {
            if (_settings.CompactHoverEnabled)
            {
                SetPointerNear(true);
            }
        };

        // Щелчок по полоске разворачивает её всегда — это осознанное действие, а не то же
        // самое, что проведённый мимо указатель, и настройка реакции на мышь его не касается.
        // Preview: нажатие надо поймать раньше, чем его разберут текстовое поле и кнопки внутри.
        _composer.PreviewMouseDown += (_, _) => Unfold();
        _window.PreviewMouseMove += OnPreviewMouseMove;
        _window.MouseLeave += (_, _) => SetPointerNear(false);
        _window.Deactivated += (_, _) => SetPointerNear(false);
        _window.Activated += (_, _) => Evaluate();

        // Перетаскиваемый файл разворачивает поле раньше, чем долетит до него: полоска уже и ниже
        // цели, в которую метят. AllowDrop на окне нужен, чтобы события перетаскивания вообще
        // приходили, пока указатель над чатом.
        _window.AllowDrop = true;
        _window.PreviewDragEnter += OnDragOverWindow;
        _window.PreviewDragOver += OnDragOverWindow;
    }

    /// <summary>Свёрнуто ли поле сейчас. Для тестов.</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>
    /// Всё, что держит поле развёрнутым, в одном чистом решении — его проверяют без окна.
    /// </summary>
    /// <param name="toolbarFocused">
    /// Фокус на панели кнопок (вложение, выбор модели, отправка). Фокус в самом <em>поле</em>
    /// развёрнутым его не держит намеренно: пустое поле с курсором — как раз тот простой, который
    /// стоит свернуть, а поле остаётся внутри полоски и сохраняет фокус и каретку. Панель же при
    /// сворачивании прячется — фокус вылетел бы в окно, а открытая ею выпадашка закрылась бы.
    /// </param>
    /// <param name="pointerReacts">
    /// Полоска слушает мышь. Если нет, <paramref name="pointerNear"/> в решении не участвует.
    /// </param>
    /// <remarks>
    /// Идущий ход поводом держать поле открытым <em>не</em> считается: пока модель пишет, панель
    /// всё равно выключена, а растёт на экране ответ — тут освобождённые строки нужнее всего.
    /// </remarks>
    public static bool ShouldCollapse(
        bool enabled,
        bool isEmpty,
        bool toolbarFocused,
        bool hasAttachments,
        bool pointerNear,
        bool pointerReacts = true) =>
        enabled && isEmpty && !toolbarFocused && !hasAttachments && (!pointerNear || !pointerReacts);

    public void Apply(AppearanceSettings settings)
    {
        _settings = settings ?? new AppearanceSettings();
        _timer.Interval = TimeSpan.FromMilliseconds(_settings.CompactDelayMs);

        if (!_settings.CompactHoverEnabled)
        {
            _pointerNear = false;
        }

        if (!_settings.CompactComposer && _collapsed)
        {
            Expand(animate: false);
        }
        else if (!_collapsed)
        {
            // Поле уже развёрнуто — подхватываем новый радиус углов.
            Animate(_composer, AnimatableCorner.RadiusProperty, CurrentRadius(), TargetRadius(), animate: false);
        }

        Evaluate();
    }

    /// <summary>
    /// Ход начался или кончился. Сам по себе он поле не держит, но это повод перепроверить:
    /// кнопка отправки, теряя фокус на старте хода, часто и делает поле сворачиваемым.
    /// </summary>
    public void SetBusy(bool busy) => Evaluate();

    /// <summary>
    /// Разворачивает полоску по прямому действию пользователя — щелчку по ней.
    /// </summary>
    /// <remarks>
    /// Отсчёт до сворачивания начинается заново, поэтому после щелчка есть время добраться до
    /// кнопок на панели. Пустое поле всё равно свернётся, когда время выйдет: держать его
    /// открытым из-за одного лишь курсора внутри — не то поведение, которого от него ждут.
    /// </remarks>
    public void Unfold()
    {
        if (!_settings.CompactComposer)
        {
            return;
        }

        if (_collapsed)
        {
            Expand(_settings.AnimationsEnabled);
        }

        _timer.Stop();
        _timer.Start();
    }

    public void SetHasAttachments(bool hasAttachments)
    {
        _hasAttachments = hasAttachments;
        Evaluate();
    }

    private bool WantsCollapse() => ShouldCollapse(
        _settings.CompactComposer,
        string.IsNullOrEmpty(_input.Text),
        _toolbar.IsKeyboardFocusWithin,
        _hasAttachments,
        _pointerNear,
        _settings.CompactHoverEnabled);

    /// <summary>Перечитывает все условия: взводит отложенное сворачивание или сразу разворачивает.</summary>
    public void Evaluate()
    {
        if (!WantsCollapse())
        {
            _timer.Stop();
            if (_collapsed)
            {
                Expand(_settings.AnimationsEnabled);
            }

            return;
        }

        if (_collapsed || _timer.IsEnabled)
        {
            return;
        }

        // Сворачивание всегда с задержкой: складываясь в тот же миг, как указатель отошёл, поле дёргалось бы.
        _timer.Start();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_settings.CompactComposer)
        {
            return;
        }

        if (!_settings.CompactHoverEnabled)
        {
            // Реакция на мышь выключена: гасим взведённый флаг, иначе поле осталось бы
            // развёрнутым ровно там, где указатель стоял в момент выключения.
            SetPointerNear(false);
            return;
        }

        var position = e.GetPosition(_composer);
        var size = _composer.RenderSize;
        var margin = _settings.CompactHoverRadius;

        var near = position.X >= -margin
                   && position.Y >= -margin
                   && position.X <= size.Width + margin
                   && position.Y <= size.Height + margin;

        SetPointerNear(near);
    }

    private void OnDragOverWindow(object sender, DragEventArgs e)
    {
        if (!_collapsed)
        {
            return;
        }

        SetPointerNear(true);
    }

    private void SetPointerNear(bool near)
    {
        if (_pointerNear == near)
        {
            return;
        }

        _pointerNear = near;
        Evaluate();
    }

    // ───────────────────────── переходы ─────────────────────────

    private void Collapse(bool animate)
    {
        _collapsed = true;
        var generation = ++_generation;

        // Панель остаётся видимой до конца анимации: её обрезает сжимающаяся рамка, а не гасит первый кадр.
        var width = TargetPillWidth();

        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _input.VerticalContentAlignment = VerticalAlignment.Center;

        Animate(_toolbar, FrameworkElement.HeightProperty, _expandedToolbarHeight, 0, animate);
        Animate(_inputRowDef, RowDefinition.MinHeightProperty, ExpandedInputMinHeight, CompactInputMinHeight, animate);
        AnimateThickness(_inputRow, FrameworkElement.MarginProperty, ExpandedInputMargin, CompactInputMargin, animate);
        AnimateThickness(_layout, FrameworkElement.MarginProperty, ExpandedPadding, CompactPadding, animate);
        Animate(_composerRow, RowDefinition.MinHeightProperty, _composerRow.MinHeight, PillHeight + 20, animate);
        Animate(_composer, FrameworkElement.MaxWidthProperty, _composer.ActualWidth, width, animate);
        Animate(_composer, AnimatableCorner.RadiusProperty, CurrentRadius(), PillHeight / 2, animate, () =>
        {
            if (generation != _generation)
            {
                return;
            }

            // Убираем панель только в покое: раньше фокус вылетел бы в окно посреди анимации.
            _toolbar.Visibility = Visibility.Collapsed;
        });

        if (!animate)
        {
            _toolbar.Visibility = Visibility.Collapsed;
        }
    }

    private void Expand(bool animate)
    {
        _collapsed = false;
        _generation++;

        _toolbar.Visibility = Visibility.Visible;
        _placeholder.VerticalAlignment = VerticalAlignment.Top;
        _input.VerticalContentAlignment = VerticalAlignment.Top;

        // К высоте из разметки, а НЕ к NaN: по содержимому панель встала бы по самой высокой
        // кнопке (32) и потеряла четыре точки до конца сеанса — кнопки «меняли размер» после
        // первого сворачивания.
        Animate(_toolbar, FrameworkElement.HeightProperty, _toolbar.ActualHeight, _expandedToolbarHeight, animate);
        Animate(_inputRowDef, RowDefinition.MinHeightProperty, _inputRowDef.MinHeight, ExpandedInputMinHeight, animate);
        AnimateThickness(_inputRow, FrameworkElement.MarginProperty, _inputRow.Margin, ExpandedInputMargin, animate);
        AnimateThickness(_layout, FrameworkElement.MarginProperty, _layout.Margin, ExpandedPadding, animate);
        Animate(_composerRow, RowDefinition.MinHeightProperty, _composerRow.MinHeight, _expandedRowMinHeight, animate);
        Animate(_composer, FrameworkElement.MaxWidthProperty, _composer.ActualWidth, _expandedComposerMaxWidth, animate);
        Animate(_composer, AnimatableCorner.RadiusProperty, CurrentRadius(), TargetRadius(), animate);
    }

    private double TargetPillWidth()
    {
        var available = _composer.ActualWidth > 0 ? _composer.ActualWidth : 1070;
        var percent = Math.Clamp(_settings.CompactWidthPercent, 30, 100) / 100.0;
        return Math.Max(MinimumPillWidth, available * percent);
    }

    private double CurrentRadius()
    {
        var current = AnimatableCorner.GetRadius(_composer);
        return double.IsNaN(current) ? _composer.CornerRadius.TopLeft : current;
    }

    private double TargetRadius() => Math.Clamp(_settings.CornerRadius, 0, 20);

    /// <summary>
    /// Начинает с <paramref name="from"/>, снятого с элемента вживую, а не с номинального
    /// состояния: прерванный переход разворачивается оттуда, где он на самом деле.
    /// </summary>
    private static void Animate(
        DependencyObject target,
        DependencyProperty property,
        double from,
        double to,
        bool animate,
        Action? completed = null)
    {
        if (target is not IAnimatable animatable)
        {
            return;
        }

        animatable.BeginAnimation(property, null);
        if (!animate)
        {
            target.SetValue(property, to);
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = double.IsNaN(from) ? null : from,
            To = to,
            Duration = Transition,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            animatable.BeginAnimation(property, null);
            target.SetValue(property, to);
            completed?.Invoke();
        };

        animatable.BeginAnimation(property, animation);
    }

    private static void AnimateThickness(
        FrameworkElement target,
        DependencyProperty property,
        Thickness from,
        Thickness to,
        bool animate)
    {
        target.BeginAnimation(property, null);
        if (!animate)
        {
            target.SetValue(property, to);
            return;
        }

        var animation = new ThicknessAnimation
        {
            From = from,
            To = to,
            Duration = Transition,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };

        animation.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };

        target.BeginAnimation(property, animation);
    }
}
