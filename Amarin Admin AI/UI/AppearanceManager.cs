using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Рисует фон окна и кладёт производную «стеклянную» палитру в <see cref="ThemeManager.OverrideSlot"/>.
/// <para>
/// Интерфейс читает цвета через <c>DynamicResource</c>, а словари разбираются от последнего к
/// первому, поэтому копии кистей <c>Bg.*</c> с прозрачностью в слоте 3 делают полупрозрачными
/// сразу все панели, не трогая разметку окна.
/// </para>
/// </summary>
internal sealed class AppearanceManager : IDisposable
{
    /// <summary>
    /// Какую долю заданной прозрачности берёт каждая поверхность; 0 — остаётся непрозрачной.
    /// <para>
    /// Двух ключей нет намеренно. <c>Bg.Window</c> заливает только корневой Grid, который лежит
    /// <em>под</em> фоном, — истончать его бесполезно, а у окна рамка DWM, и прозрачный корень
    /// мог бы просвечивать рабочим столом по краю. <c>Bg.Panel</c> — фон попапов с
    /// <c>AllowsTransparency</c> (меню вложений, выбор модели, выпадашки, контекстные меню) и тел
    /// модальных окон; это отдельные окна, и сквозь прозрачный фон в них был бы виден
    /// <em>рабочий стол</em>, а не фон программы. Поле ввода берёт <c>Bg.Glass</c> — ради этого
    /// он и заведён.
    /// </para>
    /// </summary>
    private static readonly (string Key, double Factor)[] GlassSurfaces =
    [
        ("Bg.Glass", 1.00),
        ("Bg.Sidebar", 0.85),
        ("Bg.Card", 0.70),
        ("Bg.Raised", 0.55),
        ("Bg.Hover", 0.45),
        ("Bg.Selected", 0.40),
        ("Bg.Track", 0.40),
        ("Bg.Elevated", 0.35)
    ];

    /// <summary>
    /// Приглушённые серые подбирались под непрозрачную палитру; над фото их контраст падает ниже
    /// 3:1, поэтому при фоне каждая ступень поднимается на одну ярче.
    /// </summary>
    private static readonly (string Key, string Source)[] TextPromotions =
    [
        ("Text.Faint", "Text.Muted"),
        ("Text.Muted", "Text.Tertiary"),
        ("Text.Dim", "Text.Secondary")
    ];

    /// <summary>Сколько проявляется опоздавшая картинка фона.</summary>
    private const double PhotoFadeMs = 220;

    private readonly Window _window;
    private readonly Panel _host;
    private readonly Rectangle _under = new();
    private readonly Rectangle _base = new();

    /// <summary>
    /// Картинка, не успевшая к отрисовке, — проявляется поверх градиента на <see cref="_base"/>.
    /// </summary>
    private readonly Rectangle _photo = new() { Opacity = 0, Visibility = Visibility.Collapsed };
    private readonly Rectangle _brightness = new();
    private readonly Rectangle _frost = new();
    private readonly Rectangle _sheen = new();
    private readonly Rectangle _vignette = new();

    private AppearanceSettings _settings = new();
    private Storyboard? _motion;
    private int _imageGeneration;
    private bool _disposed;
    private string? _appliedKey;

    public AppearanceManager(Window window, Panel host)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _host = host ?? throw new ArgumentNullException(nameof(host));

        // Порядок — это порядок слоёв: _photo проявляется над градиентом, но под затемнением,
        // морозом, бликом и виньеткой — иначе те гасли бы вместе с ним.
        foreach (var layer in new[] { _under, _base, _photo, _brightness, _frost, _sheen, _vignette })
        {
            layer.IsHitTestVisible = false;
            _host.Children.Add(layer);
        }

