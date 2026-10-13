using System.Windows.Media;

namespace Amarin.Fox;

/// <summary>Движение Розы: ходьба по кромке, прыжок, полёт после броска, посадка, привычки в покое.</summary>
internal sealed partial class FoxStage
{
    /// <summary>Шаг и бег — точек слоя в миллисекунду при размере 48; крупный Роза ходит пропорционально быстрее.</summary>
    private const double WalkSpeed = 0.06;
    private const double RunSpeed = 0.2;

    /// <summary>Ускорение падения, точек в мс². С трёхсот точек Роза падает со скоростью 1,2 — порог головокружения.</summary>
    private const double Gravity = 0.0024;

    private double? _walkTo;
    private bool _running;
    private bool _lying;
    private Action? _arrived;
    private DateTime _settledAt;
    private DateTime _backAt;
    private DateTime _nextHabit;
    private DateTime _reactUntil = DateTime.MaxValue;
    private DateTime _peekUntil;
    private DateTime _nextZ;
    private bool _rendering;
    private TimeSpan _lastFrame;
    private double _vx;
    private double _vy;
    private double _squashLeft;

    /// <summary>Самый сильный удар этого полёта и сколько раз уже отскочил.</summary>
    private double _impact;
    private int _bounces;

    /// <summary>Быстрее этого (вниз, точек в мс) коснувшийся кромки отскакивает, а не садится.</summary>
    private const double BounceSpeed = 0.45;
    private const int MaxBounces = 3;

    private (double X, double Y) _jumpFrom;
    private (double X, double Y) _jumpTo;
    private double _jumpHeight;
    private double _jumpElapsed;
    private double _jumpLength;
    private string _jumpSpot = "home";
    private double _jumpFoot;

    /// <summary>
    /// Дела «потом»: конец шалости, возврат домой. Каждое помнит поколение плана — взяли Розу на
    /// руки или позвали в другое место, и прежний план молча отменяется.
    /// </summary>
    private readonly List<(DateTime Due, int Plan, Action Act)> _later = [];
    private int _plan;

    private void After(TimeSpan delay, Action act) => _later.Add((_clock() + delay, _plan, act));

    /// <summary>Новый план: всё, что прежний отложил на потом, не случится.</summary>
    private void Replan()
    {
        _plan++;
        _later.Clear();
        _arrived = null;
    }

    private void RunLater(DateTime now)
    {
        for (var i = 0; i < _later.Count; i++)
        {
            var (due, plan, act) = _later[i];
            if (due > now)
            {
                continue;
            }

            _later.RemoveAt(i--);
            if (plan == _plan)
            {
                act();
            }
        }
    }

    /// <summary>Приходит к полю ввода: выбегает из-за правого края или сразу сидит, если анимации выключены.</summary>
    private void Arrive(bool greet)
    {
        Replan();
        HideCallMark();
        _spot = "home";
        _lying = false;
        var edge = _host.Home();
        _edgeY = edge.Y;
        _edgeRight = edge.Right;
        var seat = HomeFoot(edge);
        void Greet()
        {
            if (greet)
            {
                Signal(FoxSignalKind.Appeared);
            }
            else
            {
                _face = "offended";
                _faceUntil = _clock().AddSeconds(2.5);
                Draw();
            }
        }

        if (!_host.Motion())
        {
            _mode = FoxMode.Idle;
            _foot = seat;
            _flip = false;
            Play("sit", "sit");
            _settledAt = _clock();
            Draw();
            Greet();
            ScheduleFrame();
            return;
        }

        _mode = FoxMode.Idle;
        _foot = _host.Width() + Px(30);
        WalkTo(seat, run: true, then: Greet, clamp: false);
    }

    /// <summary>Идёт по своей кромке до <paramref name="foot"/>; на месте садится и делает <paramref name="then"/>.</summary>
    private void WalkTo(double foot, bool run, Action? then = null, bool clamp = true)
    {
        if (clamp)
        {
            foot = ClampFoot(EdgeOf(_spot), foot);
        }

        _arrived = then;
        _lying = false;
        if (!_host.Motion() || Math.Abs(foot - _foot) < Px(2))
        {
            _foot = foot;
            Sit();
            return;
        }

        _mode = FoxMode.Walk;
        _walkTo = foot;
        _running = run;
        _flip = foot > _foot;
        Play(run ? "run" : "walk", run ? "run" : "walk");
        StartRendering();
    }

