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

    /// <summary>
    /// Во сколько раз картинки увеличены заранее, без сглаживания: 1 при целом масштабе экрана,
    /// иначе — масштаб, округлённый вверх (см. <see cref="FoxSprite.Fit"/>). Ставит сцена.
    /// </summary>
    public int Prescale { get; set; } = 1;

    /// <summary>Картинка кадра анимации с мордочкой и предметом.</summary>
    public BitmapSource Frame(FoxAnimation animation, int frame, string? face, string? item, bool cold, bool flip)
    {
        var key = string.Join('|', animation.Name, frame, face, item, cold, flip, Prescale);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_cache.Count > 600)
        {
            _cache.Clear();
        }

        var bitmap = Render(_sprites.Compose(animation.Frames[frame], face, item), _sprites.Palette, cold, flip, Prescale);
        _cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Картинка частицы.</summary>
    public BitmapSource Particle(string name)
    {
        var key = "particle|" + name + "|" + Prescale;
        if (!_cache.TryGetValue(key, out var bitmap))
        {
            var rows = _sprites.Particles.TryGetValue(name, out var grid) ? grid : ["."];
            bitmap = Render([.. rows.Select(row => row.ToCharArray())], _sprites.Palette, cold: false, flip: false, Prescale);
            _cache[key] = bitmap;
        }

        return bitmap;
    }

    /// <summary>
    /// Значок эмоции: пузырь <c>emote</c> и значок <c>emote-…</c> по середине его поля 12×9 — так же
    /// собирает его страница-образец. Незнакомый значок — пустой пузырь, а не авария.
    /// </summary>
    public BitmapSource Emote(string name)
    {
        var key = "emote|" + name + "|" + Prescale;
        if (_cache.TryGetValue(key, out var bitmap))
        {
            return bitmap;
        }

        var bubble = _sprites.Particles.TryGetValue("emote", out var grid) ? grid : ["."];
        var rows = bubble.Select(row => row.ToCharArray()).ToArray();
        if (_sprites.Particles.TryGetValue("emote-" + name, out var icon))
        {
            var x0 = 1 + ((12 - icon.Max(row => row.Length)) / 2);
            var y0 = 1 + ((9 - icon.Length) / 2);
            for (var dy = 0; dy < icon.Length; dy++)
            {
                for (var dx = 0; dx < icon[dy].Length; dx++)
                {
                    if (icon[dy][dx] != '.' && y0 + dy < rows.Length && x0 + dx < rows[y0 + dy].Length)
                    {
                        rows[y0 + dy][x0 + dx] = icon[dy][dx];
                    }
                }
            }
        }

        bitmap = Render(rows, _sprites.Palette, cold: false, flip: false, Prescale);
        _cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>Предмет сам по себе, без Розы: мячик, брошенный человеком.</summary>
    public BitmapSource Item(string name)
    {
        var key = "item|" + name + "|" + Prescale;
        if (!_cache.TryGetValue(key, out var bitmap))
        {
            bitmap = Render(_sprites.Alone(name), _sprites.Palette, cold: false, flip: false, Prescale);
            _cache[key] = bitmap;
        }

        return bitmap;
    }

    /// <param name="scale">Пиксель рисунка — квадрат scale×scale пикселей картинки.</param>
    public static BitmapSource Render(char[][] rows, IReadOnlyDictionary<char, FoxColor> palette, bool cold, bool flip, int scale = 1)
    {
        var height = rows.Length;
        var width = rows.Max(row => row.Length);
        var stride = width * scale;
        var pixels = new uint[stride * height * scale];
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
                if (argb == 0)
                {
                    continue;
                }

                var column = (flip ? width - 1 - x : x) * scale;
                for (var dy = 0; dy < scale; dy++)
                {
                    Array.Fill(pixels, argb, (((y * scale) + dy) * stride) + column, scale);
                }
            }
        }

        var bitmap = BitmapSource.Create(stride, height * scale, 96, 96, PixelFormats.Bgra32, null, pixels, stride * 4);
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>
/// Роза на экране: кадр, увеличенный целым числом пикселей без сглаживания.
/// </summary>
/// <remarks>
/// <para>
/// Целый масштаб (пиксель рисунка — ровно n пикселей экрана) рисуется без сглаживания. Дробный
/// (средний размер, 125 % экрана) так рисовать нельзя: одни пиксели меха выходили бы в два
/// экранных, другие в один, и рисунок «плыл». Поэтому картинка заранее увеличена целым числом
/// без сглаживания (<see cref="FoxBitmaps.Prescale"/>) и только потом ужимается до нужного
/// качественным фильтром — пиксели выходят одного размера, а сглажена лишь их граница. Масштаб
/// интерфейса здесь — подделанный DPI, поэтому всё пересчитывается по <c>DpiChanged</c>.
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

    /// <summary>Сколько пикселей экрана на пиксель рисунка; бывает дробным.</summary>
    public double DeviceScale { get; private set; } = 1;

    /// <summary>Во сколько раз увеличивать картинку заранее: 1 — масштаб целый, рисуется как есть.</summary>
    public int Prescale { get; private set; } = 1;

    /// <summary>Сторона кадра в единицах раскладки.</summary>
    public double Side { get; private set; } = FoxSprites.FrameSize;

    /// <summary>Пересчитывает увеличение под размер из настроек и DPI окна.</summary>
    public void Fit(int size, double dpiScale)
    {
        var (device, prescale) = ScaleFor(size, dpiScale);
        DeviceScale = device;
        Prescale = prescale;
        Side = FoxSprites.FrameSize * device / dpiScale;
        Width = Side;
        Height = Side;
        RenderOptions.SetBitmapScalingMode(this, prescale == 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        InvalidateVisual();
    }

    /// <summary>
    /// Пикселей экрана на пиксель рисунка и заранее увеличение. Почти целый масштаб (в пределах
    /// двадцатой) округляется до целого: на крошечную разницу в размере менять чёткие пиксели на
    /// сглаженные незачем.
    /// </summary>
    internal static (double Device, int Prescale) ScaleFor(int size, double dpiScale)
    {
        var exact = Math.Max(1, size * dpiScale / FoxSprites.FrameSize);
        var whole = Math.Round(exact);
        return Math.Abs(exact - whole) < 0.05 ? (whole, 1) : (exact, (int)Math.Ceiling(exact));
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
