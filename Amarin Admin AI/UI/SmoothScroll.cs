using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using ScrollEventArgs = System.Windows.Controls.Primitives.ScrollEventArgs;
using ScrollEventHandler = System.Windows.Controls.Primitives.ScrollEventHandler;
using ScrollEventType = System.Windows.Controls.Primitives.ScrollEventType;
using ScrollViewer = System.Windows.Controls.ScrollViewer;

namespace Amarin.UI
{
    internal static class SmoothScroll
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(SmoothScroll),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty HookProperty =
            DependencyProperty.RegisterAttached(
                "Hook",
                typeof(Hook),
                typeof(SmoothScroll));

        public static bool GetIsEnabled(DependencyObject obj) =>
            (bool)obj.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject obj, bool value) =>
            obj.SetValue(IsEnabledProperty, value);

        /// <summary>
        /// True while a flick is still being carried by inertia.
        ///
        /// The hook owns <see cref="ScrollViewer.VerticalOffset"/> for as long as this is true —
        /// it re-asserts its own position every frame — so anything else that scrolls the viewer
        /// in that window is undone on the next frame, once per frame. Callers that move the view
        /// on their own check this first and let the flick finish.
        /// </summary>
        public static bool IsAnimating(ScrollViewer viewer) =>
            ((Hook?)viewer.GetValue(HookProperty))?.IsAnimating ?? false;

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not ScrollViewer viewer)
                return;

            var old = (Hook?)viewer.GetValue(HookProperty);
            old?.Detach();
            viewer.ClearValue(HookProperty);

            if (e.NewValue is true)
            {
                var hook = new Hook(viewer);
                viewer.SetValue(HookProperty, hook);
                hook.Attach();
            }
        }

        private sealed class Hook
        {
            private const double MaxOverscroll = 60.0;
            private const double ReturnDamping = 0.85;
            private const double Friction = 5.6;
            private const double EdgeAbsorb = 0.55;
            private const double StopVelocity = 16.0;
            private const double Impulse = 9.0;
            private const double MinDt = 1.0 / 240.0;
            private const double MaxDt = 1.0 / 30.0;

            private readonly ScrollViewer _viewer;
            private readonly EventHandler _onRendering;
            private readonly MouseWheelEventHandler _onWheel;
            private readonly KeyEventHandler _onKeyDown;
            private readonly MouseButtonEventHandler _onBarMouseDown;
            private readonly ScrollEventHandler _onBarScroll;
            private readonly RoutedEventHandler _onLoaded;
            private readonly RoutedEventHandler _onUnloaded;

            private TranslateTransform? _translate;
            private ScrollContentPresenter? _presenter;
            private ScrollBar? _bar;
            private bool _attached;
            private bool _ticking;
            private bool _savedCanContentScroll;
            private bool _hadLocalCanContentScroll;
            private double _lastTime;
            private double _velocity;
            private double _virtual;
            private bool _virtualValid;

            public Hook(ScrollViewer viewer)
            {
                _viewer = viewer;
                _onRendering = OnRendering;
                _onWheel = OnWheel;
                _onKeyDown = OnKeyDown;
                _onBarMouseDown = OnBarMouseDown;
                _onBarScroll = OnBarScroll;
                _onLoaded = OnLoaded;
                _onUnloaded = OnUnloaded;
            }

            public bool IsAnimating => _ticking;

            public void Attach()
            {
                if (_attached)
                    return;
                _attached = true;

                _hadLocalCanContentScroll = _viewer.ReadLocalValue(ScrollViewer.CanContentScrollProperty)
                                            != DependencyProperty.UnsetValue;
                _savedCanContentScroll = _viewer.CanContentScroll;
                _viewer.CanContentScroll = false;
                _viewer.ClipToBounds = true;

                _viewer.PreviewMouseWheel += _onWheel;
                _viewer.PreviewKeyDown += _onKeyDown;
                _viewer.Loaded += _onLoaded;
                _viewer.Unloaded += _onUnloaded;

                if (_viewer.IsLoaded)
                    BindTemplateParts();
            }

            public void Detach()
            {
                if (!_attached)
                    return;
                _attached = false;

                StopTicking();
                UnbindTemplateParts();

                _viewer.PreviewMouseWheel -= _onWheel;
                _viewer.PreviewKeyDown -= _onKeyDown;
                _viewer.Loaded -= _onLoaded;
                _viewer.Unloaded -= _onUnloaded;

                if (_presenter is not null && ReferenceEquals(_presenter.RenderTransform, _translate))
                    _presenter.RenderTransform = null;

                _translate = null;
                _presenter = null;

                _viewer.ClearValue(UIElement.ClipToBoundsProperty);
                if (_hadLocalCanContentScroll)
                    _viewer.CanContentScroll = _savedCanContentScroll;
                else
                    _viewer.ClearValue(ScrollViewer.CanContentScrollProperty);
            }

            private void OnLoaded(object sender, RoutedEventArgs e) => BindTemplateParts();

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                // Popup unloads the viewer every close — keep the hook, just stop the loop.
                StopTicking();
                _velocity = 0;
                _virtualValid = false;
                if (_translate is not null)
                    _translate.Y = 0;
            }

            private void BindTemplateParts()
            {
                _viewer.ApplyTemplate();
                _presenter ??= FindChild<ScrollContentPresenter>(_viewer);
                if (_presenter is not null && _translate is null)
                {
                    _presenter.ClipToBounds = true;
                    _translate = new TranslateTransform();
                    _presenter.RenderTransform = _translate;
                }

                if (_bar is null)
                {
                    _bar = FindNamedScrollBar(_viewer);
                    if (_bar is not null)
                    {
                        _bar.Scroll += _onBarScroll;
                        _bar.PreviewMouseLeftButtonDown += _onBarMouseDown;
                    }
                }
            }

            private void UnbindTemplateParts()
            {
                if (_bar is null)
                    return;
                _bar.Scroll -= _onBarScroll;
                _bar.PreviewMouseLeftButtonDown -= _onBarMouseDown;
                _bar = null;
            }

            private void OnWheel(object sender, MouseWheelEventArgs e)
            {
                e.Handled = true;
                SeedVirtual();

                var notch = Math.Max(1, SystemParameters.WheelScrollLines) * 16.0;
                var pixels = -e.Delta / 120.0 * notch;
                _velocity += pixels * Impulse;
                EnsureTicking();
            }

            private void OnKeyDown(object sender, KeyEventArgs e)
            {
                switch (e.Key)
                {
                    case Key.Up:
                    case Key.Down:
                    case Key.PageUp:
                    case Key.PageDown:
                    case Key.Home:
                    case Key.End:
                    case Key.Left:
                    case Key.Right:
                        CancelInertia(snap: true);
                        break;
                }
            }

            private void OnBarMouseDown(object sender, MouseButtonEventArgs e) =>
                CancelInertia(snap: true);

            private void OnBarScroll(object sender, ScrollEventArgs e)
            {
                if (e.ScrollEventType is ScrollEventType.ThumbTrack
                    or ScrollEventType.ThumbPosition
                    or ScrollEventType.EndScroll)
                {
                    CancelInertia(snap: true);
                }
            }

            private void CancelInertia(bool snap)
            {
                _velocity = 0;
                if (snap)
                {
                    _virtual = Clamp(_viewer.VerticalOffset, 0, GetMaxOffset());
                    _virtualValid = true;
                    ApplyVisual();
                }

                if (IsSettled())
                    StopTicking();
            }

            private void OnRendering(object? sender, EventArgs e)
            {
                var now = e is RenderingEventArgs re
                    ? re.RenderingTime.TotalSeconds
                    : 0;
                var dt = _lastTime <= 0 || now <= _lastTime ? 1.0 / 120.0 : now - _lastTime;
                _lastTime = now;
                if (dt < MinDt) dt = MinDt;
                else if (dt > MaxDt) dt = MaxDt;

                var max = GetMaxOffset();
                _virtual += _velocity * dt;
                _velocity *= Math.Exp(-Friction * dt);

                if (_virtual < 0 || _virtual > max)
                    _velocity *= Math.Pow(EdgeAbsorb, dt * 60.0);

                if (Math.Abs(_velocity) < StopVelocity)
                {
                    _velocity = 0;
                    if (_virtual < 0)
                    {
                        _virtual *= Math.Pow(ReturnDamping, dt * 60.0);
                        if (_virtual > -0.4)
                            _virtual = 0;
                    }
                    else if (_virtual > max)
                    {
                        var overflow = _virtual - max;
                        overflow *= Math.Pow(ReturnDamping, dt * 60.0);
                        _virtual = overflow < 0.4 ? max : max + overflow;
                    }
                }

                ApplyVisual();

                if (IsSettled())
                {
                    _virtual = Clamp(_virtual, 0, max);
                    _velocity = 0;
                    ApplyVisual();
                    StopTicking();
                }
            }

            private void ApplyVisual()
            {
                var max = GetMaxOffset();
                double offset;
                double ty;
                if (_virtual < 0)
                {
                    offset = 0;
                    ty = Rubber(-_virtual);
                }
                else if (_virtual > max)
                {
                    offset = max;
                    ty = -Rubber(_virtual - max);
                }
                else
                {
                    offset = _virtual;
                    ty = 0;
                }

                if (Math.Abs(_viewer.VerticalOffset - offset) > 0.01)
                    _viewer.ScrollToVerticalOffset(offset);

                if (_translate is null)
                    BindTemplateParts();
                if (_translate is not null && _translate.Y != ty)
                    _translate.Y = ty;
            }

            private void SeedVirtual()
            {
                if (_virtualValid)
                    return;
                _virtual = _viewer.VerticalOffset;
                _virtualValid = true;
            }

            private bool IsSettled()
            {
                if (Math.Abs(_velocity) >= StopVelocity)
                    return false;
                var max = GetMaxOffset();
                return _virtual >= -0.4 && _virtual <= max + 0.4;
            }

            private double GetMaxOffset()
            {
                var max = _viewer.ExtentHeight - _viewer.ViewportHeight;
                return max < 0 ? 0 : max;
            }

            private void EnsureTicking()
            {
                if (_ticking)
                    return;
                _ticking = true;
                _lastTime = 0;
                CompositionTarget.Rendering += _onRendering;
            }

            private void StopTicking()
            {
                if (!_ticking)
                    return;
                _ticking = false;
                _lastTime = 0;
                CompositionTarget.Rendering -= _onRendering;
                if (IsSettled())
                    _virtualValid = false;
            }

            private static double Rubber(double overflow)
            {
                if (overflow <= 0)
                    return 0;
                var r = MaxOverscroll * (1.0 - Math.Exp(-overflow / MaxOverscroll));
                return r > MaxOverscroll ? MaxOverscroll : r;
            }

            private static double Clamp(double value, double min, double max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is T match)
                        return match;
                    var nested = FindChild<T>(child);
                    if (nested is not null)
                        return nested;
                }

                return null;
            }

            private static ScrollBar? FindNamedScrollBar(DependencyObject root)
            {
                var count = VisualTreeHelper.GetChildrenCount(root);
                for (var i = 0; i < count; i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is ScrollBar bar && bar.Name == "PART_VerticalScrollBar")
                        return bar;
                    var nested = FindNamedScrollBar(child);
                    if (nested is not null)
                        return nested;
                }

                return FindChild<ScrollBar>(root);
            }
        }
    }
}
