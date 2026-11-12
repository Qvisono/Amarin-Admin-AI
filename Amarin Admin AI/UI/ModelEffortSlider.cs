using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Amarin.UI;

/// <summary>Дискретный уровень размышления с отдельным положением бегунка при перетаскивании.</summary>
internal sealed class ModelEffortSlider : Slider
{
    public static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(ModelEffortSlider), new PropertyMetadata(0d));

    private static readonly TimeSpan SettleDuration = TimeSpan.FromMilliseconds(110);
    private static readonly CubicEase SettleEase = CreateSettleEase();
    private Track? _track;
    private bool _previewing;
    private bool _settingLevel;
    private double _initialPointer;
    private double _grabOffset;
    private double _previewLevel;

    static ModelEffortSlider()
    {
        ValueProperty.OverrideMetadata(typeof(ModelEffortSlider),
            new FrameworkPropertyMetadata(0d, null, static (element, value) =>
            {
                var slider = (ModelEffortSlider)element;
                return slider.NearestLevel((double)value);
            }));
    }

    public ModelEffortSlider()
    {
        IsVisibleChanged += CancelWhenUnavailable;
        IsEnabledChanged += CancelWhenUnavailable;
        Unloaded += (_, _) => CancelPreview();
    }

    public double DisplayValue => (double)GetValue(DisplayValueProperty);
    internal int? PreviewIndex => _previewing && _previewLevel != Value ? (int)_previewLevel : null;
    internal event EventHandler? PreviewChanged;

    internal void SetLevel(int index, int optionCount)
    {
        var maximum = Math.Max(0, optionCount - 1);
        if (Maximum != maximum || Value != index)
        {
            CancelPreview();
            _settingLevel = true;
            try
            {
                SetCurrentValue(MaximumProperty, (double)maximum);
                SetCurrentValue(ValueProperty, (double)index);
            }
            finally
            {
                _settingLevel = false;
            }
        }

        SetCurrentValue(IsEnabledProperty, optionCount > 1);
    }

    public override void OnApplyTemplate()
    {
        CancelPreview();
        base.OnApplyTemplate();
        _track = GetTemplateChild("PART_Track") as Track;
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        if (_previewing)
        {
            CancelPreview();
        }
        else
        {
            MoveDisplay(newValue, animate: !_settingLevel);
        }

        base.OnValueChanged(oldValue, newValue);
    }

    protected override void OnThumbDragStarted(DragStartedEventArgs e)
    {
        base.OnThumbDragStarted(e);
        if (_track is { Thumb: { } thumb } track && ReferenceEquals(e.OriginalSource, thumb))
        {
            _initialPointer = Mouse.GetPosition(track).X;
            BeginPreview(e.HorizontalOffset - thumb.ActualWidth / 2);
        }
    }

    protected override void OnThumbDragDelta(DragDeltaEventArgs e)
    {
        if (_previewing && _track is { Thumb: { } thumb } track && ReferenceEquals(e.OriginalSource, thumb))
        {
            PreviewAt(Mouse.GetPosition(track).X, track.ActualWidth, thumb.ActualWidth);
        }
    }

    protected override void OnThumbDragCompleted(DragCompletedEventArgs e)
    {
        base.OnThumbDragCompleted(e);
        if (!_previewing || _track is not { Thumb: { } thumb } track || !ReferenceEquals(e.OriginalSource, thumb))
        {
            return;
        }

        if (e.Canceled || !IsVisible || !IsEnabled || !IsPopupOpen())
        {
            CancelPreview();
            return;
        }

        var pointer = Mouse.GetPosition(track).X;
        if (pointer == _initialPointer)
        {
            _previewLevel = Value;
        }
        else
        {
            PreviewAt(pointer, track.ActualWidth, thumb.ActualWidth);
        }

        CommitPreview();
    }

    internal void BeginPreview(double grabOffset)
    {
        if (!IsEnabled || Maximum <= Minimum)
        {
            return;
        }

        _grabOffset = grabOffset;
        _previewLevel = Value;
        _previewing = true;
    }

    internal void PreviewAt(double pointer, double trackWidth, double thumbWidth)
    {
        var travel = trackWidth - thumbWidth;
        if (!_previewing || travel <= 0)
        {
            return;
        }

        var fraction = Math.Clamp((pointer - _grabOffset - thumbWidth / 2) / travel, 0, 1);
        if (IsDirectionReversed)
        {
            fraction = 1 - fraction;
        }

        var level = NearestLevel(Minimum + fraction * (Maximum - Minimum));
        if (_previewLevel != level)
        {
            _previewLevel = level;
            MoveDisplay(level, animate: true);
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal void CommitPreview()
    {
        if (!_previewing)
        {
            return;
        }

        _previewing = false;
        if (_previewLevel == Value)
        {
            MoveDisplay(Value, animate: true);
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            SetCurrentValue(ValueProperty, _previewLevel);
        }
    }

    internal void CancelPreview()
    {
        var wasPreviewing = _previewing;
        if (wasPreviewing)
        {
            // CancelDrag сам поднимает DragCompleted, поэтому сначала сбрасываем состояние.
            _previewing = false;
            _track?.Thumb?.CancelDrag();
        }

        MoveDisplay(Value, animate: false);
        if (wasPreviewing)
        {
            PreviewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelWhenUnavailable(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            CancelPreview();
        }
    }

    private bool IsPopupOpen()
    {
        for (FrameworkElement? element = this; element is not null; element = element.Parent as FrameworkElement)
        {
            if (element is Popup popup)
            {
                return popup.IsOpen;
            }
        }

        return true;
    }

    private double NearestLevel(double value) => Math.Clamp(Math.Floor(value + 0.5), Minimum, Maximum);

    private void MoveDisplay(double value, bool animate)
    {
        var shouldAnimate = animate && UiMotion.Enabled && IsVisible;
        if (shouldAnimate && ReadLocalValue(DisplayValueProperty) is double target && target == value)
        {
            return;
        }

        var from = DisplayValue;
        BeginAnimation(DisplayValueProperty, null);
        // После FillBehavior.Stop часы возвращают значение зависимости к локальному значению.
        SetValue(DisplayValueProperty, value);
        if (shouldAnimate && from != value)
        {
            BeginAnimation(DisplayValueProperty, new DoubleAnimation(from, value, SettleDuration)
            {
                EasingFunction = SettleEase,
                FillBehavior = FillBehavior.Stop
            });
        }
    }

    private static CubicEase CreateSettleEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