    private void StepWalk(double ms)
    {
        if (_walkTo is not { } target)
        {
            Sit();
            return;
        }

        var speed = (_running ? RunSpeed : WalkSpeed) * _sprite.Side / FoxSprites.FrameSize;
        var left = target - _foot;
        _flip = left > 0;
        _foot += Math.Sign(left) * Math.Min(Math.Abs(left), speed * ms);
        if (Math.Abs(target - _foot) < 0.5)
        {
            _foot = target;
            Sit();
        }
    }

    /// <summary>Пришёл: садится и делает то, ради чего шёл.</summary>
    private void Sit()
    {
        var wasWalking = _mode == FoxMode.Walk;
        _walkTo = null;
        _mode = FoxMode.Idle;
        _settledAt = _clock();
        if (wasWalking)
        {
            Play("sitdown", "sit");
        }
        else if (_anim is "walk" or "run")
        {
            Play("sit", "sit");
        }

        var then = _arrived;
        _arrived = null;
        then?.Invoke();
    }

    /// <summary>Перепрыгивает на место <paramref name="spot"/> по дуге; без анимаций — просто оказывается там.</summary>
    private void JumpTo(string spot, double foot, Action? then = null)
    {
        var edge = EdgeOf(spot);
        foot = ClampFoot(edge, foot);
        if (!_host.Motion() || _mode is FoxMode.Away or FoxMode.Hidden)
        {
            Seat(spot, foot);
            then?.Invoke();
            return;
        }

        _jumpFrom = Airborne ? (_x, _y) : CornerAt(_foot, _edgeY, _flip);
        _flip = foot > _foot;
        _lying = false;
        _mode = FoxMode.Jump;
        _anim = "jump";
        _frame = 0;
        _jumpTo = (foot - Px(_flip ? FoxSprites.FrameSize - 1 - FootColumn : FootColumn), edge.Y - Px(Ground("jump")));
        var distance = Math.Abs(_jumpTo.X - _jumpFrom.X) + Math.Abs(_jumpTo.Y - _jumpFrom.Y);
        _jumpHeight = Px(18) + (Math.Abs(_jumpTo.Y - _jumpFrom.Y) * 0.5);
        _jumpLength = Math.Clamp(300 + (distance * 0.9), 420, 900);
        _jumpElapsed = 0;
        _jumpSpot = spot;
        _jumpFoot = foot;
        _arrived = then;
        (_x, _y) = _jumpFrom;
        StartRendering();
    }

    /// <summary>Прыжок идёт по прожитым кадрам, а не по часам: 120 мс присед, потом дуга.</summary>
    private void StepJump(double ms)
    {
        _jumpElapsed += ms;
        var t = (_jumpElapsed - 120) / _jumpLength;
        if (t < 0)
        {
            // Присел перед прыжком.
            _frame = 0;
            return;
        }

        if (t >= 1)
        {
            var then = _arrived;
            Seat(_jumpSpot, _jumpFoot);
            Play("sitdown", "sit");
            Squash();
            then?.Invoke();
            return;
        }

        // Ось Y вниз: прямая от места к месту минус парабола высоты.
        _x = _jumpFrom.X + ((_jumpTo.X - _jumpFrom.X) * t);
        _y = _jumpFrom.Y + ((_jumpTo.Y - _jumpFrom.Y) * t) - (_jumpHeight * 4 * t * (1 - t));
        _frame = t < 0.45 ? 1 : 2;
    }

    /// <summary>Сажает на место без перехода: прилетел, позвали без анимаций, кромку убрали из-под лап.</summary>
    private void Seat(string spot, double foot)
    {
        var edge = EdgeOf(spot);
        _mode = FoxMode.Idle;
        _spot = spot;
        _edgeY = edge.Y;
        _edgeRight = edge.Right;
        _foot = ClampFoot(edge, foot);
        _tilt.Angle = 0;
        _lying = false;
        _arrived = null;
        _settledAt = _clock();
        Play("sit", "sit");
    }

