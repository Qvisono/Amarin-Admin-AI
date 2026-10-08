using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Выделение рамкой в колонке чатов: зажали кнопку на пустом месте, подписи группы или
/// заголовке папки и потянули — прямоугольник цвета акцента выбирает всё, что накрыл.
/// </summary>
/// <remarks>
/// <para>
/// Нажатие на строке чата — не рамка, а перетаскивание (<see cref="ChatListDrag"/>): на одном
/// нажатии два жеста спорили бы. Нажатие на «⋯», полосе прокрутки и выше списка (поиск, кнопки)
/// — тоже не рамка. Заголовок папки рамку начинает: без сдвига он остаётся щелчком (свернуть,
/// раскрыть), со сдвигом рамка отбирает мышь, и щелчка на отпускании не будет.
/// </para>
/// <para>
/// Точка нажатия хранится в координатах списка, а не колонки: под автопрокруткой у края список
/// уезжает, и угол рамки едет вместе с той строкой, где её начали, как в Проводнике. Что значит
/// накрытая строка — чат, папка целиком, весь архив, — решает окно (<see cref="Swept"/>).
/// </para>
/// </remarks>
internal sealed class ChatListMarquee
{
    /// <summary>Полоса у края колонки, в которой список листается сам.</summary>
    private const double EdgeZone = 28;

    /// <summary>Скорость автопрокрутки у самого края, точек в секунду.</summary>
    private const double EdgeSpeed = 720;

    private readonly ScrollViewer _scroller;
    private readonly Panel _list;
    private readonly Canvas _layer;
    private readonly Func<bool> _canStart;
    private readonly Border _box = new();
    private readonly List<(FrameworkElement Row, double Top, double Bottom)> _rows = [];

    private Point? _press;
    private bool _pressOnHeader;
    private bool _additive;
    private bool _selecting;
    private bool _ownCaptureChange;
    private TimeSpan _lastFrame;

