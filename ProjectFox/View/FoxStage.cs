using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>Чем Роза занят прямо сейчас.</summary>
internal enum FoxMode
{
    /// <summary>Ещё не пришёл или ушёл совсем.</summary>
    Off,

    /// <summary>Сидит на кромке и живёт: дышит, моргает, следит за курсором.</summary>
    Idle,

    /// <summary>Идёт или бежит по кромке.</summary>
    Walk,

    /// <summary>Прыгает с кромки на кромку по дуге.</summary>
    Jump,

    /// <summary>В руках у человека.</summary>
    Drag,

    /// <summary>Летит после броска.</summary>
    Fly,

    /// <summary>Спит клубком.</summary>
    Sleep,

    /// <summary>Сидит за полем ввода и выглядывает.</summary>
    Peek,

    /// <summary>Убежал за край окна.</summary>
    Away,

    /// <summary>Человек попросил спрятаться.</summary>
    Hidden
}

/// <summary>
/// Сцена Розы: элемент в слое окна, его кадры, движение, касания и реплики.
/// </summary>
/// <remarks>
/// <para>
/// Что делать, решает мозг (<see cref="FoxBrain"/>): сцена шлёт ему сигналы и исполняет действия.
/// Сама она решает только то, что мозгу знать незачем: куда смотреть, какую привычку показать в
/// покое, как лететь после броска и где приземлиться.
/// </para>
/// <para>
/// Дёшево в покое: кадр рисуется, только когда сменился, и листает кадры фоновый таймер ровно по
/// их длительности (дыхание сидя — раз в секунду). Покадровый <c>CompositionTarget.Rendering</c>
/// подключается, лишь пока Роза движется, и снимается, как только всё замерло. Свёрнутое,
/// спрятанное или заблокированное окно останавливает всё (<see cref="SetPaused"/>).
/// </para>
/// <para>
/// Место на кромке задаётся лапами (<c>_foot</c>, <c>_edgeY</c>), а не углом кадра: нижняя строка
/// рисунка у поз разная (сидя — 47-я, стоя — 46-я, клубком — 45-я), и по углу Роза то висел бы над
/// кромкой, то тонул в ней при каждой смене позы. Угол кадра считает <see cref="Place"/>.
/// </para>
/// </remarks>
internal sealed partial class FoxStage
{
    /// <summary>Как часто мозг получает тик: скука, обида, забвение, шалости.</summary>
    public static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(10);

    /// <summary>Сколько без дела, чтобы уснуть.</summary>
    public static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(3);

    /// <summary>Сколько ждать ответа, чтобы заскучать.</summary>
    public static readonly TimeSpan LongWaitAfter = TimeSpan.FromSeconds(45);

    /// <summary>Столбец кадра, над которым стоят лапы сидящего: по нему Роза ставится на кромку.</summary>
    private const int FootColumn = 22;

    /// <summary>Строка, которая у выглядывающего лежит на кромке: всё ниже неё прячет поле ввода.</summary>
    private const int PeekRow = 40;

    private readonly IFoxHost _host;
    private readonly FoxStore _store;
    private readonly FoxSprites _sprites;
    private readonly FoxBitmaps _bitmaps;
    private readonly Func<DateTime> _clock;
    private readonly Random _random;
    private readonly FoxSprite _sprite = new();
    private readonly TranslateTransform _move = new();
    private readonly ScaleTransform _squash = new();
    private readonly RotateTransform _tilt = new();
    private readonly DispatcherTimer _frames;
    private readonly DispatcherTimer _ticks;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly Dictionary<string, int> _grounds = new(StringComparer.Ordinal);

    private FoxMode _mode = FoxMode.Off;
    private string _anim = "sit";
    private string _next = "sit";
    private int _frame;
    private TimeSpan _elapsed;
    private TimeSpan _lastStep;
    private string? _face;
    private DateTime _faceUntil;
    private string? _item;
    private DateTime _itemUntil;
    private DateTime? _eyesAt;
    private bool _eyesCold;
    private DateTime _shakeUntil;
    private DateTime _turnAwayUntil;
    private DateTime _lastInput;
    private DateTime _lastTick;
    private DateTime? _turnSince;
    private bool _longWaitSaid;
    private bool _flip;
    private bool _cold;
    private bool _paused;