    /// <summary>Отпустили или бросили: летит с этой скоростью (точек в мс).</summary>
    internal void Throw(double vx, double vy)
    {
        _mode = FoxMode.Fly;
        _vx = vx;
        _vy = vy;
        _impact = 0;
        _bounces = 0;

        // В полёте кренится вокруг середины, а не вокруг шкирки, за которую висел.
        _tilt.CenterX = _sprite.Side / 2;
        _tilt.CenterY = _sprite.Side / 2;
        Play("fly", "fly");
        if (!_host.Motion())
        {
            var (spot, foot) = NearestSpot();
            Settle(spot, foot, Math.Sqrt((vx * vx) + (vy * vy)));
            return;
        }

        StartRendering();
    }

    private void StepFly(double ms)
    {
        var feetBefore = _y + Px(Ground(_anim));
        _vy += Gravity * ms;

        // Воздух чуть гасит бег по горизонтали: без этого брошенный вбок скользил бы, как шайба.
        _vx *= Math.Pow(0.9993, ms);
        _x += _vx * ms;
        _y += _vy * ms;
        _tilt.Angle += (Math.Clamp(_vx * 22, -35, 35) - _tilt.Angle) * Math.Min(1, ms / 120);
        var right = _host.Width() - _sprite.Side;
        if (_x < 0 || _x > right)
        {
            _x = Math.Clamp(_x, 0, Math.Max(0, right));
            _vx = -_vx * 0.55;
        }

        if (_y < 0)
        {
            _y = 0;
            _vy = Math.Abs(_vy) * 0.4;
        }

        if (Math.Abs(_vx) > 0.02)
        {
            _flip = _vx > 0;
        }

        var feet = _y + Px(Ground(_anim));
        var foot = _x + Px(_flip ? FoxSprites.FrameSize - 1 - FootColumn : FootColumn);
        var speed = Math.Sqrt((_vx * _vx) + (_vy * _vy));
        if (_vy > 0)
        {
            foreach (var spot in (string[])["home", "settings"])
            {
                if (spot == "settings" && _host.Card() is null)
                {
                    continue;
                }

                // Запас вниз — на того, кого держали за шкирку прямо над его местом: лапы висят
                // чуть ниже кромки, и без запаса отпущенный провалился бы сквозь поле ввода.
                var edge = EdgeOf(spot);
                if (edge.Contains(foot) && feetBefore <= edge.Y + Px(14) && feet >= edge.Y)
                {
                    Touch(spot, edge.Y, foot, speed);
                    return;
                }
            }
        }

        var floor = _host.Floor();
        if (feet >= floor)
        {
            Touch("floor", floor, foot, speed);
        }
    }

    /// <summary>
    /// Коснулся кромки: с размаху — отскакивает (каждый раз ниже), тихо — садится. Мозгу уходит
    /// самый сильный удар полёта, а не последний мягкий: кружится голова от первого.
    /// </summary>
    private void Touch(string spot, double edgeY, double foot, double speed)
    {
        _impact = Math.Max(_impact, speed);
        if (_vy > BounceSpeed && _bounces < MaxBounces && _host.Motion())
        {
            _bounces++;
            _y = edgeY - Px(Ground(_anim));
            _vy = -_vy * 0.32;
            _vx *= 0.7;
            Squash();
            return;
        }

        Settle(spot, foot, _impact);
    }

    /// <summary>Сел после полёта: сказать мозгу, как сильно приложился.</summary>
    private void Settle(string spot, double foot, double speed)
    {
        Seat(spot, foot);
        if (spot == "home")
        {
            // Посадил человек — тут и будет его место, пока программа открыта.
            _homeFromRight = _edgeRight - _foot;
        }

        Squash();
        Signal(new FoxSignal(FoxSignalKind.Landed, Value: speed));
    }

