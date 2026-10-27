using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Фон «Что нового» (1.37.0) — сцена «Занавес» концепта: волнистые линии по всей ширине растут
/// снизу и медленно текут вверх, каждая третья толще, рядом с линиями плывут звёздочки.
/// </summary>
/// <remarks>
/// <para>
/// Сцена — постоянные визуалы, а не рисование в каждом кадре. Волна, текущая вверх, — это та же
/// синусоида, сдвинутая по вертикали: x(y, t) = S(y + w·t/k). Поэтому линия строится один раз, а
/// кадр только двигает её сдвигом и обрезкой «докуда выросла». WPF раскладывает штрих на
/// треугольники на процессоре, и делать это заново в каждом кадре стоило целого ядра; сдвиг
/// готового штриха почти ничего не стоит, и сцена идёт с частотой монитора.
/// </para>
/// <para>
/// Кадры идут от <see cref="CompositionTarget.Rendering"/> только пока лист открыт и окно
/// активно. При выключенных «Анимациях» — один неподвижный кадр (t = 40), как у концепта при
/// «уменьшить движение». <see cref="Freeze"/> ставит любой кадр — для снимков и тестов.
/// </para>
/// </remarks>
public sealed class WhatsNewCurtain : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(WhatsNewCurtain), new PropertyMetadata(null, Rebuild));

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(Brush), typeof(WhatsNewCurtain), new PropertyMetadata(null, Rebuild));

    public static readonly DependencyProperty StarProperty = DependencyProperty.Register(
        nameof(Star), typeof(Brush), typeof(WhatsNewCurtain), new PropertyMetadata(null, Rebuild));

    public static readonly DependencyProperty ShadeProperty = DependencyProperty.Register(
        nameof(Shade), typeof(Brush), typeof(WhatsNewCurtain), new PropertyMetadata(null, Rebuild));

    /// <summary>Неподвижный кадр без анимаций: линии к этому времени давно выросли.</summary>
    internal const double StillTime = 40;

    /// <summary>Сколько длится уход: линии втягиваются вниз, фон гаснет.</summary>
    internal const double LeaveTime = .62;

    private const double GlassRadius = 12;

    /// <summary>Четырёхконечная звезда с прямыми гранями: плоская и читается даже на шести точках.</summary>
    private static readonly Geometry StarShape = Frozen(Geometry.Parse("M0,-1 L0.2,-0.2 L1,0 L0.2,0.2 L0,1 L-0.2,0.2 L-1,0 L-0.2,-0.2Z"));

    private static readonly Geometry GlowShape = Frozen(new EllipseGeometry(new Point(), 1, 1));

    private readonly VisualCollection _children;
    private readonly Stopwatch _clock = new();
    private ContainerVisual _glassLayer = new();
    private DrawingVisual _shadow = new();
    private readonly RectangleGeometry _glassClip = new();
    private List<Flow> _flows = [];
    private Size _built;
    private double? _frozen;
    private double? _leaveAt;
    private bool _hooked;
    private bool _building;
    private Rect? _glass;
    private double _glassOpacity;

    public WhatsNewCurtain()
    {
        _children = new VisualCollection(this);

        // Лист убрали из окна посреди сцены — подписка на кадры не должна его пережить.
        Unloaded += (_, _) => Hook(false);
    }

    /// <summary>Каждый кадр, со временем сцены: по нему лист проявляет и уводит карточку.</summary>
    public event Action<double>? Ticked;

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Line
    {
        get => (Brush?)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public Brush? Star
    {
        get => (Brush?)GetValue(StarProperty);
        set => SetValue(StarProperty, value);
    }

    /// <summary>Цвет тени под карточкой.</summary>
    public Brush? Shade
    {
        get => (Brush?)GetValue(ShadeProperty);
        set => SetValue(ShadeProperty, value);
    }

    /// <summary>Время сцены в секундах.</summary>
    public double Time => _frozen ?? (UiMotion.Enabled ? _clock.Elapsed.TotalSeconds : StillTime);

    /// <summary>Когда начался уход (время сцены); <c>null</c> — сцена не уходит.</summary>
    internal double? LeaveAt => _leaveAt;

    /// <summary>Подписана ли сцена на кадры окна.</summary>
    internal bool Running => _hooked;

    protected override int VisualChildrenCount => _children.Count;

    /// <summary>Начинает сцену с нуля.</summary>
    public void Start()
    {
        _frozen = null;
        _leaveAt = null;
        _clock.Restart();
        if (UiMotion.Enabled)
        {
            Hook(true);
        }

        Tick();
    }

    public void Stop()
    {
        Hook(false);
        _clock.Stop();
    }

    /// <summary>Начинает уход: линии втягиваются вниз, каждая со своей задержкой, — от середины к краям.</summary>
    public void Leave()
    {
        _leaveAt = Time;
        Tick();
    }

    /// <summary>
    /// Стекло карточки (backdrop-filter концепта): под карточкой сцена рисуется размытой — те же
    /// линии широкими бледными штрихами, звёзды мягким пятном, — и тень под ней. Делает это сама
    /// сцена, а не эффекты WPF: живое размытие копии сцены пересчитывалось каждый кадр.
    /// </summary>
    /// <param name="area">Где карточка, в точках сцены; <c>null</c> — карточки нет.</param>
    /// <param name="opacity">Насколько карточка проявилась.</param>
    internal void SetGlass(Rect? area, double opacity)
    {
        _glassOpacity = opacity;
        _glassLayer.Opacity = area is null ? 0 : opacity;
        if (area == _glass)
        {
            return;
        }

        _glass = area;
        if (area is { } glass)
        {
            _glassClip.Rect = glass;
            _glassClip.RadiusX = _glassClip.RadiusY = GlassRadius;
            DrawShadow(glass);
        }
    }

    /// <summary>Останавливает сцену на кадре <paramref name="seconds"/> — для снимков и тестов.</summary>
    internal void Freeze(double seconds)
    {
        Hook(false);
        _frozen = seconds;
        Tick();
    }

    protected override Visual GetVisualChild(int index) => _children[index];

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Build();
        Apply(Time);
    }

    private static void Rebuild(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var curtain = (WhatsNewCurtain)d;
        curtain.Build();
        curtain.Apply(curtain.Time);
    }

    private void Hook(bool on)
    {
        if (on == _hooked)
        {
            return;
        }

        _hooked = on;
        if (on)
        {
            CompositionTarget.Rendering += OnFrame;
        }
        else
        {
            CompositionTarget.Rendering -= OnFrame;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // Лист спрятали, не закрыв (окно свернули в трей): кадры не нужны, пока его не покажут.
        if (!IsVisible)
        {
            return;
        }

        // Человек ушёл в другую программу или свернул окно — сцена замирает и ничего не стоит;
        // время идёт дальше, и по возвращении линии просто оказываются чуть дальше. Уход не ждёт.
        if (_leaveAt is null && Window.GetWindow(this) is { IsActive: false } or { WindowState: WindowState.Minimized })
        {
            return;
        }

        Tick();
    }

    private void Tick()
    {
        Apply(Time);
        Ticked?.Invoke(Time);
    }

    // ---- Сцена: построение по размеру ----

    private void Build()
    {
        // Добавление визуалов может поднять раскладку, а с ней и новую постройку посреди этой:
        // вложенная постройка писала бы в те же поля, и слой стекла получал бы детей дважды.
        if (_building)
        {
            return;
        }

        _building = true;
        try
        {
            BuildScene();
        }
        finally
        {
            _building = false;
        }
    }

    private void BuildScene()
    {
        _children.Clear();
        _flows = [];
        _built = RenderSize;
        if (_built.Width <= 0 || _built.Height <= 0)
        {
            return;
        }

        _children.Add(Solid(Fill, new Rect(_built)));

        var random = new SceneRandom(97);
        var count = Math.Max(7, (int)Math.Round(_built.Width / 92, MidpointRounding.AwayFromZero));
        var gap = _built.Width / count;
        for (var i = 0; i < count; i++)
        {
            var x0 = gap * (i + .5) + (random.Next() - .5) * gap * .3;
            var thick = i % 3 == 1;
            var amp = 18 + random.Next() * 34;
            var k = 2 * Math.PI / (_built.Height * (.55 + random.Next() * .6));
            var w = .35 + random.Next() * .3;
            var phase = random.Next() * 6.28;
            var centre = Math.Abs(i - (count - 1) / 2.0) / ((count - 1) / 2.0);

            var flow = new Flow(x0, amp, k, w, phase)
            {
                Width = thick ? 2.6 : 1.1 + random.Next() * .5,
                Opacity = thick ? 1 : .45 + random.Next() * .2,
                Start = .05 + (1 - centre) * .6 + random.Next() * .25,
                Grow = 2.4 + random.Next() * .6,
                LeaveDelay = centre * .15
            };

            if (thick)
            {
                AddStars(flow, random, riders: 2, riderSize: (5.5, 8.5), sparks: 7, spread: 26, sparkSize: (1.6, 10.5));
            }
            else
            {
                var riders = random.Next() < .5 ? 1 : 0;
                AddStars(flow, random, riders, riderSize: (3.4, 5), sparks: 2, spread: 16, sparkSize: (1.4, 5));
            }

            _flows.Add(flow);
        }

        var lines = new ContainerVisual();
        var stars = new ContainerVisual();
        var glass = new ContainerVisual { Clip = _glassClip };
        glass.Children.Add(Solid(Fill, new Rect(_built)));
        var glow = Glow(Star);
        foreach (var flow in _flows)
        {
            var shape = flow.Shape(_built.Height);
            lines.Children.Add(flow.Follow(Stroke(shape, flow.Width, flow.Opacity)));

            // Под стеклом та же линия размытой: два широких бледных штриха, тот же сдвиг и рост.
            var weight = flow.Opacity * flow.Width / 1.6;
            var haze = new DrawingVisual();
            using (var dc = haze.RenderOpen())
            {
                dc.PushOpacity(.035 * weight);
                dc.DrawGeometry(null, Pen(Line, 26), shape);
                dc.Pop();
                dc.PushOpacity(.05 * weight);
                dc.DrawGeometry(null, Pen(Line, 11), shape);
                dc.Pop();
            }

            glass.Children.Add(flow.Follow(haze));

            foreach (var star in flow.Stars)
            {
                star.Mark = Mark(Star, StarShape);
                stars.Children.Add(star.Mark);
                star.Glow = Mark(glow, GlowShape);
                glass.Children.Add(star.Glow);
            }
        }

        _children.Add(lines);
        _children.Add(stars);

        _shadow = new DrawingVisual();
        _glassLayer = new ContainerVisual { Opacity = _glass is null ? 0 : _glassOpacity };
        _glassLayer.Children.Add(_shadow);
        _glassLayer.Children.Add(glass);
        if (_glass is { } area)
        {
            DrawShadow(area);
        }

        _children.Add(_glassLayer);
    }

    /// <summary>Тень (0 30px 70px −24px концепта) — стопкой бледных скруглений.</summary>
    private void DrawShadow(Rect glass)
    {
        const int steps = 10;
        using var dc = _shadow.RenderOpen();
        var core = new Rect(glass.X + 24, glass.Y + 24 + 30, Math.Max(0, glass.Width - 48), Math.Max(0, glass.Height - 48));
        dc.PushOpacity(.08);
        for (var i = 0; i < steps; i++)
        {
            var spread = i * 7.0;
            var layer = core;
            layer.Inflate(spread, spread);
            dc.DrawRoundedRectangle(Shade, null, layer, GlassRadius + spread, GlassRadius + spread);
        }

        dc.Pop();
    }

    /// <summary>
    /// Звёзды линии плывут рядом с ней, не касаясь: одни быстро, другие медленно, с волной.
    /// Порядок обращений к случаю — как в концепте, отступ вбок берётся последним.
    /// </summary>
    private static void AddStars(
        Flow flow,
        SceneRandom random,
        int riders,
        (double Min, double Max) riderSize,
        int sparks,
        double spread,
        (double Min, double Max) sparkSize)
    {
        const double rideSpeed = 120;
        const double drift = 46;
        const double clearance = 7;

        for (var i = 0; i < riders; i++)
        {
            var star = new FlowStar
            {
                Rides = true,
                Size = Lerp(riderSize.Min, riderSize.Max, random.Next()),
                Speed = rideSpeed * (.8 + .4 * random.Next()),
                At = flow.Start + .45 + i * (1.6 + random.Next() * 1.4),
                Phase = random.Next() * 6.28
            };
            star.Offset = Aside(random, clearance + star.Size, spread + star.Size);
            flow.Stars.Add(star);
        }

        for (var i = 0; i < sparks; i++)
        {
            var size = random.Next();
            var star = new FlowStar
            {
                Size = Lerp(sparkSize.Min, sparkSize.Max, size * size),
                Y0 = random.Next()
            };
            star.Speed = drift * (.85 + .3 * random.Next());
            star.Phase = random.Next() * 6.28;
            star.Offset = Aside(random, clearance + star.Size, spread + star.Size);
            flow.Stars.Add(star);
        }
    }

    /// <summary>Отступ от линии влево или вправо, не ближе <paramref name="near"/>.</summary>
    private static double Aside(SceneRandom random, double near, double far)
    {
        var side = random.Next() < .5 ? -1 : 1;
        return side * Lerp(near, Math.Max(near, far), random.Next());
    }

    // ---- Сцена: кадр ----

    private void Apply(double t)
    {
        var height = _built.Height;
        foreach (var flow in _flows)
        {
            ApplyFlow(flow, t, height);
            foreach (var star in flow.Stars)
            {
                ApplyStar(flow, star, t, height);
            }
        }
    }

    /// <summary>Линия тянется от низа (Front — докуда дотянулась), потом течёт: сдвиг по волне.</summary>
    private void ApplyFlow(Flow flow, double t, double height)
    {
        const double top = -40;
        var bottom = height + 40;
        var q = Progress(t, flow.Start, flow.Grow);
        var p = 1 - (1 - q) * (1 - q);
        flow.Front = Lerp(bottom, top, p);
        if (_leaveAt is { } leave)
        {
            // Уход — рост наоборот: конец линии стекает вниз, середина начинает первой.
            var back = Progress(t, leave + flow.LeaveDelay, .45);
            flow.Front = Lerp(flow.Front, bottom, back * back * (3 - 2 * back));
        }

        flow.Move(t, q > 0 && flow.Front < bottom, _built.Width, bottom);
    }

    /// <summary>Звёзды появляются, только когда линия до них дотянулась; у краёв экрана гаснут.</summary>
    private static void ApplyStar(Flow flow, FlowStar star, double t, double height)
    {
        double y;
        if (star.Rides)
        {
            y = t < star.At ? double.NaN : height + 30 - ((t - star.At) * star.Speed) % (height + 160);
        }
        else
        {
            var cycle = height + 100;
            y = height + 50 - (((star.Y0 * cycle + t * star.Speed) % cycle) + cycle) % cycle;
        }

        if (double.IsNaN(y) || y < flow.Front || y < -30 || y > height + 30)
        {
            star.Hide();
            return;
        }

        var grown = Clamp((y - flow.Front) / 50);
        var edge = Clamp((y + 30) / 70) * Clamp((height + 30 - y) / 70);
        var twinkle = 1 + .14 * Math.Sin(t * 1.9 + star.Phase);
        var scale = Math.Max(0.0001, star.Size * EaseBack(grown) * twinkle);
        var x = flow.X(y, t) + star.Offset;

        var mark = Matrix.Identity;
        mark.Scale(scale, scale);
        mark.Rotate(4 * Math.Sin(t * .8 + star.Phase));
        mark.Translate(x, y);

        var glow = Matrix.Identity;
        var size = scale * 1.6 + 6;
        glow.Scale(size, size);
        glow.Translate(x, y);

        star.Show(mark, edge, glow, Math.Min(1, scale / 10) * edge * .3);
    }

    // ---- Помощники ----

    /// <summary>
    /// Линия с прозрачностью внутри рисунка, а не у визуала: прозрачность визуала WPF рисует
    /// через промежуточный слой, и на пятнадцати линиях это был бы пятнадцатый слой в каждом кадре.
    /// </summary>
    private DrawingVisual Stroke(Geometry shape, double width, double opacity)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushOpacity(opacity);
            dc.DrawGeometry(null, Pen(Line, width), shape);
            dc.Pop();
        }

        return visual;
    }

    private static DrawingVisual Solid(Brush? brush, Rect area)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(brush, null, area);
        }

        return visual;
    }

    private static DrawingVisual Mark(Brush? brush, Geometry shape)
    {
        var mark = new DrawingVisual { Transform = new MatrixTransform(), Opacity = 0 };
        using (var dc = mark.RenderOpen())
        {
            dc.DrawGeometry(brush, null, shape);
        }

        return mark;
    }

    private static Pen Pen(Brush? brush, double width)
    {
        var pen = new Pen(brush, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static Brush? Glow(Brush? star)
    {
        if (star is not SolidColorBrush solid)
        {
            return null;
        }

        var glow = new RadialGradientBrush(solid.Color, Color.FromArgb(0, solid.Color.R, solid.Color.G, solid.Color.B));
        glow.Freeze();
        return glow;
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static double Clamp(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    private static double Lerp(double a, double b, double p) => a + (b - a) * p;

    private static double Progress(double t, double start, double duration) => Clamp((t - start) / duration);

    private static double EaseBack(double p)
    {
        const double c1 = 2.2;
        const double c3 = c1 + 1;
        return 1 + c3 * Math.Pow(p - 1, 3) + c1 * Math.Pow(p - 1, 2);
    }

    private sealed class Flow(double x0, double amp, double k, double w, double phase)
    {
        /// <summary>Сдвиг волны — общий для линии и её размытой копии под стеклом.</summary>
        private readonly TranslateTransform _shift = new();

        /// <summary>Обрезка «докуда выросла» — тоже общая.</summary>
        private readonly RectangleGeometry _grown = new(Rect.Empty);

        private readonly List<ContainerVisual> _holders = [];

        public double Width { get; init; }

        public double Opacity { get; init; }

        public double Start { get; init; }

        public double Grow { get; init; }

        /// <summary>Задержка ухода: середина втягивается первой.</summary>
        public double LeaveDelay { get; init; }

        /// <summary>Докуда линия дотянулась в последнем кадре; звёзды выше неё не видны.</summary>
        public double Front { get; set; } = double.MaxValue;

        public List<FlowStar> Stars { get; } = [];

        private double Wavelength => 2 * Math.PI / k;

        /// <summary>Где линия на высоте <paramref name="y"/> в момент <paramref name="t"/>: волна течёт вверх.</summary>
        public double X(double y, double t) => x0 + amp * Math.Sin(k * y + w * t + phase);

        /// <summary>
        /// Неподвижная синусоида на высоту сцены и ещё одну длину волны: сдвиг вверх на остаток
        /// от w·t/k по длине волны даёт ровно ту же картину, что и пересчёт каждой точки.
        /// </summary>
        public StreamGeometry Shape(double height)
        {
            const double step = 4;
            const double top = -40;
            var bottom = height + 40 + Wavelength;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(X(top, 0), top), false, false);
                for (var y = top + step; y < bottom; y += step)
                {
                    context.LineTo(new Point(X(y, 0), y), true, true);
                }

                context.LineTo(new Point(X(bottom, 0), bottom), true, true);
            }

            geometry.Freeze();
            return geometry;
        }

        /// <summary>
        /// Вешает визуал на сдвиг и обрезку этой линии. Линия один раз растрируется в картинку:
        /// иначе WPF заново раскладывал бы штрих на треугольники при каждом сдвиге на долю
        /// точки, а картинку видеокарта двигает даром.
        /// </summary>
        public ContainerVisual Follow(ContainerVisual visual)
        {
            visual.CacheMode = new BitmapCache { EnableClearType = false };
            visual.Transform = _shift;
            var holder = new ContainerVisual { Clip = _grown, Opacity = 0 };
            holder.Children.Add(visual);
            _holders.Add(holder);
            return holder;
        }

        public void Move(double t, bool visible, double width, double bottom)
        {
            _shift.Y = -((w * t / k) % Wavelength);
            _grown.Rect = visible ? new Rect(-200, Front, width + 400, Math.Max(0, bottom - Front)) : Rect.Empty;
            foreach (var holder in _holders)
            {
                holder.Opacity = visible ? 1 : 0;
            }
        }
    }

    private sealed class FlowStar
    {
        public bool Rides { get; init; }

        public double Size { get; init; }

        public double Speed { get; set; }

        public double At { get; init; }

        public double Phase { get; set; }

        public double Y0 { get; init; }

        /// <summary>Отступ от линии вбок: звезда плывёт рядом с ней, а не по ней.</summary>
        public double Offset { get; set; }

        public DrawingVisual? Mark { get; set; }

        public DrawingVisual? Glow { get; set; }

        public void Hide()
        {
            if (Mark is not null)
            {
                Mark.Opacity = 0;
            }

            if (Glow is not null)
            {
                Glow.Opacity = 0;
            }
        }

        public void Show(Matrix mark, double markOpacity, Matrix glow, double glowOpacity)
        {
            Place(Mark, mark, markOpacity);
            Place(Glow, glow, glowOpacity);
        }

        private static void Place(DrawingVisual? visual, Matrix matrix, double opacity)
        {
            if (visual is { Transform: MatrixTransform transform })
            {
                transform.Matrix = matrix;
                visual.Opacity = opacity;
            }
        }
    }

    /// <summary>Детерминированный случай концепта (mulberry32): одна и та же раскладка при каждом показе.</summary>
    private sealed class SceneRandom(int seed)
    {
        private int _seed = seed;

        public double Next()
        {
            unchecked
            {
                _seed += 0x6D2B79F5;
                var t = Imul(_seed ^ (int)((uint)_seed >> 15), 1 | _seed);
                t = (t + Imul(t ^ (int)((uint)t >> 7), 61 | t)) ^ t;
                return (uint)(t ^ (int)((uint)t >> 14)) / 4294967296.0;
            }
        }

        private static int Imul(int a, int b) => unchecked(a * b);
    }
}
