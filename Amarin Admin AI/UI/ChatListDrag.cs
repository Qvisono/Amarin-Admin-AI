using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Amarin.Core;
using Path = System.Windows.Shapes.Path;

namespace Amarin.UI;

/// <summary>
/// Перетаскивание чатов боковой панели в папки, в закреплённые, в общий список и в архив.
/// </summary>
/// <remarks>
/// <para>
/// Жест внутри окна, с захватом мыши, а не OLE <c>DoDragDrop</c>: системное перетаскивание
/// разбудило бы оверлей вложений и разворачивание компактного композера, которые ждут файлов
/// снаружи, а нарисовать под курсором карточку строки оно не умеет. Захват заодно отнимает мышь
/// у кнопки строки — после жеста чат не откроется.
/// </para>
/// <para>
/// Куда упадёт чат, решает присоединённое <see cref="ChatRowState.DropTargetProperty"/>, которое
/// раскладка ставит каждой строке и заголовку. Подсвечивается весь раздел — карточка папки или
/// группа целиком, — чтобы было видно, где чат окажется, а не только строку под курсором.
/// </para>
/// </remarks>
internal sealed class ChatListDrag
{
    /// <summary>Полоса у края колонки, в которой список листается сам.</summary>
    private const double EdgeZone = 28;

    /// <summary>Скорость автопрокрутки у самого края, точек в секунду.</summary>
    private const double EdgeSpeed = 720;

    private readonly Panel _list;
    private readonly ScrollViewer _scroller;
    private readonly Canvas _layer;
    private readonly Func<bool> _canDrag;
    private readonly Func<string, IReadOnlyList<string>> _payload;
    private readonly Action<IReadOnlyList<string>, ChatDropTarget> _drop;

    private Button? _pressRow;
    private Point _pressPoint;
    private bool _dragging;
    private bool _ownCaptureChange;
    private IReadOnlyList<string> _ids = [];
    private readonly List<(UIElement Element, double Opacity)> _dimmed = [];
    private readonly List<RowExtent> _rows = [];
    private readonly List<ChatDropTarget?> _sources = [];
    private ChatDropTarget? _target;
    private bool _pinHot;
    private TimeSpan _lastFrame;

    private readonly Border _ghost = new();
    private readonly TextBlock _ghostText = new();
    private readonly Border _indicator = new();
    private readonly Border _pinZone = new();