    /// <summary>Под какой кромкой Роза сейчас: туда он сядет, если окно свернут посреди полёта.</summary>
    private (string Spot, double Foot) NearestSpot()
    {
        var foot = (Airborne ? _x : CornerAt(_foot, _edgeY, _flip).X) + Px(_flip ? FoxSprites.FrameSize - 1 - FootColumn : FootColumn);
        var feet = (Airborne ? _y : _edgeY - Px(Ground(_anim))) + Px(Ground(_anim));
        var best = "floor";
        var bestY = _host.Floor();
        foreach (var spot in (string[])["home", "settings"])
        {
            if (spot == "settings" && _host.Card() is null)
            {
                continue;
            }

            var edge = EdgeOf(spot);
            if (edge.Contains(foot) && edge.Y >= feet - Px(6) && edge.Y < bestY)
            {
                best = spot;
                bestY = edge.Y;
            }
        }

        return (best, ClampFoot(EdgeOf(best), foot));
    }

    /// <summary>Сжатие при приземлении — лапы на месте, тело пружинит.</summary>
    private void Squash()
    {
        if (_host.Motion())
        {
            _squashLeft = 220;
            StartRendering();
        }
    }

    private void StepSquash(double ms)
    {
        if (_squashLeft <= 0)
        {
            _squash.ScaleX = 1;
            _squash.ScaleY = 1;
            return;
        }

        _squashLeft = Math.Max(0, _squashLeft - ms);
        var k = _squashLeft / 220;
        _squash.ScaleY = 1 - (0.16 * k * k);
        _squash.ScaleX = 1 + (0.12 * k * k);
    }

    /// <summary>Перейти по слову мозга: домой, на карточку настроек или в середину поля ввода (требует внимания).</summary>
    private void GoTo(string spot)
    {
        if (_mode is FoxMode.Drag or FoxMode.Fly)
        {
            return;
        }

        if (_mode is FoxMode.Away or FoxMode.Hidden)
        {
            if (spot == "home")
            {
                Arrive(greet: false);
            }

            return;
        }

        if (_mode == FoxMode.Sleep)
        {
            Wake();
        }

        if (_mode == FoxMode.Peek)
        {
            EndPeek();
        }

        Replan();
        switch (spot)
        {
            case "settings" when _host.Card() is { } card:
                JumpTo("settings", card.Right - 120);
                break;
            case "center":
                var middle = _host.Home();
                GoHomeEdge((middle.Left + middle.Right) / 2, run: true);
                After(TimeSpan.FromSeconds(30), () => GoHomeEdge(HomeFoot(_host.Home()), run: false));
                break;
            case "home":
                GoHomeEdge(HomeFoot(_host.Home()), run: false);
                break;
        }
    }

    /// <summary>На кромку поля ввода в точку <paramref name="foot"/>: по ней пешком, с другого места — прыжком.</summary>
    private void GoHomeEdge(double foot, bool run, Action? then = null)
    {
        if (_mode == FoxMode.Peek)
        {
            EndPeek();
        }

        if (_spot == "home" && !Airborne)
        {
            WalkTo(foot, run || Math.Abs(foot - _foot) > Px(120), then);
        }
        else
        {
            JumpTo("home", foot, then);
        }
    }

    /// <summary>Прячется за поле ввода и выглядывает <paramref name="duration"/>.</summary>
    private void Peek(TimeSpan duration)
    {
        if (_mode != FoxMode.Idle || _spot != "home")
        {
            return;
        }

        _mode = FoxMode.Peek;
        _lying = false;
        _peekUntil = _clock() + duration;
        Play("peekup", "peek");
    }

    /// <summary>Вылезает из-за поля ввода обратно на кромку.</summary>
    private void EndPeek()
    {
        _mode = FoxMode.Idle;
        _settledAt = _clock();
        Play("sitdown", "sit");
        Squash();
    }

    private void Sleep()
    {
        Replan();
        _mode = FoxMode.Sleep;
        _lying = false;
        _nextZ = _clock().AddSeconds(3);
        Play("liedown", "sleep");
    }

    private void Wake()
    {
        _mode = FoxMode.Idle;
        _settledAt = _clock();
        Play("stretch", "sit");
        _face = "sleepy";
        _faceUntil = _clock().AddMilliseconds(1400);
    }