    public ChatListMarquee(ScrollViewer scroller, Panel list, Canvas layer, Func<bool> canStart)
    {
        _scroller = scroller;
        _list = list;
        _layer = layer;
        _canStart = canStart;

        _layer.IsHitTestVisible = false;
        _layer.ClipToBounds = true;
        _layer.Visibility = Visibility.Collapsed;
        _box.CornerRadius = new CornerRadius(4);
        _box.BorderThickness = new Thickness(1);
        _box.SetResourceReference(Border.BorderBrushProperty, "Accent.Fill");
        _layer.Children.Add(_box);

        _scroller.PreviewMouseLeftButtonDown += (_, e) => OnPress(e);
        _scroller.PreviewMouseMove += (_, e) => OnMove(e);
        _scroller.PreviewMouseLeftButtonUp += (_, e) => OnRelease(e);

        // Только свой захват: событие всплывает, и потерю захвата любой строкой или закрытым
        // меню рамка приняла бы за свою и оборвалась бы посреди жеста.
        _scroller.LostMouseCapture += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _scroller))
            {
                OnLostCapture();
            }
        };

        // Нажатие под списком — не перетаскивание окна: корневой Grid ловит MouseDown и двигал бы
        // окно с того самого места, откуда тянут рамку.
        _scroller.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left && _press is not null)
            {
                e.Handled = true;
            }
        };
    }

    /// <summary>Рамка пошла — с Ctrl она добавляет к прежнему выбору.</summary>
    public event Action<bool>? Began;

    /// <summary>Рамка накрыла эти строки — все прямые дети списка, кроме подписей групп.</summary>
    public event Action<IReadOnlyList<FrameworkElement>>? Swept;

    /// <summary>Рамку отпустили — выбор остаётся тем, что она накрыла.</summary>
    public event Action? Ended;

    /// <summary>Рамку отменили (Esc) — выбор возвращается к прежнему.</summary>
    public event Action? Cancelled;

    /// <summary>Щелчок по пустому месту без сдвига — снять выбор, как в Проводнике.</summary>
    public event Action? ClickedEmpty;

    public bool IsSelecting => _selecting;

    /// <summary>
    /// Жест ведёт настоящая мышь: захват и автопрокрутка у краёв. Тесты выключают и ведут жест
    /// точками — их окно стоит за краем экрана, и настоящий курсор для него всегда «у края».
    /// </summary>
    internal bool RealMouse { get; set; } = true;

    /// <summary>Esc: рамка отменяется. Возвращает, было ли что отменять.</summary>
    public bool Cancel()
    {
        _press = null;
        if (!_selecting)
        {
            return false;
        }

        End();
        Cancelled?.Invoke();
        return true;
    }

    // ───────────────────────── жест (точки — в координатах списка) ─────────────────────────

    internal void Press(Point at, bool additive, bool onHeader)
    {
        _press = at;
        _additive = additive;
        _pressOnHeader = onHeader;
    }

    internal void MoveTo(Point at)
    {
        if (_press is not { } press)
        {
            return;
        }

        if (!_selecting)
        {
            if (Math.Abs(at.X - press.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(at.Y - press.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }

            Begin();
        }

        Track(at);
    }

    /// <summary>Отпускание. Возвращает, была ли рамка (или щелчок по пустому месту).</summary>
    internal bool Release()
    {
        var press = _press;
        _press = null;
        if (_selecting)
        {
            End();
            Ended?.Invoke();
            return true;
        }

        if (press is not null && !_pressOnHeader)
        {
            ClickedEmpty?.Invoke();
            return true;
        }

        return false;
    }

    /// <summary>Нажатие в колонке. Окно зовёт его и само — для нажатия, которое рамку создало.</summary>
    internal void OnPress(MouseButtonEventArgs e)
    {
        _press = null;
        if (!_canStart() || e.OriginalSource is not DependencyObject origin || !StartsHere(origin, out var onHeader))
        {
            return;
        }

        var at = e.GetPosition(_list);
        if (at.Y < 0)
        {
            return;
        }

        Press(at, Keyboard.Modifiers.HasFlag(ModifierKeys.Control), onHeader);
    }

    private void OnMove(MouseEventArgs e)
    {
        if (_press is null)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            Cancel();
            return;
        }

        MoveTo(e.GetPosition(_list));
        if (_selecting)
        {
            e.Handled = true;
        }
    }

    private void OnRelease(MouseButtonEventArgs e)
    {
        var selecting = _selecting;
        if (Release() && selecting)
        {
            e.Handled = true;
        }
    }

    private void OnLostCapture()
    {
        // Захват отняли снаружи (другое окно, системное меню) — рамка кончается на том, что успела.
        if (_selecting && !_ownCaptureChange)
        {
            _press = null;
            End();
            Ended?.Invoke();
        }
    }

    /// <summary>
    /// С этого места рамку начинают: пустое место колонки или списка, подпись группы, заголовок
    /// папки или архива. Строка чата, вложенная кнопка («⋯») и полоса прокрутки — нет.
    /// </summary>
    private bool StartsHere(DependencyObject origin, out bool onHeader)
    {
        onHeader = false;
        for (var node = origin; node is not null && !ReferenceEquals(node, _scroller); node = Parent(node))
        {
            if (node is ScrollBar)
            {
                return false;
            }

            if (node is ButtonBase button)
            {
                if (!IsListChild(button))
                {
                    // Кнопка внутри строки или вне списка (поиск, «Новый чат», полоса действий).
                    return false;
                }

                if (button.Tag is string)
                {
                    return false;
                }

                onHeader = true;
                return true;
            }

            if (node is TextBoxBase)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsListChild(DependencyObject element) =>
        ReferenceEquals(VisualTreeHelper.GetParent(element), _list) || ReferenceEquals(LogicalTreeHelper.GetParent(element), _list);

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);

    // ───────────────────────── начало и конец ─────────────────────────

    private void Begin()
    {
        _selecting = true;
        Began?.Invoke(_additive);

        if (RealMouse)
        {
            _ownCaptureChange = true;
            Mouse.Capture(_scroller, CaptureMode.Element);
            _ownCaptureChange = false;
        }

        CacheRows();
        _box.Background = AccentWash();
        _layer.Visibility = Visibility.Visible;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void End()
    {
        CompositionTarget.Rendering -= OnFrame;
        _selecting = false;
        _rows.Clear();
        _layer.Visibility = Visibility.Collapsed;

        if (ReferenceEquals(Mouse.Captured, _scroller))
        {
            _ownCaptureChange = true;
            Mouse.Capture(null);
            _ownCaptureChange = false;
        }
    }

    /// <summary>
    /// Строки списка в его координатах — один раз на жест: под рамкой список не перестраивается
    /// (окно откладывает обновления до её конца), а автопрокрутка двигает его целиком.
    /// </summary>
    private void CacheRows()
    {
        _rows.Clear();
        foreach (var child in _list.Children.OfType<FrameworkElement>())
        {
            // Подписи групп («Папки», «Сегодня») — не строки: выбирать в них нечего.
            if (!child.IsVisible || child is not ButtonBase || !child.IsHitTestVisible)
            {
                continue;
            }

            var top = child.TranslatePoint(default, _list).Y;
            _rows.Add((child, top, top + child.ActualHeight));
        }
    }

    // ───────────────────────── слежение ─────────────────────────

    private void Track(Point at)
    {
        if (_press is not { } press)
        {
            return;
        }

        var width = _list.ActualWidth;
        var left = Math.Clamp(Math.Min(press.X, at.X), 0, width);
        var right = Math.Clamp(Math.Max(press.X, at.X), 0, width);
        var top = Math.Min(press.Y, at.Y);
        var bottom = Math.Max(press.Y, at.Y);

        var hits = new List<FrameworkElement>();
        foreach (var (row, rowTop, rowBottom) in _rows)
        {
            if (rowBottom > top && rowTop < bottom)
            {
                hits.Add(row);
            }
        }

        Swept?.Invoke(hits);

        var origin = _list.TranslatePoint(default, _layer);
        Canvas.SetLeft(_box, origin.X + left);
        Canvas.SetTop(_box, origin.Y + top);
        _box.Width = Math.Max(1, right - left);
        _box.Height = Math.Max(1, bottom - top);
    }

    /// <summary>Автопрокрутка у краёв колонки: рамку тянут за пределы видимого.</summary>
    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_selecting || !RealMouse || e is not RenderingEventArgs frame)
        {
            return;
        }

        var dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((frame.RenderingTime - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = frame.RenderingTime;

        var y = Mouse.GetPosition(_scroller).Y;
        var height = _scroller.ViewportHeight;
        var speed = 0.0;
        if (y < EdgeZone)
        {
            speed = -EdgeSpeed * Math.Clamp((EdgeZone - y) / EdgeZone, 0.15, 1);
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

            // Курсор стоит, а список под ним уехал — рамка накрывает уже другие строки.
            Track(Mouse.GetPosition(_list));
        }
    }

    /// <summary>
    /// Заливка акцентом на просвет — та же, что у подсветки раздела при перетаскивании: строки
    /// под рамкой читаются. Собирается на каждый жест — акцент меняют в настройках оформления.
    /// </summary>
    private SolidColorBrush AccentWash()
    {
        var color = _layer.TryFindResource("Accent.Fill") is SolidColorBrush accent ? accent.Color : Colors.SteelBlue;
        var wash = new SolidColorBrush(Color.FromArgb(0x1F, color.R, color.G, color.B));
        wash.Freeze();
        return wash;
    }
}