    public ChatListDrag(
        Panel list,
        ScrollViewer scroller,
        Canvas layer,
        Func<bool> canDrag,
        Func<string, IReadOnlyList<string>> payload,
        Action<IReadOnlyList<string>, ChatDropTarget> drop)
    {
        _list = list;
        _scroller = scroller;
        _layer = layer;
        _canDrag = canDrag;
        _payload = payload;
        _drop = drop;

        BuildVisuals();

        _list.PreviewMouseLeftButtonDown += (_, e) => OnPress(e);
        _list.PreviewMouseMove += (_, e) => OnMove(e);
        _list.PreviewMouseLeftButtonUp += (_, e) => OnRelease(e);
        _list.LostMouseCapture += (_, _) => OnLostCapture();

        // Нажатие в списке — не перетаскивание окна: корневой Grid ловит MouseDown и двигал бы
        // окно с пустого места между строками и с заголовков групп.
        _list.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                e.Handled = true;
            }
        };
    }

    public bool IsDragging => _dragging;

    /// <summary>Список просили пересобрать посреди жеста — это случится, когда жест кончится.</summary>
    public bool RefreshPending { get; set; }

    /// <summary>Жест кончился — броском или отменой.</summary>
    public event Action? Ended;

    /// <summary>Куда упадут чаты, если отпустить сейчас.</summary>
    internal ChatDropTarget? Target => _target;

    /// <summary>
    /// Жест ведёт настоящая мышь: захват и автопрокрутка у краёв. Тесты выключают и ведут жест
    /// точками — их окно стоит за краем экрана, неактивное окно Windows лишает захвата сразу, а
    /// настоящий курсор для него всегда «у нижнего края».
    /// </summary>
    internal bool RealMouse { get; set; } = true;

    /// <summary>Отменяет идущий жест (Esc). Возвращает, было ли что отменять.</summary>
    public bool Cancel()
    {
        if (!_dragging)
        {
            _pressRow = null;
            return false;
        }

        End();
        return true;
    }

    // ───────────────────────── жест (точки — в координатах списка) ─────────────────────────

    internal void Press(Button row, Point at)
    {
        _pressRow = row;
        _pressPoint = at;
    }

    internal void MoveTo(Point at)
    {
        if (_pressRow is null)
        {
            return;
        }

        if (!_dragging)
        {
            var dx = Math.Abs(at.X - _pressPoint.X);
            var dy = Math.Abs(at.Y - _pressPoint.Y);
            if (dx < SystemParameters.MinimumHorizontalDragDistance && dy < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            Begin();
        }

        Track(at);
    }

    /// <summary>Отпускание: бросок, если жест шёл. Возвращает, был ли жест.</summary>
    internal bool Release()
    {
        _pressRow = null;
        if (!_dragging)
        {
            return false;
        }

        var target = _target;
        var ids = _ids;
        End();
        if (target is { } where)
        {
            _drop(ids, where);
        }

        return true;
    }

    private void OnPress(MouseButtonEventArgs e)
    {
        _pressRow = null;
        if (!_canDrag() || RowUnder(e.OriginalSource as DependencyObject) is not { } row)
        {
            return;
        }

        Press(row, e.GetPosition(_list));
    }

    private void OnMove(MouseEventArgs e)
    {
        if (_pressRow is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Cancel();
            return;
        }

        MoveTo(e.GetPosition(_list));
        if (_dragging)
        {
            e.Handled = true;
        }
    }

    private void OnRelease(MouseButtonEventArgs e)
    {
        if (Release())
        {
            e.Handled = true;
        }
    }

    private void OnLostCapture()
    {
        // Захват отняли снаружи (другое окно, системное меню) — жест не доведён, бросать некуда.
        if (_dragging && !_ownCaptureChange)
        {
            End();
        }
    }

    /// <summary>
    /// Строка чата, внутри которой нажали, — прямой ребёнок списка с id в <c>Tag</c>. Нажатие на
    /// вложенную кнопку («⋯») жеста не начинает: у неё своё меню.
    /// </summary>
    private Button? RowUnder(DependencyObject? node)
    {
        Button? inner = null;
        while (node is not null && !ReferenceEquals(node, _list))
        {
            if (node is Button button)
            {
                if (ReferenceEquals(VisualTreeHelper.GetParent(button), _list) || ReferenceEquals(button.Parent, _list))
                {
                    return inner is null && button.Tag is string ? button : null;
                }

                inner ??= button;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    // ───────────────────────── начало и конец ─────────────────────────

    private void Begin()
    {
        var row = _pressRow!;
        _ids = _payload((string)row.Tag);
        _dragging = true;

        if (RealMouse)
        {
            _ownCaptureChange = true;
            Mouse.Capture(_list, CaptureMode.Element);
            _ownCaptureChange = false;
        }

        CacheRows();

        foreach (var child in _list.Children.OfType<Button>())
        {
            if (child.Tag is string id && _ids.Contains(id))
            {
                _dimmed.Add((child, child.Opacity));
                _sources.Add(ChatRowState.GetDropTarget(child));
                child.Opacity = 0.4;
            }
        }

        _ghostText.Text = _ids.Count > 1 ? Loc.Format("S.ChatList.DragMany", _ids.Count) : row.Content as string ?? "";
        _ghost.Width = Math.Max(80, row.ActualWidth - 8);
        _ghost.Height = Math.Max(24, row.ActualHeight);
        _indicator.Background = AccentWash();
        _pinZone.Visibility = _rows.Any(extent => extent.Target?.Kind == ChatDropKind.Pinned) ? Visibility.Collapsed : Visibility.Visible;
        SetPinHot(false);
        _layer.Visibility = Visibility.Visible;

        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void End()
    {
        CompositionTarget.Rendering -= OnFrame;
        _dragging = false;
        _pressRow = null;
        _target = null;
        _ids = [];
        _rows.Clear();
        _sources.Clear();

        foreach (var (element, opacity) in _dimmed)
        {
            element.Opacity = opacity;
        }

        _dimmed.Clear();
        _layer.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(Mouse.Captured, _list))
        {
            _ownCaptureChange = true;
            Mouse.Capture(null);
            _ownCaptureChange = false;
        }

        Ended?.Invoke();
    }

    /// <summary>
    /// Вертикальные полосы детей списка в его координатах. Полоса тянется до верха следующего
    /// ребёнка: курсор в зазоре между строками принадлежит строке над ним, а не «ничему».
    /// </summary>
    private void CacheRows()
    {
        _rows.Clear();
        var children = _list.Children.OfType<FrameworkElement>().Where(child => child.IsVisible).ToList();
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var top = child.TranslatePoint(default, _list).Y;
            var bottom = i + 1 < children.Count
                ? children[i + 1].TranslatePoint(default, _list).Y
                : top + child.ActualHeight + child.Margin.Bottom;
            _rows.Add(new RowExtent(top, bottom, child.ActualHeight, ChatRowState.GetDropTarget(child)));
        }
    }

    // ───────────────────────── слежение ─────────────────────────

    private void Track(Point listPoint)
    {
        var listTop = _list.TranslatePoint(default, _scroller).Y;
        var pointer = new Point(listPoint.X, listPoint.Y + listTop);

        Rect? region = null;
        _target = null;
        var overPinZone = _pinZone.Visibility == Visibility.Visible && pointer.Y >= 0 && pointer.Y <= PinZoneBottom;
        SetPinHot(overPinZone);
        if (overPinZone)
        {
            _target = ChatDropTarget.Pinned;
        }
        else
        {
            var index = _rows.FindIndex(extent => listPoint.Y >= extent.Top && listPoint.Y < extent.Bottom);
            if (index >= 0 && _rows[index].Target is { } target && !AlreadyThere(target))
            {
                _target = target;
                var first = index;
                var last = index;
                while (first > 0 && _rows[first - 1].Target == target)
                {
                    first--;
                }

                while (last + 1 < _rows.Count && _rows[last + 1].Target == target)
                {
                    last++;
                }

                var top = _rows[first].Top + listTop;
                var bottom = _rows[last].Top + _rows[last].Height + listTop;
                region = new Rect(4, top - 2, Math.Max(0, _scroller.ViewportWidth - 8), bottom - top + 4);
            }
        }

        if (region is { } rect)
        {
            _indicator.Visibility = Visibility.Visible;
            _indicator.Width = rect.Width;
            _indicator.Height = rect.Height;
            Canvas.SetLeft(_indicator, rect.X);
            Canvas.SetTop(_indicator, rect.Y);
        }
        else
        {
            _indicator.Visibility = Visibility.Collapsed;
        }

        Canvas.SetLeft(_ghost, 10);
        Canvas.SetTop(_ghost, pointer.Y - (_ghost.Height / 2));
    }

    /// <summary>
    /// Все перетаскиваемые чаты уже лежат в этом разделе — подсвечивать нечего: бросок ничего
    /// бы не изменил, а подсветка обещала бы обратное.
    /// </summary>
    private bool AlreadyThere(ChatDropTarget target) => _sources.Count > 0 && _sources.All(source => source == target);

    private void SetPinHot(bool hot)
    {
        if (hot == _pinHot && _pinZone.ReadLocalValue(Border.BorderBrushProperty) != DependencyProperty.UnsetValue)
        {
            return;
        }

        _pinHot = hot;
        _pinZone.SetResourceReference(Border.BorderBrushProperty, hot ? "Accent.Fill" : "Border.Strong");
    }

    /// <summary>Автопрокрутка у краёв колонки и пересчёт цели под неподвижным курсором.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_dragging || !RealMouse || e is not RenderingEventArgs frame)
        {
            return;
        }

        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((frame.RenderingTime - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = frame.RenderingTime;

        var y = Mouse.GetPosition(_scroller).Y;
        var height = _scroller.ViewportHeight;

        // Пока сверху висит зона «Закрепить», вверх листают, выведя курсор за кромку колонки.
        var topZone = _pinZone.Visibility == Visibility.Visible ? 0 : EdgeZone;
        var speed = 0.0;
        if (y < topZone)
        {
            speed = -EdgeSpeed * Math.Clamp((topZone - y) / EdgeZone, 0.15, 1);
        }
        else if (y > height - EdgeZone)
        {
            speed = EdgeSpeed * Math.Clamp((y - (height - EdgeZone)) / EdgeZone, 0.15, 1);
        }

        if (speed == 0)
        {
            return;
        }

        var offset = Math.Clamp(_scroller.VerticalOffset + (speed * dt), 0, _scroller.ScrollableHeight);
        if (Math.Abs(offset - _scroller.VerticalOffset) > 0.1)
        {
            _scroller.ScrollToVerticalOffset(offset);
            _scroller.UpdateLayout();

            // Курсор стоит, а список под ним уехал — цель под курсором уже другая.
            Track(Mouse.GetPosition(_list));
        }
    }

    // ───────────────────────── отрисовка ─────────────────────────

    private double PinZoneBottom => Canvas.GetTop(_pinZone) + _pinZone.Height;

    private void BuildVisuals()
    {
        _layer.IsHitTestVisible = false;
        _layer.ClipToBounds = true;
        _layer.Visibility = Visibility.Collapsed;

        _indicator.CornerRadius = new CornerRadius(10);
        _indicator.BorderThickness = new Thickness(1.5);
        _indicator.SetResourceReference(Border.BorderBrushProperty, "Accent.Fill");
        _indicator.Visibility = Visibility.Collapsed;

        _ghostText.FontSize = 12.5;
        _ghostText.TextTrimming = TextTrimming.CharacterEllipsis;
        _ghostText.VerticalAlignment = VerticalAlignment.Center;
        _ghostText.SetResourceReference(TextBlock.ForegroundProperty, "Text.Primary");
        _ghost.Child = _ghostText;
        _ghost.CornerRadius = new CornerRadius(6);
        _ghost.Padding = new Thickness(10, 0, 10, 0);
        _ghost.BorderThickness = new Thickness(1);
        _ghost.Opacity = 0.96;
        _ghost.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        _ghost.SetResourceReference(Border.BorderBrushProperty, "Border.Strong");
        _ghost.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Opacity = 0.4 };

        var pin = new Path
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        pin.SetResourceReference(Path.DataProperty, "Icon.Menu.Pin");
        pin.SetResourceReference(Shape.StrokeProperty, "Text.Secondary");
        var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.TextProperty, "S.ChatList.DropToPin");
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(pin);
        row.Children.Add(label);

        _pinZone.Child = row;
        _pinZone.Height = 36;
        _pinZone.CornerRadius = new CornerRadius(10);
        _pinZone.BorderThickness = new Thickness(1.5);
        _pinZone.SetResourceReference(Border.BackgroundProperty, "Bg.Panel");
        Canvas.SetTop(_pinZone, 6);
        Canvas.SetLeft(_pinZone, 6);

        _layer.Children.Add(_indicator);
        _layer.Children.Add(_pinZone);
        _layer.Children.Add(_ghost);
        _layer.SizeChanged += (_, _) => _pinZone.Width = Math.Max(0, _layer.ActualWidth - 12);
    }

    /// <summary>
    /// Лёгкая заливка цветом акцента: раздел подсвечен, а строки в нём читаются. Собирается на
    /// каждый жест — акцент меняют в настройках оформления.
    /// </summary>
    private Brush AccentWash()
    {
        var color = _layer.TryFindResource("Accent.Fill") is SolidColorBrush accent ? accent.Color : Colors.SteelBlue;
        var wash = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));
        wash.Freeze();
        return wash;
    }

    private readonly record struct RowExtent(double Top, double Bottom, double Height, ChatDropTarget? Target);
}