    /// <summary>Убегает за правый край: побег (<see cref="FoxMode.Away"/>) или «спрячься» (<see cref="FoxMode.Hidden"/>).</summary>
    private void RunOff(DateTime until, FoxMode then)
    {
        Replan();
        _backAt = until;
        void Gone()
        {
            _mode = then;
            _walkTo = null;
            if (then == FoxMode.Away)
            {
                ShowCallMark();
            }

            Draw();
        }

        if (Airborne)
        {
            var (spot, foot) = NearestSpot();
            Seat(spot, foot);
        }

        if (!_host.Motion() || _spot != "home")
        {
            Burst("puff", 2);
            Gone();
            return;
        }

        if (_mode == FoxMode.Peek || _mode == FoxMode.Sleep)
        {
            _mode = FoxMode.Idle;
        }

        // Сперва договаривает: реплика и взмах лапой — и только потом бежит.
        After(TimeSpan.FromMilliseconds(900), () => WalkTo(_host.Width() + Px(40), run: true, then: Gone, clamp: false));
    }

    /// <summary>Где лапы сидят по умолчанию: справа на кромке поля ввода, или куда его посадили.</summary>
    private double HomeFoot(FoxEdge edge) => ClampFoot(edge, edge.Right - (_homeFromRight ?? Px(12) + 18));

    /// <summary>Лапы на кромке так, чтобы тело не свисало за её концы.</summary>
    private double ClampFoot(FoxEdge edge, double foot)
    {
        var low = edge.Left + Px(17);
        var high = edge.Right - Px(12);
        return high < low ? (edge.Left + edge.Right) / 2 : Math.Clamp(foot, low, high);
    }

    private FoxEdge EdgeOf(string spot)
    {
        switch (spot)
        {
            case "settings" when _host.Card() is { } card:
                return card;
            case "floor":
                return new FoxEdge(0, _host.Width(), _host.Floor());
            case "send":
            case "chip":
                var rect = spot == "send" ? _host.SendButton() : _host.ModelChip();
                return rect.IsEmpty || rect.Width < 1
                    ? _host.Home()
                    : new FoxEdge(rect.Left, rect.Right, rect.Top + (rect.Height * 0.7));
            default:
                return _host.Home();
        }
    }

    /// <summary>Жизнь в покое: дела «потом», взгляд, привычки, сон, выглядывание. Зовётся таймером кадров и отрисовкой.</summary>
    private void Live()
    {
        var now = _clock();
        RunLater(now);
        ExpireBubble(now);
        Hover(now);

        switch (_mode)
        {
            case FoxMode.Peek:
                if (_anim == "peek" && now >= _peekUntil)
                {
                    Play("peekdown", "hide");
                }
                else if (_anim == "hide")
                {
                    EndPeek();
                }

                return;

            case FoxMode.Sleep:
                if (_anim == "sleep" && now >= _nextZ)
                {
                    _nextZ = now.AddSeconds(3.5);
                    Burst("z", 1);
                }

                return;

            case FoxMode.Idle:
                break;

            default:
                return;
        }

        if (_anim != BasePose() && _sprites.Animations[_anim].Loop && now >= _reactUntil)
        {
            Play(BasePose(), BasePose());
        }

        Gaze(now);
        Habit(now);
    }

    /// <summary>Смотрит на курсор, на поле ввода, пока человек печатает, или от человека, если дуется.</summary>
    private void Gaze(DateTime now)
    {
        if (_anim is not ("sit" or "lie" or "tail" or "earflick" or "think" or "nod"))
        {
            return;
        }

        var situation = _host.Situation();
        if (situation.SinceTyping < TimeSpan.FromSeconds(2) && _spot == "home")
        {
            // Поле ввода — слева от его места: смотрит, как человек пишет.
            _flip = false;
            return;
        }

        if (!situation.WindowActive || _host.Pointer() is not { } pointer)
        {
            return;
        }

        var center = CornerAt(_foot, _edgeY, _flip).X + (_sprite.Side / 2);
        var dx = pointer.X - center;
        var away = _turnAwayUntil > now;
        var wantRight = away ? dx < 0 : dx > 0;
        if (wantRight != _flip && Math.Abs(dx) > 40)
        {
            _flip = wantRight;
        }
    }

