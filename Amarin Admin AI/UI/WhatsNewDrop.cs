using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Amarin.UI;

/// <summary>
/// Фон «Что нового» (1.37.1) — сцена «Капля»: в верхний центр окна падает капля, и два красных
/// кольца расходятся, как волна на воде, — быстро, с замедлением и лёгким перелётом, — и остаются:
/// большое обнимает карточку по бокам, малое стоит полукругом над ней. Вокруг медленно падают перья
/// ворона.
/// </summary>
/// <remarks>
/// <para>
/// Перенос концепта <c>scene-drop.js</c> один в один: те же доли, плавности и случай. Сцена —
/// постоянные визуалы: перо строится один раз, а кадр двигает его поворотом и сдвигом; кольцо
/// перерисовывается, только когда изменились его радиус, штрих или прозрачность, — в покое между
/// остаточными волнами работы нет вовсе.
/// </para>
/// <para>
/// Растровых кэшей нет (1.37.1): у «Занавеса» линии и карточка шли картинками (<c>BitmapCache</c>),
/// и на дробном сдвиге и в масштабе интерфейса это давало мыло. Здесь всё векторное.
/// </para>
/// <para>
/// Кадры идут от <see cref="CompositionTarget.Rendering"/> только пока лист открыт и окно активно;
/// часы пускаются с первого кадра, на котором сцену видно. Без «Анимаций» — один неподвижный кадр
/// (<see cref="StillTime"/>). <see cref="Freeze"/> ставит любой кадр — для снимков и тестов.
/// </para>
/// </remarks>
public sealed class WhatsNewDrop : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = BrushProperty(nameof(Fill));

    public static readonly DependencyProperty RingProperty = BrushProperty(nameof(Ring));

    public static readonly DependencyProperty FeatherProperty = BrushProperty(nameof(Feather));

    public static readonly DependencyProperty SheenProperty = BrushProperty(nameof(Sheen));

    public static readonly DependencyProperty ShaftProperty = BrushProperty(nameof(Shaft));

    public static readonly DependencyProperty ShadowProperty = BrushProperty(nameof(Shadow));

    /// <summary>Неподвижный кадр без анимаций: кольца давно успокоились.</summary>
    internal const double StillTime = 40;

    /// <summary>Сколько длится уход: кольца стягиваются в точку удара, фон гаснет.</summary>
    internal const double LeaveTime = .62;

    /// <summary>
    /// Когда выходят кольца. Раньше концепта (.12 и .34): окно должно появляться сразу (1.37.1),
    /// поэтому внешнее кольцо идёт с первого кадра.
    /// </summary>
    private const double OuterAt = 0;

    private const double InnerAt = .18;

    /// <summary>Когда проявляется первое перо и шаг между перьями.</summary>
    private const double FeathersAt = .2;

    private const double FeatherStep = .07;

    /// <summary>
    /// Перья по бокам карточки: ближние крупнее и падают быстрее, дальние мельче и медленнее.
    /// Сторона, доля зоны по ширине, длина, вид и начальная высота в долях пути — как в концепте.
    /// </summary>
    private static readonly (int Side, double At, double Length, FeatherKind Kind, double Y0)[] Flock =
    [
        (-1, .3, 176, FeatherKind.Primary, .08),
        (-1, .78, 104, FeatherKind.Secondary, .55),
        (-1, .52, 70, FeatherKind.Covert, .82),
        (1, .64, 188, FeatherKind.Secondary, .4),
        (1, .24, 96, FeatherKind.Primary, .02),
        (1, .86, 74, FeatherKind.Primary, .7),
        (1, -.06, 136, FeatherKind.Primary, .9)
    ];

    private readonly VisualCollection _children;
    private readonly Stopwatch _clock = new();
    private readonly DrawingVisual _back = new();
    private readonly WaveRing[] _rings = [new(OuterAt), new(InnerAt)];
    private readonly FallingFeather[] _feathers;
    private readonly DrawingVisual _shadow = new() { Transform = new TranslateTransform() };
    private Rect? _card;
    private double? _frozen;
    private double? _leaveAt;
    private bool _hooked;

    public WhatsNewDrop()
    {
        _children = new VisualCollection(this);
        _feathers = BuildFeathers();
        _children.Add(_back);
        foreach (var ring in _rings)
        {
            _children.Add(ring.Visual);
        }

        foreach (var feather in _feathers)
        {
            _children.Add(feather.Visual);
        }

        _children.Add(_shadow);

        // Тень лежит картинкой вдвое реже точек экрана: сглаженное растяжение, а не ступени.
        RenderOptions.SetBitmapScalingMode(_shadow, BitmapScalingMode.Linear);

        // Лист убрали из окна посреди сцены — подписка на кадры не должна его пережить.
        Unloaded += (_, _) => Hook(false);
    }

    /// <summary>Каждый кадр, со временем сцены: по нему лист проявляет и уводит карточку.</summary>
    public event Action<double>? Ticked;

    /// <summary>Фон: белый в светлой теме, почти чёрный в тёмной.</summary>
    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Контур колец.</summary>
    public Brush? Ring
    {
        get => (Brush?)GetValue(RingProperty);
        set => SetValue(RingProperty, value);
    }

    /// <summary>Тело пера.</summary>
    public Brush? Feather
    {
        get => (Brush?)GetValue(FeatherProperty);
        set => SetValue(FeatherProperty, value);
    }

    /// <summary>Блик вдоль стержня.</summary>
    public Brush? Sheen
    {
        get => (Brush?)GetValue(SheenProperty);
        set => SetValue(SheenProperty, value);
    }

    /// <summary>Стержень по опахалу.</summary>
    public Brush? Shaft
    {
        get => (Brush?)GetValue(ShaftProperty);
        set => SetValue(ShaftProperty, value);
    }

    /// <summary>Тень под карточкой: цвет, а его прозрачность — насколько тень густа у середины.</summary>
    public Brush? Shadow
    {
        get => (Brush?)GetValue(ShadowProperty);
        set => SetValue(ShadowProperty, value);
    }

    /// <summary>Время сцены в секундах.</summary>
    public double Time => _frozen ?? (UiMotion.Enabled ? _clock.Elapsed.TotalSeconds : StillTime);

    /// <summary>Когда начался уход (время сцены); <c>null</c> — сцена не уходит.</summary>
    internal double? LeaveAt => _leaveAt;

    /// <summary>Подписана ли сцена на кадры окна.</summary>
    internal bool Running => _hooked;

    protected override int VisualChildrenCount => _children.Count;

    /// <summary>
    /// Начинает сцену с нуля. Часы пойдут с первого кадра, на котором сцену видно: на холодном
    /// запуске первый кадр приходит не сразу, и пущенные здесь часы показали бы его посреди появления.
    /// </summary>
    public void Start()
    {
        _frozen = null;
        _leaveAt = null;
        _clock.Reset();
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

    /// <summary>Начинает уход: кольца стягиваются в точку удара, перья гаснут.</summary>
    public void Leave()
    {
        _leaveAt = Time;
        Tick();
    }

    /// <summary>
    /// Где карточка (в точках сцены, без сдвига появления) и насколько она проявилась: по ней
    /// встают кольца и перья, под ней — тень.
    /// </summary>
    /// <param name="area">Место карточки по раскладке; <c>null</c> — оставить прежнее.</param>
    /// <param name="opacity">Насколько карточка проявилась.</param>
    /// <param name="shift">Сдвиг карточки вниз при появлении и уходе.</param>
    internal void SetCard(Rect? area, double opacity, double shift)
    {
        _shadow.Opacity = opacity;
        ((TranslateTransform)_shadow.Transform).Y = shift;
        if (area is not { } card || card == _card)
        {
            return;
        }

        _card = card;
        Place();
        DrawShadow();
        Apply(Time);
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
        DrawBack();
        Place();
        DrawShadow();
        Apply(Time);
    }

    private static DependencyProperty BrushProperty(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(WhatsNewDrop), new PropertyMetadata(null, Repaint));

    private static void Repaint(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var drop = (WhatsNewDrop)d;
        drop.DrawBack();
        foreach (var feather in drop._feathers)
        {
            feather.Draw(drop.Feather, drop.Sheen, drop.Shaft);
        }

        foreach (var ring in drop._rings)
        {
            ring.Forget();
        }

        drop.DrawShadow();
        drop.Apply(drop.Time);
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
        // Лист спрятан, не закрыт (окно в трее или ещё не показано): кадры не нужны, часы стоят.
        if (!IsVisible)
        {
            return;
        }

        if (!_clock.IsRunning)
        {
            _clock.Start();
        }

        // Человек ушёл в другую программу или свернул окно — сцена замирает и ничего не стоит;
        // время идёт дальше, и по возвращении перья просто оказываются чуть дальше. Уход не ждёт.
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

    // ---- Построение ----

    /// <summary>
    /// Перья и их движение — из случая концепта (зерно 31) в его порядке обращений: перо, его
    /// маятник, следующее перо. От размера окна не зависят, поэтому строятся один раз.
    /// </summary>
    private static FallingFeather[] BuildFeathers()
    {
        var random = new SceneRandom(31);
        var feathers = new FallingFeather[Flock.Length];
        for (var i = 0; i < Flock.Length; i++)
        {
            var (_, _, length, kind, y0) = Flock[i];
            var spec = Spec(random, kind, length);
            var period = 3.4 + length * .012;
            var phase = random.Next() * 6.28;
            var tilt = (random.Next() < .5 ? -1 : 1) * (14 + random.Next() * 18);
            var swing = 16 + random.Next() * 10;
            feathers[i] = new FallingFeather(WhatsNewFeather.Build(spec), length)
            {
                Y0 = y0,
                Speed = 14 + length * .15,
                Amplitude = 10 + length * .12,
                Omega = 2 * Math.PI / period,
                Phase = phase,
                Tilt = tilt,
                Swing = swing,
                Appear = FeathersAt + i * FeatherStep
            };
        }

        return feathers;
    }

    /// <summary>Случайные расщепы: чаще по широкому внутреннему опахалу, не ближе друг к другу, чем нужно.</summary>
    private static FeatherSpec Spec(SceneRandom random, FeatherKind kind, double length)
    {
        var count = kind == FeatherKind.Covert ? 2 : random.Next() < .55 ? 2 : 3;
        var splits = new List<FeatherSplit>(count);
        for (var i = 0; i < count; i++)
        {
            var side = random.Next() < .66 ? 1 : -1;
            var u = .24 + (i + .5) / count * .44 + (random.Next() - .5) * .06;
            var depth = .45 + random.Next() * .3;
            var gap = .01 + random.Next() * .006;
            splits.Add(new FeatherSplit(side, u, depth, gap));
        }

        var flip = random.Next() < .5;
        var seed = 1 + (uint)Math.Floor(random.Next() * 1e6);
        return new FeatherSpec(kind, length, flip, splits, seed);
    }

    /// <summary>Карточка по раскладке, а пока её нет — та, что встанет: не шире 680 и не выше 600.</summary>
    private Rect Card()
    {
        if (_card is { } card)
        {
            return card;
        }

        var width = Math.Max(320, Math.Min(680, RenderSize.Width - 48));
        var height = Math.Max(260, Math.Min(600, RenderSize.Height - 150));
        return new Rect((RenderSize.Width - width) / 2, (RenderSize.Height - height) / 2, width, height);
    }

    /// <summary>
    /// Кольца и зоны перьев по месту карточки. Первая волна уходит далеко и обнимает карточку по
    /// бокам, вторая остаётся у места удара: полукругом над карточкой, не касаясь её.
    /// </summary>
    private void Place()
    {
        var size = RenderSize;
        var card = Card();
        _rings[0].Radius = Math.Max(card.Width / 2 + 116, size.Height * .6);
        _rings[1].Radius = Math.Max(36, Math.Min(card.Y - 22, 104));
        foreach (var ring in _rings)
        {
            ring.Centre = new Point(size.Width / 2, 0);
            ring.Forget();
        }

        for (var i = 0; i < _feathers.Length; i++)
        {
            var (side, at, _, _, _) = Flock[i];
            var (left, width) = side < 0 ? (0d, card.X) : (card.Right, size.Width - card.Right);
            _feathers[i].X0 = left + width * at;
        }
    }

    private void DrawBack()
    {
        using var dc = _back.RenderOpen();
        dc.DrawRectangle(Fill, null, new Rect(RenderSize));
    }

    /// <summary>
    /// Тень карточки — та же, что у концепта (<c>0 30px 70px −32px</c>): прямоугольник карточки,
    /// ужатый на 32 точки и опущенный на 30, размыт по Гауссу с σ = 35. Цвет и густоту у середины
    /// даёт кисть <see cref="Shadow"/>.
    /// </summary>
    /// <remarks>
    /// Размытие прямоугольника по Гауссу разделимо — f(x)·f(y), где f — разность двух erf, — поэтому
    /// тень считается картинкой один раз на размер карточки, а кадр её только накладывает. До этого
    /// она была стопкой скруглений: плотная полоса прямо под карточкой с жёстким краем вместо
    /// широкой бледной дымки концепта (1.37.1). Скругление углов не нужно: ужатый на 32 точки
    /// прямоугольник у концепта и так без скругления.
    /// </remarks>
    private void DrawShadow()
    {
        const double sigma = ShadowBlur / 2;

        using var dc = _shadow.RenderOpen();
        if (Shadow is not SolidColorBrush { Color: var color } || color.A == 0 || RenderSize.Width <= 0)
        {
            return;
        }

        var core = Card();
        core.Offset(0, ShadowOffset);
        core.Inflate(ShadowSpread, ShadowSpread);
        if (core.IsEmpty || core.Width <= 0 || core.Height <= 0)
        {
            return;
        }

        var area = core;
        area.Inflate(3 * sigma, 3 * sigma);
        dc.DrawImage(ShadowImage(core, area, sigma, color), area);
    }

    /// <summary>Размытие тени, как у CSS: радиус размытия вдвое больше σ.</summary>
    private const double ShadowBlur = 70;

    private const double ShadowSpread = -32;

    private const double ShadowOffset = 30;

    /// <summary>
    /// Точек картинки на точку экрана: тень — плавный спад без деталей, вдвое реже ей хватает с
    /// запасом, а считается и весит она вчетверо меньше.
    /// </summary>
    private const double ShadowDensity = .5;

    private static BitmapSource ShadowImage(Rect core, Rect area, double sigma, Color color)
    {
        var width = Math.Max(1, (int)Math.Ceiling(area.Width * ShadowDensity));
        var height = Math.Max(1, (int)Math.Ceiling(area.Height * ShadowDensity));
        var across = Profile(width, area.X, core.Left, core.Right, sigma);
        var down = Profile(height, area.Y, core.Top, core.Bottom, sigma);
        var strength = color.A / 255.0;

        // Pbgra32 — цвет, умноженный на прозрачность.
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var alpha = strength * across[x] * down[y];
                var i = (y * width + x) * 4;
                pixels[i] = (byte)Math.Round(color.B * alpha);
                pixels[i + 1] = (byte)Math.Round(color.G * alpha);
                pixels[i + 2] = (byte)Math.Round(color.R * alpha);
                pixels[i + 3] = (byte)Math.Round(255 * alpha);
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>Отрезок [from, to], размытый по Гауссу: доля тени в каждой точке картинки вдоль оси.</summary>
    private static double[] Profile(int count, double origin, double from, double to, double sigma)
    {
        var scale = 1 / (sigma * Math.Sqrt(2));
        var values = new double[count];
        for (var i = 0; i < count; i++)
        {
            var at = origin + (i + .5) / ShadowDensity;
            values[i] = .5 * (Erf((at - from) * scale) - Erf((at - to) * scale));
        }

        return values;
    }

    /// <summary>Функция ошибок (Абрамовиц и Стиган 7.1.26, погрешность меньше 1,5·10⁻⁷): в Math её нет.</summary>
    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + .3275911 * x);
        var poly = t * (.254829592 + t * (-.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429))));
        return sign * (1 - poly * Math.Exp(-x * x));
    }

    // ---- Кадр ----

    private void Apply(double t)
    {
        var leave = _leaveAt is { } at && t >= at ? t - at : (double?)null;
        foreach (var ring in _rings)
        {
            ring.Apply(t, leave, Ring);
        }

        var height = RenderSize.Height;
        foreach (var feather in _feathers)
        {
            feather.Apply(t, leave, height);
        }
    }

    private static double Clamp(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    private static double Lerp(double a, double b, double p) => a + (b - a) * p;

    private static double Progress(double t, double start, double duration) => Clamp((t - start) / duration);

    private static double EaseOut(double p) => 1 - Math.Pow(1 - p, 3);

    private static double EaseInOut(double p) => p < .5 ? 4 * p * p * p : 1 - Math.Pow(-2 * p + 2, 3) / 2;

    /// <summary>Затухающая пружина 0 → 1 с лёгким перелётом: так расходится и успокаивается кольцо на воде.</summary>
    private static double Spring(double p, double zeta = .72, double omega = 4.6)
    {
        if (p <= 0)
        {
            return 0;
        }

        var s = Math.Sqrt(1 - zeta * zeta);
        var damped = omega * s;
        return 1 - Math.Exp(-zeta * omega * p) * (Math.Cos(damped * p) + zeta / s * Math.Sin(damped * p));
    }

    /// <summary>Кольцо волны: только контур, заливки нет.</summary>
    private sealed class WaveRing(double start)
    {
        private double _radius = -1;
        private double _width = -1;
        private double _opacity = -1;

        public DrawingVisual Visual { get; } = new();

        /// <summary>Где кольцо остановится.</summary>
        public double Radius { get; set; }

        public Point Centre { get; set; }

        /// <summary>Нарисованное больше не годится: сменились цвет, место или размер.</summary>
        public void Forget() => _radius = -1;

        public void Apply(double t, double? leave, Brush? brush)
        {
            var p = t - start;
            if (p < 0)
            {
                Draw(0, 0, 0, brush);
                return;
            }

            var x = Spring(p);
            var r = Radius * x;

            // Остаточная волна: каждые 6 с кольцо чуть вздрагивает, внешнее раньше внутреннего.
            var since = t - (3.2 + start);
            if (since >= 0 && since % 6 < 1.2)
            {
                var k = since % 6;
                r += 4 * Math.Sin(Math.PI * k / 1.2) * (1 - k / 1.2);
            }

            var width = Lerp(3.4, 1.7, Clamp(x));
            var opacity = Clamp(p / .08);
            if (leave is { } l)
            {
                // Уход: кольца стягиваются обратно в точку удара.
                r *= 1 - EaseInOut(Progress(l, 0, .5));
                opacity *= 1 - Progress(l, .38, .14);
            }

            Draw(r, width, opacity, brush);
        }

        /// <summary>
        /// Перерисовывает, только если кольцо изменилось заметно глазу: в покое между волнами
        /// радиус стоит, и раскладывать штрих заново незачем. Прозрачность — внутри рисунка, а не
        /// у визуала: та шла бы через промежуточный слой.
        /// </summary>
        private void Draw(double radius, double width, double opacity, Brush? brush)
        {
            if (opacity <= .001 || radius <= .5)
            {
                radius = width = opacity = 0;
            }

            if (_radius >= 0 && Math.Abs(radius - _radius) < .01 && Math.Abs(width - _width) < .005 &&
                Math.Abs(opacity - _opacity) < .002)
            {
                return;
            }

            _radius = radius;
            _width = width;
            _opacity = opacity;
            using var dc = Visual.RenderOpen();
            if (opacity <= 0)
            {
                return;
            }

            var pen = new Pen(brush, width);
            pen.Freeze();
            dc.PushOpacity(opacity);
            dc.DrawEllipse(null, pen, Centre, radius, radius);
            dc.Pop();
        }
    }

    /// <summary>
    /// Перо, падающее маятником: качается вбок, наклоняясь по ходу качания, медленнее на краях
    /// размаха и быстрее посередине; уходит за низ и возвращается сверху.
    /// </summary>
    private sealed class FallingFeather
    {
        private readonly FeatherShape _shape;
        private readonly double _length;
        private readonly MatrixTransform _place = new();

        public FallingFeather(FeatherShape shape, double length)
        {
            _shape = shape;
            _length = length;
            Visual = new DrawingVisual { Transform = _place, Opacity = 0 };
        }

        public DrawingVisual Visual { get; }

        public double X0 { get; set; }

        public double Y0 { get; init; }

        public double Speed { get; init; }

        public double Amplitude { get; init; }

        public double Omega { get; init; }

        public double Phase { get; init; }

        public double Tilt { get; init; }

        public double Swing { get; init; }

        public double Appear { get; init; }

        /// <summary>Перо рисуется один раз; поворачивается оно вокруг своей середины.</summary>
        public void Draw(Brush? body, Brush? sheen, Brush? shaft)
        {
            using var dc = Visual.RenderOpen();
            dc.PushTransform(new TranslateTransform(0, -_length / 2));
            dc.DrawGeometry(body, null, _shape.Body);
            dc.DrawGeometry(sheen, null, _shape.Sheen);
            dc.DrawGeometry(shaft, null, _shape.Rachis);
            dc.Pop();
        }

        public void Apply(double t, double? leave, double height)
        {
            var phase = Omega * t + Phase;
            var span = height + _length * 1.2 + 80;
            var travel = Speed * t * 1.3 + Speed * .3 / (2 * Omega) * Math.Sin(2 * phase);
            var y = ((Y0 * span + travel) % span + span) % span - _length * .6 - 40;
            var x = X0 + Amplitude * Math.Sin(phase);
            var rotation = Tilt - Swing * Math.Cos(phase);

            var matrix = Matrix.Identity;
            matrix.Rotate(rotation);
            matrix.Translate(x, y);
            _place.Matrix = matrix;

            var opacity = EaseOut(Progress(t, Appear, .6));
            if (leave is { } l)
            {
                opacity *= 1 - Progress(l, 0, .35);
            }

            Visual.Opacity = opacity;
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