        ThemeManager.EffectiveThemeChanged += OnThemeChanged;
        _window.StateChanged += OnPowerRelevantChange;
        _window.IsVisibleChanged += OnVisibilityChanged;
    }

    /// <summary>Рисуется ли фон на самом деле — с учётом общего выключателя и проверки картинки.</summary>
    public bool BackdropActive { get; private set; }

    /// <summary>
    /// Папка профиля, куда скопирована картинка фона. В настройках — голое имя файла, чтобы
    /// картинка переезжала с профилем; разрешается оно относительно этой папки.
    /// </summary>
    public string DataRoot { get; set; } = AppPaths.Root;

    /// <summary>
    /// Альфа поверхности при заданной непрозрачности стекла. Главный инвариант:
    /// <paramref name="glassOpacity"/> = 1 воспроизводит палитру точно, и «включено, но
    /// нейтрально» выглядит как заводская тема.
    /// </summary>
    public static double GlassAlpha(string key, double glassOpacity)
    {
        foreach (var (surface, factor) in GlassSurfaces)
        {
            if (surface == key)
            {
                return Math.Clamp(1.0 - ((1.0 - glassOpacity) * factor), 0.0, 1.0);
            }
        }

        return 1.0;
    }

    /// <summary>Поверхности, которые становятся стеклом, — для тестов и подписей в интерфейсе.</summary>
    public static IEnumerable<string> GlassKeys => GlassSurfaces.Select(s => s.Key);

    public void Apply(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        // Открытие настроек прогоняло этот метод целиком — с перекраской фона и перезапуском
        // сториборда, — хотя оформление уже применено и ничего не менялось. Ключ снимается с
        // самих настроек, поэтому поля, которые добавят потом, попадут в него сами; палитра в
        // ключе потому, что от неё зависят выводимые цвета стекла.
        var key = BuildKey();
        if (key == _appliedKey)
        {
            return;
        }

        _appliedKey = key;
        using var timer = PerfLog.Measure("appearance_apply");

        var mode = settings.Enabled ? settings.BackdropMode : BackdropMode.None;
        if (mode == BackdropMode.Image && string.IsNullOrWhiteSpace(settings.BackgroundImagePath))
        {
            mode = BackdropMode.Gradient;
        }

        BackdropActive = mode != BackdropMode.None;
        _host.Visibility = BackdropActive ? Visibility.Visible : Visibility.Collapsed;

        StopMotion();
        if (BackdropActive)
        {
            PaintBackdrop(mode);
        }

        WriteOverrides();

        if (BackdropActive && MotionAllowed())
        {
            StartMotion(mode);
        }
    }

    /// <summary>
    /// Отпечаток того, что сейчас нарисовано. Палитра входит в него, потому что цвета стекла
    /// выводятся из неё, а не только из настроек оформления.
    /// </summary>
    private string BuildKey() =>
        ThemeManager.Current.PaletteName + "|" + JsonSerializer.Serialize(_settings, AppJson.Options);

    // ───────────────────────── фон ─────────────────────────

    private void PaintBackdrop(BackdropMode mode)
    {
        var light = ThemeManager.IsLight;

        // Непрозрачная подложка: при «вписать» и «плиткой» картинка может не закрыть окно, и
        // просвет смешался бы с рамкой DWM.
        _under.Fill = Resource("Bg.Window") is SolidColorBrush window
            ? new SolidColorBrush(window.Color)
            : Brushes.Black;

        if (mode == BackdropMode.Image)
        {
            _base.Fill = BuildImageBrush();
        }
        else
        {
            // Слой проявления снимаем и здесь: ушли с картинки на градиент — доехавшее фото
            // не должно остаться висеть поверх него.
            _imageGeneration++;
            HidePhotoLayer();
            _base.Fill = BuildGradientBrush();
        }

        // Яркость — накладкой, а не в пикселях: ползунок работает сразу и ничего не стоит.
        var brightness = _settings.ImageBrightness;
        if (mode != BackdropMode.Image)
        {
            brightness = 1.0;
        }

        if (brightness < 1.0)
        {
            _brightness.Fill = new SolidColorBrush(Color.FromArgb((byte)((1.0 - brightness) * 255), 0, 0, 0));
            _brightness.Visibility = Visibility.Visible;
        }
        else if (brightness > 1.0)
        {
            _brightness.Fill = new SolidColorBrush(
                Color.FromArgb((byte)(Math.Min(brightness - 1.0, 0.6) * 255), 255, 255, 255));
            _brightness.Visibility = Visibility.Visible;
        }
        else
        {
            _brightness.Visibility = Visibility.Collapsed;
        }

        if (_settings.GlassFrost > 0 && Resource("Bg.Window") is SolidColorBrush tint)
        {
            var c = tint.Color;
            _frost.Fill = new SolidColorBrush(
                Color.FromArgb((byte)(_settings.GlassFrost * 255), c.R, c.G, c.B));
            _frost.Visibility = Visibility.Visible;
        }
        else
        {
            _frost.Visibility = Visibility.Collapsed;
        }

        // Белый отблеск на светлой палитре не виден — там он тёмный.
        if (_settings.GlassSheen)
        {
            var ink = light ? Colors.Black : Colors.White;
            var sheen = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 0.0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)18 : (byte)26, ink.R, ink.G, ink.B), 0.42));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0, ink.R, ink.G, ink.B), 0.72));
            sheen.Freeze();
            _sheen.Fill = sheen;
            _sheen.Visibility = Visibility.Visible;
        }
        else
        {
            _sheen.Visibility = Visibility.Collapsed;
        }

        if (_settings.Vignette)
        {
            var vignette = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.5, 0.5),
                Center = new Point(0.5, 0.5),
                RadiusX = 0.78,
                RadiusY = 0.78
            };
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.55));
            vignette.GradientStops.Add(new GradientStop(Color.FromArgb(110, 0, 0, 0), 1.0));
            vignette.Freeze();
            _vignette.Fill = vignette;
            _vignette.Visibility = Visibility.Visible;
        }
        else
        {
            _vignette.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Точки градиента идут палиндромом (c0…cn…c0): «Сияние» сдвигает кисть вдоль оси, и у
    /// несимметричного списка на стыке концов был бы виден шов.
    /// </summary>
    private Brush BuildGradientBrush()
    {
        var colors = _settings.GradientColors
            .Select(Parse)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();

        if (colors.Count == 0)
        {
            colors = [Color.FromRgb(0x1B, 0x27, 0x35), Color.FromRgb(0x0F, 0x20, 0x27)];
        }

        if (colors.Count == 1)
        {
            colors.Add(colors[0]);
        }

        var mirrored = _settings.GradientMotion == BackdropMotion.Aurora && colors.Count > 2;
        var sequence = mirrored
            ? colors.Concat(Enumerable.Reverse(colors).Skip(1)).ToList()
            : colors;

        var brush = new LinearGradientBrush();
        for (var i = 0; i < sequence.Count; i++)
        {
            brush.GradientStops.Add(new GradientStop(sequence[i], (double)i / (sequence.Count - 1)));
        }

        var (start, end) = AxisFor(_settings.GradientAngle);
        brush.StartPoint = start;
        brush.EndPoint = end;

        // Не замораживается намеренно: «Дрейф» и «Сияние» анимируют StartPoint/EndPoint.
        return brush;
    }

    /// <summary>
    /// Концы оси под углом — на окружности вокруг центра единичного квадрата.
    /// <para>
    /// «Дрейф» двигает именно их. Поворот кисти через <c>RelativeTransform</c> ошибался бы дважды:
    /// на неквадратном окне единичный квадрат растянут, и ось шла бы с неровной скоростью, а
    /// повёрнутые концы выходят за [0,1], и заливка Pad рисует растущие полосы крайних цветов.
    /// </para>
    /// </summary>
    private static (Point Start, Point End) AxisFor(double degrees)
    {
        const double radius = 0.75;
        var radians = degrees * Math.PI / 180.0;
        var dx = Math.Cos(radians) * radius;
        var dy = Math.Sin(radians) * radius;
        return (new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
    }

    /// <summary>
    /// Кисть фона-картинки. Пока картинки нет, отдаёт градиент и досылает её через
    /// <see cref="_photo"/>.
    /// </summary>
    /// <remarks>
    /// Готовая картинка ставится прямо на <see cref="_base"/> — слой проявления при этом гасим,
    /// иначе поверх нового фона осталась бы прошлая картинка. Опоздавшая же приезжает на
    /// <see cref="_photo"/> и проявляется поверх градиента: раньше она подменялась рывком,
    /// и на запуске это читалось как «сначала загрузилась затычка, потом фото».
    /// </remarks>
    private Brush BuildImageBrush()
    {
        var path = ImagePath();
        var ready = AppearanceImageCache.Peek(path, _settings.ImageSaturation, _settings.ImageBlur);
        _imageGeneration++;
        HidePhotoLayer();

        if (ready is not null)
        {
            return ImageBrushFor(ready);
        }

        // Картинка ещё не готова: пока — градиент, а готовая встанет, когда фон её досчитает.
        // Выбор обоев 4K не останавливает поток интерфейса.
        var generation = _imageGeneration;
        var saturation = _settings.ImageSaturation;
        var blur = _settings.ImageBlur;
        _ = AppearanceImageCache.LoadAsync(path, saturation, blur, DataRoot).ContinueWith(
            task =>
            {
                if (_disposed || generation != _imageGeneration || task.Result is null)
                {
                    return;
                }

                FadeInPhoto(ImageBrushFor(task.Result));
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.FromCurrentSynchronizationContext());

        return BuildGradientBrush();
    }

    private void HidePhotoLayer()
    {
        _photo.BeginAnimation(UIElement.OpacityProperty, null);
        _photo.Opacity = 0;
        _photo.Fill = null;
        _photo.Visibility = Visibility.Collapsed;
    }

    private void FadeInPhoto(Brush brush)
    {
        _photo.Fill = brush;
        _photo.Visibility = Visibility.Visible;
        _photo.Opacity = 0;
        _photo.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(PhotoFadeMs))
            {
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private Brush ImageBrushFor(System.Windows.Media.Imaging.BitmapSource image)
    {
        var brush = new ImageBrush(image)
        {
            Stretch = _settings.ImageFit switch
            {
                BackdropFit.Fit => Stretch.Uniform,
                BackdropFit.Tile => Stretch.None,
                _ => Stretch.UniformToFill
            }
        };

        if (_settings.ImageFit == BackdropFit.Tile)
        {
            brush.TileMode = TileMode.Tile;
            brush.ViewportUnits = BrushMappingMode.Absolute;
            brush.Viewport = new Rect(0, 0, image.Width, image.Height);
        }

        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Полный путь к картинке. Разрешает его <see cref="AppearanceImageCache.ResolvePath"/>:
    /// тем же путём строится ключ кэша, и второй копией правила они бы молча разошлись.
    /// </summary>
    private string ImagePath() =>
        AppearanceImageCache.ResolvePath(_settings.BackgroundImagePath, DataRoot);

    // ───────────────────────── движение ─────────────────────────

    /// <summary>
    /// Бесконечная анимация не даёт потоку отрисовки WPF уснуть, а окно чата открыто весь день —
    /// это заметный расход батареи. Поэтому движение не включается там, где хорошо выглядеть
    /// всё равно не будет, и стоит, пока окна не видно.
    /// </summary>
    private bool MotionAllowed()
    {
        if (!_settings.AnimationsEnabled || _settings.GradientMotion == BackdropMotion.None)
        {
            return false;
        }

        if (_window.WindowState == WindowState.Minimized || !_window.IsVisible)
        {
            return false;
        }

        if (SystemParameters.IsRemoteSession || !SystemParameters.ClientAreaAnimation)
        {
            return false;
        }

        // Программная отрисовка: каждый кадр анимации — перерисовка всего окна процессором.
        return (RenderCapability.Tier >> 16) >= 1;
    }

    private void StartMotion(BackdropMode mode)
    {
        var speed = Math.Clamp(_settings.MotionSpeed, 0.25, 3.0);
        var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };

        switch (_settings.GradientMotion)
        {
            case BackdropMotion.Pulse:
                AddPulse(storyboard, speed);
                break;

            case BackdropMotion.Drift when mode == BackdropMode.Gradient:
                AddDrift(storyboard, speed);
                break;

            case BackdropMotion.Aurora when mode == BackdropMode.Gradient:
                AddAurora(storyboard, speed);
                break;

            // У фото нет оси градиента — вместо поворота медленный наезд камеры.
            case BackdropMotion.Drift:
            case BackdropMotion.Aurora:
                AddKenBurns(storyboard, speed);
                break;
        }

        if (storyboard.Children.Count == 0)
        {
            return;
        }

        _motion = storyboard;
        storyboard.Begin(_window, isControllable: true);
    }

    private void AddPulse(Storyboard storyboard, double speed)
    {
        _brightness.Visibility = Visibility.Visible;
        _brightness.Fill ??= new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

        var animation = new DoubleAnimation
        {
            From = 0.55,
            To = 1.0,
            Duration = TimeSpan.FromSeconds(8 / speed),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(animation, _brightness);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        storyboard.Children.Add(animation);
    }

    private void AddDrift(Storyboard storyboard, double speed)
    {
        if (_base.Fill is not LinearGradientBrush)
        {
            return;
        }

        var duration = TimeSpan.FromSeconds(40 / speed);
        var start = new PointAnimationUsingKeyFrames { Duration = duration };
        var end = new PointAnimationUsingKeyFrames { Duration = duration };

        // Шестнадцать точек по кругу: линейный переход между ними не отличить от настоящего
        // вращения, а стоят они ничего.
        const int steps = 16;
        for (var i = 0; i <= steps; i++)
        {
            var time = KeyTime.FromPercent((double)i / steps);
            var (from, to) = AxisFor(_settings.GradientAngle + (360.0 * i / steps));
            start.KeyFrames.Add(new LinearPointKeyFrame(from, time));
            end.KeyFrames.Add(new LinearPointKeyFrame(to, time));
        }

        BindGradientEnds(storyboard, start, end);
    }

    private void AddAurora(Storyboard storyboard, double speed)
    {
        if (_base.Fill is not LinearGradientBrush brush)
        {
            return;
        }

        var duration = TimeSpan.FromSeconds(24 / speed);
        var axis = brush.EndPoint - brush.StartPoint;
        var shift = new Vector(axis.X * 0.35, axis.Y * 0.35);

        var start = new PointAnimation
        {
            From = brush.StartPoint - shift,
            To = brush.StartPoint + shift,
            Duration = duration,
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var end = new PointAnimation
        {
            From = brush.EndPoint - shift,
            To = brush.EndPoint + shift,
            Duration = duration,
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        BindGradientEnds(storyboard, start, end);
    }

    /// <summary>
    /// Сажает две анимации на концы градиента фонового прямоугольника.
    /// </summary>
    /// <remarks>
    /// Общая для Drift и Aurora: обе двигают одни и те же две точки, и разница между ними —
    /// только в самих анимациях. Путь к свойству строкой, потому что кисть живёт под Fill,
    /// а не отдельным свойством элемента.
    /// </remarks>
    private void BindGradientEnds(Storyboard storyboard, Timeline start, Timeline end)
    {
        Storyboard.SetTarget(start, _base);
        Storyboard.SetTargetProperty(
            start,
            new PropertyPath("(Shape.Fill).(LinearGradientBrush.StartPoint)"));
        Storyboard.SetTarget(end, _base);
        Storyboard.SetTargetProperty(
            end,
            new PropertyPath("(Shape.Fill).(LinearGradientBrush.EndPoint)"));

        storyboard.Children.Add(start);
        storyboard.Children.Add(end);
    }

    private void AddKenBurns(Storyboard storyboard, double speed)
    {
        var scale = new ScaleTransform(1.0, 1.0);
        _base.RenderTransformOrigin = new Point(0.5, 0.5);
        _base.RenderTransform = scale;

        // RenderTransform не трогает раскладку, а картинка и так размыта — увеличение не видно.
        foreach (var property in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var animation = new DoubleAnimation
            {
                From = 1.0,
                To = 1.08,
                Duration = TimeSpan.FromSeconds(45 / speed),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, _base);
            Storyboard.SetTargetProperty(
                animation,
                new PropertyPath($"(UIElement.RenderTransform).({property.OwnerType.Name}.{property.Name})"));
            storyboard.Children.Add(animation);
        }
    }

    private void StopMotion()
    {
        if (_motion is null)
        {
            return;
        }

        _motion.Stop(_window);
        _motion.Remove(_window);
        _motion = null;
        _base.RenderTransform = Transform.Identity;
        _base.BeginAnimation(UIElement.OpacityProperty, null);
        _brightness.BeginAnimation(UIElement.OpacityProperty, null);
        _brightness.Opacity = 1;
    }

    // ───────────────────────── палитра ─────────────────────────

    /// <summary>
    /// Пересобирает слот 3. Зовётся напрямую, а не только из
    /// <see cref="ThemeManager.EffectiveThemeChanged"/>: при неизменной палитре
    /// <see cref="ThemeManager.Apply"/> выходит сразу и события не поднимает — ровно так и на запуске.
    /// </summary>
    private void WriteOverrides()
    {
        var overrides = new ResourceDictionary();

        if (Parse(_settings.Enabled ? _settings.AccentColor : "") is { } accent)
        {
            overrides["Accent.Fill"] = Frozen(accent);
            overrides["Accent.FillHover"] = Frozen(Shift(accent, 0.12));
            overrides["Accent.FillPressed"] = Frozen(Shift(accent, -0.12));
            overrides["Text.OnAccent"] = Frozen(Luminance(accent) > 0.55 ? Colors.Black : Colors.White);
        }

        if (BackdropActive)
        {
            foreach (var (key, _) in GlassSurfaces)
            {
                // Bg.Glass в каждой палитре равен Bg.Panel; разведены они лишь затем, чтобы поле
                // ввода становилось стеклом без попапов и диалогов.
                var sourceKey = key == "Bg.Glass" ? "Bg.Panel" : key;
                if (Resource(sourceKey) is not SolidColorBrush brush)
                {
                    continue;
                }

                var alpha = GlassAlpha(key, _settings.GlassOpacity);
                var c = brush.Color;
                overrides[key] = Frozen(Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B));
            }

            foreach (var (key, source) in TextPromotions)
            {
                if (Resource(source) is SolidColorBrush promoted)
                {
                    overrides[key] = Frozen(promoted.Color);
                }
            }

            // Затемнение под модальным окном подбиралось под непрозрачное окно; над ярким фото
            // заводское значение диалог от фона уже не отделяет.
            if (Resource("Overlay.Scrim") is SolidColorBrush scrim)
            {
                var c = scrim.Color;
                overrides["Overlay.Scrim"] = Frozen(Color.FromArgb(Math.Max(c.A, (byte)0xB8), c.R, c.G, c.B));
            }

            // Заливки становятся прозрачными, и край держат рамки.
            if (Resource("Border.Default") is SolidColorBrush border)
            {
                overrides["Border.Subtle"] = Frozen(border.Color);
            }
        }

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null)
        {
            return;
        }

        while (dictionaries.Count <= ThemeManager.OverrideSlot)
        {
            dictionaries.Add(new ResourceDictionary());
        }

        // Подмена словаря приложения инвалидирует каждый DynamicResource во всём дереве. При
        // выключенном оформлении надстройки пусты всегда, и этот обход шёл на каждую смену темы
        // впустую; сверить пару десятков кистей несопоставимо дешевле.
        if (SameColours(dictionaries[ThemeManager.OverrideSlot], overrides))
        {
            return;
        }

        dictionaries[ThemeManager.OverrideSlot] = overrides;
    }

    /// <summary>Одинаковы ли два набора надстроек — по ключам и по цвету кистей.</summary>
    private static bool SameColours(ResourceDictionary current, ResourceDictionary next)
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        foreach (var key in next.Keys)
        {
            if (next[key] is not SolidColorBrush fresh ||
                current[key] is not SolidColorBrush existing ||
                existing.Color != fresh.Color)
            {
                return false;
            }
        }

        return true;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Читает базовую палитру, а не слот надстроек: стекло из уже истончённого цвета копило бы
    /// прозрачность при каждом применении.
    /// </summary>
    private static object? Resource(string key)
    {
        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null || dictionaries.Count == 0)
        {
            return null;
        }

        return dictionaries[0][key];
    }

    public static Color? Parse(string? hex)
    {
        var normalized = AppearanceSettings.NormalizeHex(hex);
        if (normalized.Length != 7)
        {
            return null;
        }

        return Color.FromRgb(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    /// <summary>Сдвигает цвет к белому (больше нуля) или к чёрному (меньше нуля).</summary>
    public static Color Shift(Color color, double amount)
    {
        static byte Mix(byte value, double amount) => amount >= 0
            ? (byte)Math.Round(value + ((255 - value) * amount))
            : (byte)Math.Round(value * (1 + amount));

        return Color.FromRgb(Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }

    /// <summary>Относительная яркость по Rec. 709, 0..1 — по ней выбирается чёрный или белый текст на акценте.</summary>
    public static double Luminance(Color color) =>
        ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    private void OnThemeChanged()
    {
        // Синхронно: в слоте 3 лежат цвета от только что сменённой палитры, и как последний он
        // всё ещё побеждает. Отложи пересборку — была бы видна вспышка.
        if (BackdropActive)
        {
            PaintBackdrop(_settings.BackdropMode);
        }

        WriteOverrides();

        // Нарисовано теперь по новой палитре — отпечаток обязан это отражать, иначе следующее
        // открытие настроек перекрасит всё заново на ровном месте.
        _appliedKey = BuildKey();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        OnPowerRelevantChange(sender, EventArgs.Empty);

    private void OnPowerRelevantChange(object? sender, EventArgs e)
    {
        if (_motion is null)
        {
            if (BackdropActive && MotionAllowed())
            {
                StartMotion(_settings.BackdropMode);
            }

            return;
        }

        // Пауза, а не остановка: продолжение не перескакивает фазу.
        if (_window.WindowState == WindowState.Minimized || !_window.IsVisible)
        {
            _motion.Pause(_window);
        }
        else
        {
            _motion.Resume(_window);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopMotion();
        ThemeManager.EffectiveThemeChanged -= OnThemeChanged;
        _window.StateChanged -= OnPowerRelevantChange;
        _window.IsVisibleChanged -= OnVisibilityChanged;
    }
}