    /// <summary>Привычки: моргнуть, вильнуть хвостом, умыться, почесаться, пройтись, прилечь — раз в несколько секунд.</summary>
    private void Habit(DateTime now)
    {
        if (now < _nextHabit || _anim != BasePose() || (_face is not null && _faceUntil > now) ||
            _talk is { Visibility: System.Windows.Visibility.Visible })
        {
            return;
        }

        _nextHabit = now.AddMilliseconds(3000 + _random.Next(4000));

        if (_spot is "floor" && now - _settledAt > TimeSpan.FromSeconds(8))
        {
            JumpTo("home", HomeFoot(_host.Home()));
            return;
        }

        if (_spot == "home" && Math.Abs(_foot - HomeFoot(_host.Home())) > Px(6) && now - _settledAt > TimeSpan.FromSeconds(25))
        {
            WalkTo(HomeFoot(_host.Home()), run: false);
            return;
        }

        if (_lying)
        {
            if (_random.Next(3) == 0)
            {
                _lying = false;
                Play("stretch", "sit");
            }
            else
            {
                Blink(now);
            }

            return;
        }

        var roll = _random.Next(100);
        switch (roll)
        {
            case < 30:
                Blink(now);
                break;
            case < 42:
                Play("tail", "sit");
                break;
            case < 52:
                Play("earflick", "sit");
                break;
            case < 58:
                Play("wash", "sit");
                _face = "lick";
                _faceUntil = now.AddMilliseconds(1400);
                break;
            case < 63:
                Play("scratch", "sit");
                break;
            case < 69:
                Play("sniff", "sit");
                break;
            case < 77:
                Play("lookback", "sit");
                break;
            case < 80:
                Play("knead", "sit");
                break;
            case < 82:
                Play("stretch", "sit");
                break;
            case < 85 when _spot is "home" or "settings":
                Play("liedown", "lie");
                _lying = true;
                break;
            case < 92 when _spot is "home" or "settings" && _host.Motion():
                var step = Px(16 + _random.Next(32)) * (_random.Next(2) == 0 ? -1 : 1);
                WalkTo(_foot + step, run: false);
                _settledAt = now;
                break;
            default:
                Blink(now);
                break;
        }
    }

    private void Blink(DateTime now)
    {
        _face = "blink";
        _faceUntil = now.AddMilliseconds(150);
    }

    private void StartRendering()
    {
        if (_rendering || _paused || _mode == FoxMode.Off)
        {
            return;
        }

        _rendering = true;
        _lastFrame = _watch.Elapsed;
        _frames.Stop();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering)
        {
            return;
        }

        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    /// <summary>Есть ли что двигать каждый кадр; иначе хватает таймера кадров.</summary>
    private bool Moving =>
        _mode is FoxMode.Walk or FoxMode.Jump or FoxMode.Fly or FoxMode.Drag ||
        _squashLeft > 0 || _motes.Count > 0 || _shakeUntil > _clock() || _ball is not null || _laser is not null || _bubbleFade != 0;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_paused || _mode == FoxMode.Off)
        {
            StopRendering();
            return;
        }

        var now = _watch.Elapsed;
        var ms = Math.Min(64, (now - _lastFrame).TotalMilliseconds);
        if (ms < 1)
        {
            // Rendering бывает и дважды за кадр.
            return;
        }

        _lastFrame = now;
        Advance(ms);
        if (!Moving)
        {
            StopRendering();
            ScheduleFrame();
        }
    }

    /// <summary>Один кадр жизни: <paramref name="ms"/> миллисекунд движения. Тестам — без настоящих кадров.</summary>
    internal void Advance(double ms)
    {
        StepFrames(TimeSpan.FromMilliseconds(ms));
        switch (_mode)
        {
            case FoxMode.Walk:
                StepWalk(ms);
                break;
            case FoxMode.Jump:
                StepJump(ms);
                break;
            case FoxMode.Fly:
                StepFly(ms);
                break;
            case FoxMode.Drag:
                StepDrag(ms);
                break;
        }

        StepSquash(ms);
        StepGames(ms);
        StepEffects(ms);
        Live();
        Draw();
    }
}