    /// <summary>Где лапы: столбец <see cref="FootColumn"/> кадра — в этой точке слоя.</summary>
    private double _foot;

    /// <summary>Кромка, на которой стоят лапы.</summary>
    private double _edgeY;

    /// <summary>Угол кадра в полёте и в руках, где лап на кромке нет.</summary>
    private double _x;
    private double _y;

    /// <summary>Где сидит: <c>home</c> — кромка поля ввода, <c>settings</c> — карточка настроек, <c>floor</c> — низ окна.</summary>
    private string _spot = "home";

    /// <summary>
    /// Насколько левее правого конца поля ввода он сидит, если его туда принесли; <c>null</c> —
    /// привычное место справа. От правого конца, а не точкой: поле меняет ширину вместе с окном и
    /// боковой панелью, а Роза сидит у его правого края.
    /// </summary>
    private double? _homeFromRight;

    /// <summary>Правый конец кромки, на которой он сидит, — каким он был при посадке или последней пересадке.</summary>
    private double _edgeRight;

    public FoxStage(IFoxHost host, FoxStore store, FoxSprites sprites, Func<DateTime> clock, Random random)
    {
        _host = host;
        _store = store;
        _sprites = sprites;
        _bitmaps = new FoxBitmaps(sprites);
        _clock = clock;
        _random = random;

        var transforms = new TransformGroup();
        transforms.Children.Add(_squash);
        transforms.Children.Add(_tilt);
        transforms.Children.Add(_move);
        _sprite.RenderTransform = transforms;
        _sprite.Visibility = Visibility.Collapsed;

        _frames = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _frames.Tick += (_, _) => OnFrameTimer();
        _ticks = new DispatcherTimer(DispatcherPriority.Background) { Interval = TickEvery };
        _ticks.Tick += (_, _) => OnTick();
        HookInput();
    }

    /// <summary>Своя модель Розы нужна: вид (<c>react</c>, <c>talk</c>) и что прочитать.</summary>
    public event Action<string, string>? MindRequested;

    /// <summary>Пора написать дневник за прошедшие дни.</summary>
    public event Action? DiaryDue;

    public FoxMode Mode => _mode;

    /// <summary>Где сидит: <c>home</c>, <c>settings</c>, <c>floor</c>, <c>send</c>, <c>chip</c>.</summary>
    internal string Spot => _spot;

    /// <summary>Угол кадра на слое, как его поставил последний кадр.</summary>
    internal Point Corner => new(_move.X, _move.Y);

    /// <summary>Сторона кадра на слое.</summary>
    internal double Side => _sprite.Side;

    /// <summary>Наклон в руках и в полёте, градусы по часовой.</summary>
    internal double Tilt => _tilt.Angle;

    /// <summary>Сам элемент Розы — окну, чтобы узнать его среди источников нажатия.</summary>
    public FrameworkElement Element => _sprite;

    /// <summary>Роза в руках, в игре или с ним говорят: Esc в это время отпускает его, а не останавливает ответ.</summary>
    public bool Holding => _mode == FoxMode.Drag || _laserUntil > _clock() || Talking || _game is not null;

    /// <summary>Приходит в окно: выбегает к полю ввода справа или сразу садится, если анимации выключены.</summary>
    public void Start()
    {
        if (_mode != FoxMode.Off)
        {
            return;
        }

        if (!_host.Layer.Children.Contains(_sprite))
        {
            _host.Layer.Children.Add(_sprite);
        }

        var state = _store.Snapshot();
        _cold = state.Cold;
        Refit();
        _lastInput = _clock();
        _lastTick = _lastInput;
        _ticks.Start();

        if (state.HiddenUntil is { } until && until > _clock())
        {
            _mode = FoxMode.Hidden;
            _backAt = until;
            return;
        }

        Arrive(greet: true);
    }

