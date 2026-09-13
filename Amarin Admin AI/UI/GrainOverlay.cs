using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Матовая микротекстура поверх всего окна — то, чем матовый корпус отличается от глянцевого
/// вблизи: рассеянное зерно вместо гладкого блика.
/// </summary>
/// <remarks>
/// <para>
/// Отдельным слоем, а не частью палитры, потому что палитра — это ровно 48
/// <see cref="SolidColorBrush"/> и ничего больше: тест на полноту тем валит любой лишний
/// ключ, а кисть с текстурой ключом и была бы. Зато слой общий: зерно можно поднять на любой
/// теме, а матовые пресеты просто просят его по умолчанию через
/// <see cref="ThemePresetInfo.Grain"/>.
/// </para>
/// <para>
/// Попапы, всплывающие меню и модальные диалоги зерна не получают: это отдельные слоистые
/// окна, в дерево главного окна они не входят. Зерно — на окне, и это видно.
/// </para>
/// </remarks>
internal sealed class GrainOverlay : IDisposable
{
    /// <summary>
    /// Сторона тайла в пикселях текстуры. 128 — компромисс: меньше начинает читаться
    /// повторяемость, больше без пользы занимает память видеокарты.
    /// </summary>
    internal const int TileSize = 128;

    /// <summary>
    /// Во что превращается единица на ползунке. Выше примерно шести процентов шум перестаёт
    /// читаться как поверхность и начинает читаться как грязь на экране или артефакты сжатия,
    /// поэтому весь ход ползунка укладывается в этот потолок.
    /// </summary>
    private const double MaxOpacity = 0.055;

    private static BitmapSource? _noise;

    private readonly Window _window;
    private readonly Rectangle _layer;

    private AppearanceSettings _settings = new();
    private bool _disposed;

    public GrainOverlay(Window window, Rectangle layer)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _layer = layer ?? throw new ArgumentNullException(nameof(layer));

        // Тайл обязан ложиться пиксель в пиксель, иначе шум размажется билинейной
        // интерполяцией и перестанет быть зерном.
        RenderOptions.SetBitmapScalingMode(_layer, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(_layer, EdgeMode.Aliased);

        ThemeManager.EffectiveThemeChanged += OnThemeChanged;
        _window.DpiChanged += OnDpiChanged;
    }

    /// <summary>
    /// Сколько зерна просят прямо сейчас, 0..1. Явное значение в настройках перебивает тему;
    /// <c>null</c> означает «как хочет тема».
    /// </summary>
    public static double Effective(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Math.Clamp(settings.Grain ?? ThemeManager.Current.Grain, 0.0, 1.0);
    }

    /// <summary>Тайл шума. Один на процесс, замороженный: он одинаков для всех тем и окон.</summary>
    public static BitmapSource Noise => _noise ??= BuildNoise();

    public void Apply(AppearanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;

        var amount = Effective(settings);
        if (amount <= 0 || !Allowed())
        {
            // Не просто прозрачный, а свёрнутый: полноэкранная тайловая кисть с нулевой
            // непрозрачностью всё равно попадает в композицию каждого кадра.
            _layer.Visibility = Visibility.Collapsed;
            _layer.Fill = null;
            return;
        }

        _layer.Fill = BuildBrush();
        _layer.Opacity = amount * MaxOpacity;
        _layer.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Программный рендеринг: полноэкранная тайловая кисть означает полный CPU-перерисов окна
    /// на каждый кадр. Та же осторожность, что у анимаций фона в <see cref="AppearanceManager"/>.
    /// </summary>
    internal static bool Allowed() => (RenderCapability.Tier >> 16) >= 1;

    /// <summary>
    /// Окно тайла для заданного масштаба DPI — отдельным методом, чтобы деление можно было
    /// проверить тестом, не трогая настоящий масштаб окна: <see cref="UiScale"/> статичен, и
    /// тест, который его двигает, ломает соседние.
    /// </summary>
    internal static Rect Viewport(double dpiScale)
    {
        if (dpiScale <= 0 || double.IsNaN(dpiScale))
        {
            dpiScale = 1.0;
        }

        var side = TileSize / dpiScale;
        return new Rect(0, 0, side, side);
    }

    /// <summary>
    /// Кисть с тайлом, посаженным на устройственные пиксели.
    /// </summary>
    /// <remarks>
    /// <see cref="UiScale"/> не масштабирует разметку — он шлёт окну подделанный
    /// <c>WM_DPICHANGED</c>, — поэтому масштабируется всё дерево окна, слой зерна в том числе.
    /// Тайл в 128 независимых от устройства единиц при масштабе 150 % растянулся бы в 192
    /// устройственных пикселя, и зерно превратилось бы в мыло. Отсюда деление на текущий DPI
    /// и пересчёт по <see cref="Window.DpiChanged"/>.
    /// </remarks>
    private Brush BuildBrush()
    {
        var brush = new ImageBrush(Noise)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = Viewport(VisualTreeHelper.GetDpi(_window).DpiScaleX)
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// Знаковый шум: половина точек светлее фона, половина темнее, в сумме по тайлу ноль.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Режимов наложения в WPF нет, только альфа-композитинг, поэтому обычный серый оверлей
    /// не добавлял бы зерно, а подтягивал весь интерфейс к своему серому и подъедал контраст.
    /// Знаковая пара «белая точка / чёрная точка» нейтральна сама по себе и меняет только
    /// микрорельеф. Куб от равномерного распределения оставляет большинство точек почти
    /// невидимыми, а редкие — заметными: так выглядит настоящее зерно, а не телевизионный снег.
    /// Зерно генератора фиксировано, чтобы картинка не менялась от запуска к запуску и её
    /// можно было проверить тестом.
    /// </para>
    /// <para>
    /// Нейтрален при этом сам тайл, а не результат наложения: белая точка на тёмной
    /// поверхности поднимает её на <c>(255 − фон) · α</c>, а чёрная опускает всего на
    /// <c>фон · α</c>. На самой тёмной поверхности матовой темы перекос выходит около одной
    /// трёхсотпятидесятой шкалы — ниже различимого шага, — но он есть, он в плюс, и выправить
    /// его глобально нельзя: коэффициент свой у каждой поверхности.
    /// </para>
    /// </remarks>
    private static BitmapSource BuildNoise()
    {
        var random = new Random(0x4D41_5454);
        var pixels = new byte[TileSize * TileSize * 4];

        for (var i = 0; i < TileSize * TileSize; i++)
        {
            var value = (random.NextDouble() * 2.0) - 1.0;
            var signed = value * value * value;
            var alpha = (byte)Math.Round(Math.Abs(signed) * 255.0);
            var ink = signed >= 0 ? (byte)255 : (byte)0;

            var offset = i * 4;
            pixels[offset] = ink;
            pixels[offset + 1] = ink;
            pixels[offset + 2] = ink;
            pixels[offset + 3] = alpha;
        }

        var bitmap = BitmapSource.Create(
            TileSize,
            TileSize,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            TileSize * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private void OnThemeChanged()
    {
        // Карточка темы зовёт только ThemeManager.Apply и до ApplyAppearance не доходит, а
        // зерно у матовых пресетов своё — без этой подписки Matte выбиралась бы без текстуры.
        if (!_disposed)
        {
            Apply(_settings);
        }
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (!_disposed && _layer.Visibility == Visibility.Visible)
        {
            _layer.Fill = BuildBrush();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ThemeManager.EffectiveThemeChanged -= OnThemeChanged;
        _window.DpiChanged -= OnDpiChanged;
    }
}
