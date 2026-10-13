using System.Windows;
using System.Windows.Input;

namespace Amarin.Fox;

/// <summary>Касания: тык в нос, уши, хвост, поглаживание, наведение, перенос и бросок, меню.</summary>
/// <remarks>
/// Нажатие на Розу помечается обработанным и берёт мышь: иначе оно ушло бы дальше — заголовок окна
/// начал бы его двигать, а рамка колонки чатов — выделять. Щелчок мимо рисунка до Розы не доходит
/// вовсе (<see cref="FoxSprite"/> ловит мышь только непрозрачными пикселями).
/// </remarks>
internal sealed partial class FoxStage
{
    /// <summary>За шкирку: эта клетка кадра «висит» под курсором.</summary>
    private const int ScruffX = 19;
    private const int ScruffY = 9;

    /// <summary>Сколько сдвинуть мышь с зажатой кнопкой, чтобы взять на руки, а не щёлкнуть.</summary>
    private const double DragThreshold = 4;

    /// <summary>Быстрее этого (точек в мс) отпущенный Роза летит брошенным, а не падает.</summary>
    private const double ThrowSpeed = 0.35;

    private readonly List<DateTime> _boops = [];
    private readonly List<DateTime> _clicks = [];
    private readonly List<(TimeSpan At, double X, double Y)> _trail = [];
    private Point _press;
    private bool _pressed;
    private bool _shaken;
    private int _reversals;
    private TimeSpan _firstReversal;
    private double _lastDx;

    private bool _hovering;
    private DateTime _hoverSince;
    private DateTime _lastHoverStart;
    private bool _hoverStartSaid;
    private bool _hoverLongSaid;
    private Point _strokeLast;
    private double _strokePath;
    private int _strokeTurns;
    private double _strokeDx;
    private DateTime _strokeSince;
    private DateTime _lastPet;