    /// <summary>Уходит совсем: Розу выключили в настройках или окно закрывается.</summary>
    public void Stop()
    {
        _mode = FoxMode.Off;
        _frames.Stop();
        _ticks.Stop();
        StopRendering();
        ClearEffects();
        ClearFeelings(detach: true);
        _sprite.Visibility = Visibility.Collapsed;
        _host.Layer.Children.Remove(_sprite);
        if (_callMark is not null)
        {
            _host.Layer.Children.Remove(_callMark);
            _callMark = null;
        }

        if (_bubble is not null)
        {
            _host.Layer.Children.Remove(_bubble);
            _bubble = null;
        }

        if (_talk is not null)
        {
            _host.Layer.Children.Remove(_talk);
            _talk = null;
            _talkInput = null;
        }

        _store.Flush();
    }

    /// <summary>Окно свёрнуто, спрятано или заблокировано: всё замирает.</summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;
        if (paused)
        {
            _frames.Stop();
            _ticks.Stop();
            if (_mode is FoxMode.Drag or FoxMode.Fly or FoxMode.Jump)
            {
                // Брошенный в сворачиваемое окно сядет туда, куда летел, а не повиснет в воздухе.
                var (spot, foot) = NearestSpot();
                Seat(spot, foot);
            }

            StopRendering();
            ClearEffects();

            // Свернули — самое время записать, но не потоком окна: тот сейчас сворачивает окно.
            Detached.Run(Task.Run(_store.Flush), "fox_flush");
            return;
        }

