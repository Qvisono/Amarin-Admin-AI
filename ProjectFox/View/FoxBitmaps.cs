using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Amarin.Fox;

/// <summary>Сетка символов → замороженная картинка в пикселях кадра (48×48 у позы).</summary>
/// <remarks>
/// Картинка — в настоящую величину кадра, без масштаба: увеличивает её <see cref="FoxSprite"/>
/// целым числом пикселей экрана и без сглаживания. Готовые картинки помнятся по ключу (кадр,
/// мордочка, предмет, глаза, зеркало): Роза перебирает два-три десятка кадров, и собирать каждый
/// заново на каждом шаге незачем.
/// </remarks>
internal sealed class FoxBitmaps
{
    private readonly FoxSprites _sprites;
    private readonly Dictionary<string, BitmapSource> _cache = new(StringComparer.Ordinal);

    public FoxBitmaps(FoxSprites sprites) => _sprites = sprites;

    /// <summary>Картинка кадра анимации с мордочкой и предметом.</summary>
    public BitmapSource Frame(FoxAnimation animation, int frame, string? face, string? item, bool cold, bool flip)
    {
        var key = string.Join('|', animation.Name, frame, face, item, cold, flip);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_cache.Count > 600)
        {
            _cache.Clear();
        }

        var bitmap = Render(_sprites.Compose(animation.Frames[frame], face, item), _sprites.Palette, cold, flip);
        _cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Картинка частицы.</summary>
    public BitmapSource Particle(string name)
    {
        var key = "particle|" + name;
        if (!_cache.TryGetValue(key, out var bitmap))
        {
            var rows = _sprites.Particles.TryGetValue(name, out var grid) ? grid : ["."];
            bitmap = Render([.. rows.Select(row => row.ToCharArray())], _sprites.Palette, cold: false, flip: false);
            _cache[key] = bitmap;
        }

        return bitmap;
    }

    /// <summary>Предмет сам по себе, без Розы: мячик, брошенный человеком.</summary>
    public BitmapSource Item(string name)
    {
        var key = "item|" + name;
        if (!_cache.TryGetValue(key, out var bitmap))
        {
            bitmap = Render(_sprites.Alone(name), _sprites.Palette, cold: false, flip: false);
            _cache[key] = bitmap;
        }

        return bitmap;
    }

    public static BitmapSource Render(char[][] rows, IReadOnlyDictionary<char, FoxColor> palette, bool cold, bool flip)
    {
        var height = rows.Length;
        var width = rows.Max(row => row.Length);
        var pixels = new uint[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = rows[y];
            for (var x = 0; x < row.Length; x++)
            {
                if (!palette.TryGetValue(row[x], out var color))
                {
                    continue;
                }

                var argb = cold ? color.Cold : color.Warm;
                if (argb != 0)
                {
                    pixels[(y * width) + (flip ? width - 1 - x : x)] = argb;
                }
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Роза на экране: кадр, увеличенный целым числом пикселей без сглаживания.
/// </summary>
/// <remarks>
/// <para>
/// Пиксель рисунка — целое число пикселей экрана (<see cref="DeviceScale"/>): иначе при масштабе
/// 150 % одни пиксели меха выходили бы в два экранных, другие в один, и рисунок «плыл». Масштаб
/// интерфейса здесь — подделанный DPI, поэтому число пересчитывается по <c>DpiChanged</c>.
/// </para>
/// <para>
/// Мышь ловится только непрозрачными пикселями (<see cref="HitTestCore"/>): щелчок мимо лап и
/// хвоста уходит в окно, как будто Розы там нет. С клавиатуры Роза не фокусируется — набранное
/// принадлежит полю ввода.
/// </para>
/// </remarks>
internal sealed class FoxSprite : FrameworkElement
{
    private BitmapSource? _source;
    private char[][]? _grid;
    private bool _flip;

    public FoxSprite()
    {
        Focusable = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SnapsToDevicePixels = true;
    }

    /// <summary>Сколько пикселей экрана на пиксель рисунка.</summary>
    public int DeviceScale { get; private set; } = 1;

    /// <summary>Сторона кадра в единицах раскладки.</summary>
    public double Side { get; private set; } = FoxSprites.FrameSize;

    /// <summary>Пересчитывает увеличение под размер из настроек и DPI окна.</summary>
    public void Fit(int size, double dpiScale)
    {
        var device = Math.Max(1, (int)Math.Round(size * dpiScale / FoxSprites.FrameSize));
        DeviceScale = device;
        Side = FoxSprites.FrameSize * device / dpiScale;
        Width = Side;
        Height = Side;
        InvalidateVisual();
    }

    /// <summary>Показывает кадр; сетка нужна, чтобы ловить мышь только по рисунку, и собирается, лишь когда кадр сменился.</summary>
    public void Show(BitmapSource source, Func<char[][]> grid, bool flip)
    {
        if (ReferenceEquals(source, _source))
        {
            return;
        }

        _source = source;
        _grid = grid();
        _flip = flip;
        InvalidateVisual();
    }

    /// <summary>Клетка кадра под точкой элемента; <c>null</c> — мимо рисунка.</summary>
    public (int X, int Y)? CellAt(Point point)
    {
        if (_grid is null || Side <= 0)
        {
            return null;
        }

        var x = (int)Math.Floor(point.X / Side * FoxSprites.FrameSize);
        var y = (int)Math.Floor(point.Y / Side * FoxSprites.FrameSize);
        if (y < 0 || y >= _grid.Length || x < 0 || x >= FoxSprites.FrameSize)
        {
            return null;
        }

        var cellX = _flip ? FoxSprites.FrameSize - 1 - x : x;
        var row = _grid[y];
        return cellX < row.Length && row[cellX] != '.' ? (cellX, y) : null;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_source is not null)
        {
            drawingContext.DrawImage(_source, new Rect(0, 0, Side, Side));
        }
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        CellAt(hitTestParameters.HitPoint) is not null ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;
}