    private void HookInput()
    {
        _sprite.MouseLeftButtonDown += OnPress;
        _sprite.MouseMove += OnMove;
        _sprite.MouseLeftButtonUp += OnRelease;
        _sprite.LostMouseCapture += (_, _) =>
        {
            if (_mode == FoxMode.Drag)
            {
                LetGo();
            }

            _pressed = false;
        };
        _sprite.MouseEnter += (_, _) =>
        {
            _hovering = true;
            _hoverSince = _clock();
            _hoverStartSaid = false;
            _hoverLongSaid = false;
            ScheduleFrame();
        };
        _sprite.MouseLeave += (_, _) =>
        {
            _hovering = false;
            _strokePath = 0;
            _strokeTurns = 0;
        };
        _sprite.MouseRightButtonDown += (_, e) => e.Handled = true;
        _sprite.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_mode is not (FoxMode.Drag or FoxMode.Fly))
            {
                _host.ShowMenu(this);
            }
        };
    }

    /// <summary>Отпустить: Esc, потеря мыши, свёрнутое окно. Из рук — падает, из игры — игра кончается.</summary>
    public void LetGo()
    {
        StopLaser();
        CloseTalk();
        if (_mode != FoxMode.Drag)
        {
            return;
        }

        _pressed = false;
        if (_sprite.IsMouseCaptured)
        {
            _sprite.ReleaseMouseCapture();
        }

        Throw(0, 0);
    }

    private void OnPress(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_mode is FoxMode.Fly or FoxMode.Off)
        {
            return;
        }

        _pressed = true;
        _press = e.GetPosition(_host.Layer);
        _sprite.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(_host.Layer);
        if (_pressed && e.LeftButton == MouseButtonState.Pressed)
        {
            if (_mode != FoxMode.Drag && (point - _press).Length > DragThreshold && _mode is not (FoxMode.Off or FoxMode.Fly))
            {
                BeginDrag(point);
            }

            if (_mode == FoxMode.Drag)
            {
                Hold(point);
            }

            return;
        }

        if (!_pressed)
        {
            Stroke(e.GetPosition(_sprite), point);
        }
    }

    private void OnRelease(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var wasPressed = _pressed;
        _pressed = false;
        if (_sprite.IsMouseCaptured)
        {
            _sprite.ReleaseMouseCapture();
        }

        if (_mode == FoxMode.Drag)
        {
            Release();
            return;
        }

        if (wasPressed)
        {
            Click(e.GetPosition(_sprite));
        }
    }

    internal void BeginDrag(Point point)
    {
        Replan();
        if (_mode == FoxMode.Sleep)
        {
            Wake();
        }

        _lying = false;
        _walkTo = null;
        _mode = FoxMode.Drag;
        _shaken = false;
        _reversals = 0;
        _trail.Clear();
        _tilt.CenterX = Px(_flip ? FoxSprites.FrameSize - 1 - ScruffX : ScruffX);
        _tilt.CenterY = Px(ScruffY);
        Play("hang", "hang");
        Hold(point);
        StartRendering();
        Signal(FoxSignalKind.Grab);
    }

    /// <summary>Висит за шкирку под курсором; помнит путь — по нему считается бросок и тряска.</summary>
    internal void Hold(Point point)
    {
        var scruff = Px(_flip ? FoxSprites.FrameSize - 1 - ScruffX : ScruffX);
        _x = Math.Clamp(point.X - scruff, -scruff, _host.Width() - scruff);
        _y = Math.Clamp(point.Y - Px(ScruffY), -Px(ScruffY), _host.Floor() - Px(ScruffY));
        var now = _watch.Elapsed;
        _trail.Add((now, _x, _y));
        _trail.RemoveAll(entry => now - entry.At > TimeSpan.FromMilliseconds(120));

        // Тряска — четыре смены направления быстрым движением за секунду.
        if (_trail.Count >= 2)
        {
            var (at, x, _) = _trail[^2];
            var ms = Math.Max(1, (now - at).TotalMilliseconds);
            var dx = (_x - x) / ms;
            if (Math.Abs(dx) > 0.6)
            {
                if (_lastDx != 0 && Math.Sign(dx) != Math.Sign(_lastDx))
                {
                    if (_reversals == 0 || now - _firstReversal > TimeSpan.FromSeconds(1))
                    {
                        _reversals = 0;
                        _firstReversal = now;
                    }

                    _reversals++;
                    if (_reversals >= 4 && !_shaken)
                    {
                        _shaken = true;
                        Signal(FoxSignalKind.Shake);
                    }
                }

                _lastDx = dx;
            }
        }

        Draw();
    }

    /// <summary>Болтается в руках: наклон за движением, без рывков.</summary>
    private void StepDrag(double ms)
    {
        var vx = 0.0;
        if (_trail.Count >= 2)
        {
            var (at, x, _) = _trail[0];
            var span = Math.Max(1, (_watch.Elapsed - at).TotalMilliseconds);
            vx = (_x - x) / span;
        }

        var target = Math.Clamp(-vx * 30, -30, 30);
        _tilt.Angle += (target - _tilt.Angle) * Math.Min(1, ms / 90);
        _trail.RemoveAll(entry => _watch.Elapsed - entry.At > TimeSpan.FromMilliseconds(120));
    }

    internal void Release()
    {
        double vx = 0, vy = 0;
        if (_trail.Count >= 2)
        {
            var (at, x, y) = _trail[0];
            var span = Math.Max(1, (_watch.Elapsed - at).TotalMilliseconds);
            vx = Math.Clamp((_x - x) / span, -3, 3);
            vy = Math.Clamp((_y - y) / span, -3, 3);
        }

        var speed = Math.Sqrt((vx * vx) + (vy * vy));
        if (speed > ThrowSpeed)
        {
            Throw(vx, vy);
            Signal(new FoxSignal(FoxSignalKind.Thrown, Value: speed));
        }
        else
        {
            Throw(0, 0);
        }
    }

    /// <summary>Щелчок без переноса: по месту на рисунке, двойной и тройной — отдельно.</summary>
    private void Click(Point at)
    {
        var now = _clock();
        _clicks.RemoveAll(time => now - time > TimeSpan.FromMilliseconds(420));
        _clicks.Add(now);

        if (_mode == FoxMode.Peek)
        {
            EndPeek();
            return;
        }

        if (_spot is "send" or "chip")
        {
            // Прогнали с кнопки отправки или с плашки модели: уходит домой надутым.
            Replan();
            _face = "offended";
            _faceUntil = now.AddSeconds(2);
            Burst("puff", 1);
            JumpTo("home", HomeFoot(_host.Home()));
            return;
        }

        switch (_clicks.Count)
        {
            case 2:
                Signal(FoxSignalKind.DoubleClick);
                return;
            case >= 3:
                _clicks.Clear();
                Signal(FoxSignalKind.TripleClick);
                return;
        }

        switch (Region(at))
        {
            case FoxRegion.Nose:
                _boops.Add(now);
                Signal(FoxSignalKind.Boop);
                break;
            case FoxRegion.Ear:
                Signal(FoxSignalKind.EarTouch);
                break;
            case FoxRegion.Tail:
                Signal(FoxSignalKind.TailPull);
                break;
            case FoxRegion.Chin:
                Signal(FoxSignalKind.Scratch);
                break;
            default:
                Signal(FoxSignalKind.Pet);
                break;
        }
    }

    /// <summary>
    /// Поглаживание — водить курсором туда-сюда по голове или спине, без кнопки. Два разворота на
    /// сорока точках пути за полторы секунды — одно поглаживание; подбородок и ухо — почесать.
    /// </summary>
    private void Stroke(Point onSprite, Point onLayer)
    {
        if (_mode is not (FoxMode.Idle or FoxMode.Sleep or FoxMode.Peek))
        {
            return;
        }

        var now = _clock();
        if (now - _strokeSince > TimeSpan.FromSeconds(1.5))
        {
            _strokeSince = now;
            _strokePath = 0;
            _strokeTurns = 0;
        }

        var delta = onLayer - _strokeLast;
        _strokeLast = onLayer;
        if (delta.Length > 40)
        {
            // Курсор только что вошёл: прыжок с прошлого места — не движение по шерсти.
            return;
        }

        _strokePath += delta.Length;
        if (Math.Abs(delta.X) > 1.5)
        {
            if (_strokeDx != 0 && Math.Sign(delta.X) != Math.Sign(_strokeDx))
            {
                _strokeTurns++;
            }

            _strokeDx = delta.X;
        }

        var region = Region(onSprite);
        if (_strokeTurns >= 2 && _strokePath >= 40 && now - _lastPet > TimeSpan.FromSeconds(1.5) &&
            region is FoxRegion.Head or FoxRegion.Back or FoxRegion.Chin or FoxRegion.Ear)
        {
            _lastPet = now;
            _strokePath = 0;
            _strokeTurns = 0;
            Signal(region is FoxRegion.Chin or FoxRegion.Ear ? FoxSignalKind.Scratch : FoxSignalKind.Pet);
        }
    }

    /// <summary>Наведение: тёплый виляет хвостом, холодный рычит, а задержишь курсор — кусает.</summary>
    private void Hover(DateTime now)
    {
        if (!_hovering || _pressed || _mode is not (FoxMode.Idle or FoxMode.Peek))
        {
            return;
        }

        if (!_hoverStartSaid && now - _hoverSince >= TimeSpan.FromMilliseconds(300))
        {
            _hoverStartSaid = true;
            if (now - _lastHoverStart >= TimeSpan.FromSeconds(20))
            {
                _lastHoverStart = now;
                Signal(FoxSignalKind.HoverStart);
            }
        }

        if (!_hoverLongSaid && _cold && now - _hoverSince >= TimeSpan.FromSeconds(2.5))
        {
            _hoverLongSaid = true;
            Signal(FoxSignalKind.HoverLong);
        }
    }

    /// <summary>Часть тела под точкой элемента — по якорю головы нынешнего кадра.</summary>
    private FoxRegion Region(Point onSprite)
    {
        if (_sprite.CellAt(onSprite) is not { } cell)
        {
            return FoxRegion.None;
        }

        var animation = _sprites.Animations[_anim];
        var frame = animation.Frames[Math.Min(_frame, animation.Frames.Count - 1)];
        return RegionOf(cell, frame.Head);
    }

    /// <summary>
    /// Часть тела по клетке кадра: смещения от глаза (якоря мордочки) у всех поз одни, голова
    /// нарисована одинаково. Без якоря (клубок, стоячие позы с 7-й картинки) — просто спина.
    /// </summary>
    internal static FoxRegion RegionOf((int X, int Y) cell, (int X, int Y)? head)
    {
        if (head is not { } eye)
        {
            return FoxRegion.Back;
        }

        var dx = cell.X - eye.X;
        var dy = cell.Y - eye.Y;
        if (dx <= -3 && dy is >= 2 and <= 5)
        {
            return FoxRegion.Nose;
        }

        if (dy <= -6)
        {
            return FoxRegion.Ear;
        }

        if (dy is >= 4 and <= 7 && dx is >= -3 and <= 6)
        {
            return FoxRegion.Chin;
        }

        if (dy is > -6 and < 4)
        {
            return FoxRegion.Head;
        }

        // Хвост обвивает лапы спереди снизу — так нарисован сидящий Роза.
        return dy >= 23 && dx >= 7 ? FoxRegion.Tail : FoxRegion.Back;
    }

    /// <summary>Погладить из меню.</summary>
    public void Pet() => Signal(FoxSignalKind.Pet);

    /// <summary>Почесать за ухом из меню.</summary>
    public void Scratch() => Signal(FoxSignalKind.Scratch);

    /// <summary>Вкусняшка из меню.</summary>
    public void Treat() => Signal(FoxSignalKind.Treat);

    /// <summary>Спрятать на <paramref name="hours"/> часов; 0 — до завтрашнего утра.</summary>
    public void HideFor(double hours) => Signal(new FoxSignal(FoxSignalKind.Hide, Value: hours));
}

/// <summary>Часть тела Розы под курсором.</summary>
internal enum FoxRegion
{
    None,
    Nose,
    Ear,
    Chin,
    Head,
    Back,
    Tail
}
