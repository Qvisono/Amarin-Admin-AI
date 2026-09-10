using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Folds the composer into a pill while it is empty and unattended, and unfolds it the instant
/// the user shows any interest. The point is to hand the freed rows back to the conversation.
/// </summary>
/// <remarks>
/// <para>
/// The composer's own <c>Height</c> is never animated. An animation holds its value at the
/// Animation precedence level for good, so a later <c>Height = double.NaN</c> to restore
/// <c>Auto</c> sizing is silently ignored and attachments can no longer grow the box. Instead the
/// layout is driven from the inside — the toolbar row's height, the input row's minimum and the
/// paddings — and the composer keeps sizing itself to its children.
/// </para>
/// <para>
/// Every animated property is cleared with <c>BeginAnimation(prop, null)</c> before a local value
/// is written, and each transition carries a generation number so a stale completion callback
/// from an interrupted collapse cannot undo the expand that interrupted it.
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

    // Read off the markup once, at construction, before anything has been animated. Restoring an
    // animated element to a literal written here — rather than to NaN or to a number repeated in
    // this file — is what keeps a collapse/expand cycle from quietly resizing the composer.
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

            // Re-checked rather than assumed: the idle period is long enough for the user to have
            // moved the pointer back or started typing since the timer was armed.
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

        // A drag has to unfold the composer before it reaches it: the pill is both narrower and
        // shorter than the drop target the user is aiming at. Marking the window as a drop target
        // is what makes drag events fire at all while the pointer is over the chat.
        _window.AllowDrop = true;
        _window.PreviewDragEnter += OnDragOverWindow;
        _window.PreviewDragOver += OnDragOverWindow;
    }

    /// <summary>Collapsed right now. Exposed for tests.</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>
    /// Pure decision function: everything that keeps the composer open, in one place, so the state
    /// machine can be tested without a window.
    /// </summary>
    /// <param name="toolbarFocused">
    /// Keyboard focus is on the toolbar — the attach button, the model picker or send. Note that
    /// focus in the <em>text box</em> deliberately does not hold the composer open: an empty,
    /// merely-focused field is exactly the idle state worth folding away, and the text box stays
    /// on screen inside the pill, so it keeps its focus and its caret across the transition.
    /// The toolbar is different: collapsing hides it, which would throw focus out to the window
    /// and close whatever popup it is driving.
    /// </param>
    /// <remarks>
    /// A turn being in progress is deliberately <em>not</em> a reason to stay open. While the
    /// model streams, the whole toolbar is disabled anyway and the answer is the thing growing on
    /// screen — that is precisely when the freed rows are worth the most.
    /// </remarks>
    /// <param name="pointerReacts">
    /// The pill listens to the mouse. Off means <paramref name="pointerNear"/> is not a reason to
    /// stay open — the pointer is simply not part of the decision any more.
    /// </param>
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
            // Picking up a corner-radius change while the composer is already open.
            Animate(_composer, AnimatableCorner.RadiusProperty, CurrentRadius(), TargetRadius(), animate: false);
        }

        Evaluate();
    }

    /// <summary>
    /// A turn started or finished. Being busy is not itself a reason to stay unfolded, but the
    /// transition is a good moment to re-check: the send button losing focus as the turn starts
    /// is often exactly what makes the composer foldable.
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

    /// <summary>Re-reads every input and either schedules a collapse or expands immediately.</summary>
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

        // Collapsing is always on a delay: folding the instant the pointer drifts off would make
        // the composer feel twitchy.
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

        // The toolbar keeps its Visible flag until the animation lands, so it stays painted (and
        // clipped by the shrinking border) instead of vanishing on the first frame.
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

            // Only now, at rest, is the toolbar taken out of the tree — collapsing it earlier
            // would push keyboard focus out to the window mid-animation.
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

        // Back to the height the markup declares -- NOT to NaN. The toolbar is a fixed-height row
        // by design; letting it size to content instead lands it on the tallest child (32 for the
        // 32px buttons) and the whole strip loses four pixels for the rest of the session, which
        // reads as the send button and its neighbours changing size after the first fold.
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
    /// Starts from <paramref name="from"/> — read live from the element, never from the nominal
    /// state — so interrupting a transition reverses it from where it actually is.
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
