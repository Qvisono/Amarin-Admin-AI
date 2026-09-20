using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Paints the window backdrop and pushes the derived "glass" palette into
/// <see cref="ThemeManager.OverrideSlot"/>.
/// <para>
/// The whole UI reads its colours through <c>DynamicResource</c>, and merged dictionaries resolve
/// last-to-first, so writing alpha-adjusted copies of the <c>Bg.*</c> brushes into slot 3 makes
/// every panel translucent at once without touching a line of the window's XAML.
/// </para>
/// </summary>
internal sealed class AppearanceManager : IDisposable
{
    /// <summary>
    /// How much of the requested transparency each surface takes. 0 keeps a surface fully opaque.
    /// <para>
    /// Two keys are deliberately absent. <c>Bg.Window</c> only fills the root Grid, which sits
    /// <em>under</em> the backdrop, so thinning it would be invisible — and the window is
    /// DWM-glass-framed, so a translucent root risks bleeding the desktop at the edge.
    /// <c>Bg.Panel</c> backs the <c>AllowsTransparency</c> popups (attach menu, model picker,
    /// combo drop-downs, context menus) and all six modal dialog bodies; those are separate
    /// layered windows, so making them translucent shows the <em>desktop</em>, not the backdrop.
    /// The composer uses <c>Bg.Glass</c> instead, which exists for exactly this reason.
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
    /// Muted greys were picked against an opaque palette. Over a photo they fall under 3:1, so
    /// each tier is promoted one step brighter while a backdrop is up.
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

    /// <summary>Whether a backdrop is actually painted, after the master switch and image checks.</summary>
    public bool BackdropActive { get; private set; }

    /// <summary>
    /// Profile folder the background picture was copied into. Settings hold a bare file name so
    /// the picture travels with the profile; this is what it is resolved against.
    /// </summary>
    public string DataRoot { get; set; } = AppPaths.Root;

    /// <summary>
    /// Alpha for one surface at the requested glass opacity. Pure and deterministic — the
    /// invariant worth remembering is that <paramref name="glassOpacity"/> of 1 reproduces the
    /// palette exactly, so "enabled but neutral" looks identical to the shipped theme.
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

    /// <summary>Surfaces that participate in the glass treatment, for tests and for the UI copy.</summary>
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

        // An opaque floor under everything: with Fit or Tile the picture need not cover the
        // window, and a gap here would composite against the DWM glass frame.
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

        // Brightness as an overlay, not baked: the slider stays live and costs nothing.
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

        // A white sheen vanishes on a light palette — flip it dark there.
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
    /// Stops are laid out as a palindrome (c0…cn…c0) so that Aurora, which slides the brush along
    /// its axis, never shows the seam a non-symmetric stop list produces where the ends meet.
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

        // Left unfrozen on purpose: Drift and Aurora animate StartPoint/EndPoint.
        return brush;
    }

    /// <summary>
    /// Endpoints for an angle, on a circle around the centre of the unit square.
    /// <para>
    /// Sweeping these directly is what Drift animates. Rotating the brush with a
    /// <c>RelativeTransform</c> instead would be wrong twice over: the unit square is anisotropic
    /// on a non-square window, so the axis would sweep at uneven speed, and the rotated endpoints
    /// leave [0,1], which with the default Pad spread paints growing slabs of the end colours.
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

        // Not processed yet. Show the gradient meanwhile and swap in when the worker lands,
        // so picking a 4K wallpaper never stalls the UI thread.
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
    /// An endless timeline keeps WPF's render thread awake, which on a chat window that stays
    /// open all day is a real battery cost. Motion is therefore refused outright where it cannot
    /// look good anyway, and paused whenever the window is not on screen.
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

        // Software rendering: every animated frame is a full CPU repaint of the window.
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

            // A photo cannot rotate its gradient axis; it gets a slow Ken Burns instead.
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
        if (_brightness.Fill is null)
        {
            _brightness.Fill = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

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

        // Sixteen samples around the circle: enough that the linear interpolation between them
        // is indistinguishable from a true rotation, cheap enough to be free.
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

        // A RenderTransform costs no layout, and the picture is already blurred, so the
        // upscale is invisible.
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
    /// Rebuilds slot 3. Called directly rather than only from
    /// <see cref="ThemeManager.EffectiveThemeChanged"/>, because
    /// <see cref="ThemeManager.Apply"/> short-circuits — and skips the event — when the palette
    /// is unchanged, which is exactly the startup case.
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
                // Bg.Glass mirrors Bg.Panel in every palette; the split exists only so the
                // composer can be frosted without dragging popups and dialogs with it.
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

            // The scrim was tuned against an opaque window. Over a bright photo the shipped
            // value no longer separates a modal dialog from what is behind it.
            if (Resource("Overlay.Scrim") is SolidColorBrush scrim)
            {
                var c = scrim.Color;
                overrides["Overlay.Scrim"] = Frozen(Color.FromArgb(Math.Max(c.A, (byte)0xB8), c.R, c.G, c.B));
            }

            // Fills go translucent, so the borders become the main edge cue.
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
    /// Reads from the base palette, never through the override slot: deriving a glass colour from
    /// an already-thinned one would compound the alpha on every reapply.
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

    /// <summary>Moves a colour towards white (positive) or black (negative).</summary>
    public static Color Shift(Color color, double amount)
    {
        static byte Mix(byte value, double amount) => amount >= 0
            ? (byte)Math.Round(value + ((255 - value) * amount))
            : (byte)Math.Round(value * (1 + amount));

        return Color.FromRgb(Mix(color.R, amount), Mix(color.G, amount), Mix(color.B, amount));
    }

    /// <summary>Rec. 709 relative luminance, 0..1 — used to pick black or white text on the accent.</summary>
    public static double Luminance(Color color) =>
        ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    private void OnThemeChanged()
    {
        // Synchronously: slot 3 currently holds colours derived from the palette that was just
        // replaced, and being last it still wins. Deferring this would show a real flash.
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

        // Pause rather than Stop, so resuming does not jump the phase.
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