        _lastTick = _clock();
        _lastStep = _watch.Elapsed;
        if (_mode != FoxMode.Off)
        {
            _ticks.Start();
            Draw();

            // Застигнутый паузой посреди пробежки бежит дальше, а не стоит на полушаге.
            if (Moving)
            {
                StartRendering();
            }
            else
            {
                ScheduleFrame();
            }
        }
    }

    /// <summary>Сигнал мозгу и исполнение его ответа.</summary>
    public void Signal(FoxSignal signal)
    {
        if (_mode == FoxMode.Off)
        {
            return;
        }

        if (signal.Kind is not (FoxSignalKind.Tick or FoxSignalKind.MindReaction or FoxSignalKind.MindEscape))
        {
            _lastInput = _clock();
            if (_mode == FoxMode.Sleep)
            {
                Wake();
            }
        }

        if (signal.Kind is FoxSignalKind.Pet or FoxSignalKind.Scratch or FoxSignalKind.Treat)
        {
            // Бесячество ждёт ласки: погладили — дальше не топает и не дразнит.
            _pettedAt = _clock();
        }

        var situation = _host.Situation() with
        {
            RecentBoops = RecentBoops(),
            Away = _mode is FoxMode.Away or FoxMode.Hidden
        };
        List<FoxAction> actions = [];
        _store.Update(state => actions = FoxBrain.Next(state, signal, situation, _clock(), _random), lazy: signal.Kind == FoxSignalKind.Tick);
        Apply(actions);
    }

    /// <summary>Окно или поле ввода сменили размер: Роза пересаживается на ту же кромку.</summary>
    public void Relayout()
    {
        if (_mode == FoxMode.Off)
        {
            return;
        }

        Refit();
        if (_spot == "column" && _host.Column() is null && !Airborne)
        {
            // Колонку свернули из-под лап: перебирается на поле ввода.
            JumpTo("home", HomeFoot(_host.Home()));
        }
        else if (!Airborne)
        {
            // Кромка уехала или сменила ширину: место держится от её правого конца.
            // Кто ещё выбегает из-за края (лапы правее кромки), того не трогаем: он сам добежит.
            var edge = EdgeOf(_spot);
            var shift = edge.Right - _edgeRight;
            var onEdge = _foot <= _edgeRight + Px(FoxSprites.FrameSize);
            _edgeY = edge.Y;
            _edgeRight = edge.Right;
            if (onEdge)
            {
                _foot = ClampFoot(edge, _foot + shift);
            }

            if (_walkTo is { } target && target <= _edgeRight - shift)
            {
                _walkTo = ClampFoot(edge, target + shift);
            }
        }

        Draw();
        PlaceCallMark();
        if (_mode == FoxMode.Idle)
        {
            OffTheButtons();
        }
    }

    /// <summary>Сменился профиль: другая память — другой Роза. Приходит заново.</summary>
    public void Reload()
    {
        var running = _mode != FoxMode.Off;
        Stop();
        if (running)
        {
            Start();
        }
    }

    /// <summary>
    /// Зовёт раньше срока — из настроек Розы. Спрятанного по просьбе человека — сразу; убежавшего —
    /// как следом лапки: обиженный может и не прийти.
    /// </summary>
    public void Unhide()
    {
        if (_mode == FoxMode.Away)
        {
            Signal(FoxSignalKind.Call);
            return;
        }

        _store.Update(state => state.HiddenUntil = null);
        if (_mode == FoxMode.Hidden)
        {
            Arrive(greet: true);
        }
    }

    private void Refit()
    {
        var dpi = VisualTreeHelper.GetDpi(_host.Layer).DpiScaleX;
        _sprite.Fit(_host.Size(), dpi);
        _bitmaps.Prescale = _sprite.Prescale;
        _squash.CenterX = _sprite.Side / 2;
        _squash.CenterY = _sprite.Side;
    }

    /// <summary>Сколько точек слоя занимают <paramref name="pixels"/> пикселей рисунка при нынешнем размере.</summary>
    private double Px(double pixels) => _sprite.Side * pixels / FoxSprites.FrameSize;

    private void Apply(IEnumerable<FoxAction> actions)
    {
        var now = _clock();
        var visible = _mode is not (FoxMode.Away or FoxMode.Hidden);
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case FoxActionKind.Anim when visible:
                    React(action.Name);
                    break;
                case FoxActionKind.Face when visible:
                    _face = action.Name;
                    _faceUntil = now.AddMilliseconds(action.Ms);
                    break;
                case FoxActionKind.Say when visible:
                    Bubble(Loc.Get(action.Name), action.Ms);
                    break;
                case FoxActionKind.Line when visible:
                    Bubble(action.Text, action.Ms);
                    break;
                case FoxActionKind.Particle when visible:
                    Burst(action.Name, Math.Max(1, action.Ms));
                    break;
                case FoxActionKind.Item when visible:
                    _item = action.Name;
                    _itemUntil = now.AddMilliseconds(action.Ms);
                    break;
                case FoxActionKind.Shake when visible:
                    _shakeUntil = now.AddMilliseconds(action.Ms);
                    StartRendering();
                    break;
                case FoxActionKind.Emote when visible:
                    ShowEmote(action.Name, action.Ms);
                    break;
                case FoxActionKind.Effect when visible:
                    Effect(action.Name, action.Ms);
                    break;
                case FoxActionKind.Do when visible:
                    Do(action.Name);
                    break;
                case FoxActionKind.Shout:
                    Shout(Loc.Get(action.Name), action.Ms);
                    break;
                case FoxActionKind.Eyes:
                    // Смена глаз — с морганием: закрыл одними, открыл уже другими.
                    _face = "blink";
                    _faceUntil = now.AddMilliseconds(160);
                    _eyesAt = now.AddMilliseconds(160);
                    _eyesCold = action.Name == "cold";
                    break;
                case FoxActionKind.Go:
                    GoTo(action.Name);
                    break;
                case FoxActionKind.TurnAway when visible:
                    _turnAwayUntil = now.AddSeconds(5);
                    break;
                case FoxActionKind.Escape:
                    RunOff(now.AddMilliseconds(action.Ms), FoxMode.Away);
                    break;
                case FoxActionKind.Hide:
                    RunOff(_store.Snapshot().HiddenUntil ?? now.AddHours(1), FoxMode.Hidden);
                    break;
                case FoxActionKind.Mischief when visible:
                    Mischief(action.Name);
                    break;
                case FoxActionKind.AskMind:
                    if (action.Name == "talk" && visible)
                    {
                        Ponder();
                    }

                    MindRequested?.Invoke(action.Name, action.Text);
                    break;
                case FoxActionKind.Diary:
                    DiaryDue?.Invoke();
                    break;
            }
        }

        Draw();
        ScheduleFrame();
    }

    /// <summary>Анимация реакции. Сидячие реакции — только сидя: на бегу или в полёте их не видно.</summary>
    private void React(string anim)
    {
        if (!_sprites.Animations.ContainsKey(anim))
        {
            return;
        }

        switch (_mode)
        {
            case FoxMode.Idle:
                _lying = false;
                Play(anim, "sit");

                // Повторяющаяся реакция (кивает, ласкается) идёт не вечно: «кивает в такт» — пока читает ответ.
                _reactUntil = _sprites.Animations[anim].Loop
                    ? _clock().AddSeconds(anim == "nod" ? 6 : 2.5)
                    : DateTime.MaxValue;
                break;
            case FoxMode.Drag when anim == "hang":
            case FoxMode.Fly when anim == "fly":
                Play(anim, anim);
                break;
        }
    }

    private void Play(string anim, string next)
    {
        if (!_sprites.Animations.TryGetValue(anim, out var animation) || animation.Frames.Count == 0)
        {
            return;
        }

        _anim = anim;
        _next = _sprites.Animations.ContainsKey(next) ? next : "sit";
        _frame = 0;
        _elapsed = TimeSpan.Zero;
    }

    /// <summary>Листает кадры на <paramref name="delta"/> времени.</summary>
    private void StepFrames(TimeSpan delta)
    {
        if (_mode == FoxMode.Jump)
        {
            // Кадры прыжка ведёт дуга (StepJump), а не часы.
            return;
        }

        var animation = _sprites.Animations[_anim];
        _elapsed += delta;
        var guard = 0;
        while (_elapsed >= animation.HoldOf(_frame) && guard++ < 64)
        {
            _elapsed -= animation.HoldOf(_frame);
            _frame++;
            if (_frame < animation.Frames.Count)
            {
                continue;
            }

            if (animation.Loop)
            {
                _frame = 0;
            }
            else
            {
                Play(_next, BasePose());
                animation = _sprites.Animations[_anim];
            }
        }
    }

    /// <summary>Поза, к которой Роза возвращается после реакции.</summary>
    private string BasePose() => _mode switch
    {
        FoxMode.Peek => "peek",
        FoxMode.Sleep => "sleep",
        FoxMode.Drag => "hang",
        FoxMode.Fly => "fly",
        FoxMode.Walk => _gait,
        _ => _lying ? _lieAnim : "sit"
    };

    private void OnFrameTimer()
    {
        if (_paused || _mode == FoxMode.Off)
        {
            _frames.Stop();
            return;
        }

        var now = _watch.Elapsed;
        StepFrames(now - _lastStep);
        _lastStep = now;
        Live();
        Draw();
        ScheduleFrame();
    }

    /// <summary>
    /// Таймер кадров — ровно до следующей смены кадра или конца мордочки. Пока идёт покадровая
    /// отрисовка, кадры листает она, а таймер стоит.
    /// </summary>
    private void ScheduleFrame()
    {
        // Убежавший кадров не листает, но фразу у следа «Позвать» таймер должен убрать вовремя.
        var shouting = _mode == FoxMode.Away && _bubble is { Visibility: Visibility.Visible };
        if (_paused || _mode is FoxMode.Off or FoxMode.Hidden || (_mode == FoxMode.Away && !shouting) || _rendering)
        {
            _frames.Stop();
            return;
        }

        var animation = _sprites.Animations[_anim];
        var wait = animation.HoldOf(Math.Min(_frame, animation.Frames.Count - 1)) - _elapsed;
        var now = _clock();
        var soon = _later.Count > 0 ? _later.Min(entry => entry.Due) : DateTime.MaxValue;
        foreach (var until in (DateTime[])[_faceUntil, _itemUntil, _eyesAt ?? DateTime.MinValue, _nextHabit, _reactUntil, _bubbleUntil, _peekUntil, _emoteUntil, soon])
        {
            if (until > now && until - now < wait)
            {
                wait = until - now;
            }
        }

        // Взгляд за курсором и наведение опрашиваются чаще, чем меняется кадр сидящего.
        var poll = _hovering ? 250 : 500;
        _lastStep = _watch.Elapsed;
        _frames.Interval = TimeSpan.FromMilliseconds(Math.Clamp(wait.TotalMilliseconds, 120, poll));
        _frames.Start();
    }

    private void OnTick()
    {
        if (_paused || _mode == FoxMode.Off)
        {
            return;
        }

        var now = _clock();
        var seconds = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        if (_mode is FoxMode.Hidden or FoxMode.Away && now >= _backAt)
        {
            Arrive(greet: _mode == FoxMode.Hidden);
        }

        if (_mode == FoxMode.Hidden)
        {
            return;
        }

        var situation = _host.Situation();
        if (situation.TurnRunning)
        {
            _turnSince ??= now;
            if (!_longWaitSaid && now - _turnSince >= LongWaitAfter)
            {
                _longWaitSaid = true;
                Signal(FoxSignalKind.LongWait);
            }
        }
        else
        {
            _turnSince = null;
            _longWaitSaid = false;
        }

        if (_mode == FoxMode.Idle && now - _lastInput >= SleepAfter && situation.SinceTyping >= SleepAfter && !situation.TurnRunning)
        {
            Sleep();
        }

        Signal(new FoxSignal(FoxSignalKind.Tick, Value: seconds));
    }

    /// <summary>Рисует кадр (картинка берётся из кэша и не перерисовывается, если та же) и ставит Розу на место.</summary>
    private void Draw()
    {
        var now = _clock();
        if (_mode is FoxMode.Off or FoxMode.Hidden or FoxMode.Away)
        {
            _sprite.Visibility = Visibility.Collapsed;
            PlaceBubble();
            PlaceEmote(now);
            PlaceTremble(now);
            return;
        }

        if (_eyesAt is { } eyesAt && eyesAt <= now)
        {
            _eyesAt = null;
            _cold = _eyesCold;
        }

        _sprite.Visibility = Visibility.Visible;
        var face = _face is not null && _faceUntil > now ? _face : null;
        var item = _item is not null && _itemUntil > now ? _item : null;
        var animation = _sprites.Animations[_anim];
        var frame = Math.Min(_frame, animation.Frames.Count - 1);
        _sprite.Show(_bitmaps.Frame(animation, frame, face, item, _cold, _flip), () => _sprites.Compose(animation.Frames[frame], face, item), _flip);
        Place();
        PlaceBubble();
        PlaceTalk();
        PlaceEmote(now);
        PlaceTremble(now);
    }

    /// <summary>Ставит угол кадра по лапам (на кромке) или по <c>_x</c>/<c>_y</c> (в воздухе), с выравниванием по пикселям экрана.</summary>
    private void Place()
    {
        double x, y;
        if (Airborne)
        {
            x = _x;
            y = _y;
        }
        else
        {
            (x, y) = CornerAt(_foot, _edgeY, _flip);
        }

        if (_shakeUntil > _clock())
        {
            x += (_random.Next(2) == 0 ? -1 : 1) * Px(1);
        }

        var dpi = VisualTreeHelper.GetDpi(_host.Layer).DpiScaleX;
        _move.X = Math.Round(x * dpi) / dpi;
        _move.Y = Math.Round(y * dpi) / dpi;
        _sprite.Clip = _mode == FoxMode.Peek ? new RectangleGeometry(new Rect(0, 0, _sprite.Side, Px(PeekRow + 1))) : null;
    }

    /// <summary>Угол кадра, при котором лапы нынешней позы стоят в точке (<paramref name="foot"/>, <paramref name="edgeY"/>).</summary>
    private (double X, double Y) CornerAt(double foot, double edgeY, bool flip) =>
        (foot - Px(flip ? FoxSprites.FrameSize - 1 - FootColumn : FootColumn),
         edgeY - Px(_mode == FoxMode.Peek ? PeekRow : Ground(_anim)));

    /// <summary>Роза не на кромке: угол кадра задан сам по себе.</summary>
    private bool Airborne => _mode is FoxMode.Drag or FoxMode.Fly or FoxMode.Jump;

    /// <summary>Нижняя строка рисунка у анимации: на ней лапы.</summary>
    private int Ground(string anim)
    {
        if (!_grounds.TryGetValue(anim, out var ground))
        {
            ground = FoxSprites.GroundOf(_sprites.Animations[anim]);
            _grounds[anim] = ground;
        }

        return ground;
    }

    private int RecentBoops()
    {
        var since = _clock().AddSeconds(-8);
        _boops.RemoveAll(time => time < since);
        return _boops.Count;
    }
}
