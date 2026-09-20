using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Amarin.UI;

/// <summary>
/// Turns the chosen background picture into a frozen bitmap that is already dimmed-to-taste:
/// saturation and blur are baked into the pixels once, off the UI thread.
/// <para>
/// Baking the blur rather than hanging a <see cref="System.Windows.Media.Effects.BlurEffect"/> on
/// the backdrop matters: an effect re-runs a full-window gaussian on every frame the backdrop
/// animates, and its radius is in DIPs, so the app's fake-DPI UI scaling would multiply the cost
/// again at 250 %. A blur is a low-pass filter, so we get it almost for free by working at a
/// reduced resolution and letting the <see cref="ImageBrush"/> stretch the result back up
/// (<c>BitmapScalingMode.HighQuality</c> is set window-wide by <see cref="PerformanceOptimizer"/>).
/// </para>
/// <para>Brightness is deliberately <em>not</em> baked — it stays an overlay so its slider is live.</para>
/// </summary>
internal static class AppearanceImageCache
{
    /// <summary>
    /// Wallpapers are routinely 4K+. Decoding at that size costs tens of megabytes for a picture
    /// that is about to be dimmed and blurred, so cap the long edge.
    /// </summary>
    private const int MaxEdge = 2560;

    /// <summary>Below this the blur starts eating the picture's shape, not just its detail.</summary>
    private const int MinEdge = 200;

    private const int CacheSize = 4;

    /// <summary>Крупнее этого готовый снимок на диск не пишем — см. <c>WriteDerived</c>.</summary>
    private const long MaxDerivedPixels = 4_000_000;

    /// <summary>
    /// Имя готового файла: та же приставка, что у самой картинки.
    /// </summary>
    /// <remarks>
    /// Приставка общая не случайно. Смена обоев перечисляет <c>background.*</c> и стирает всё,
    /// кроме нового файла (<c>MainWindow.RemoveStoredBackgrounds</c>), — значит устаревший
    /// готовый снимок уносится оттуда же, и заводить ему вторую уборку не нужно. Хвост
    /// <see cref="DerivedSuffix"/> отдельно: по нему файл не берут в архив данных
    /// (<c>DataBundle.CategoryOf</c>) — это производное, оно пересоздаётся само.
    /// </remarks>
    private const string DerivedStem = "background";

    /// <summary>Хвост имени готового файла. Тот же литерал знает <c>DataBundle.CategoryOf</c>.</summary>
    internal const string DerivedSuffix = ".cache.png";

    private static readonly Lock Gate = new();
    private static readonly List<(string Key, BitmapSource Image)> Cache = [];

    /// <summary>
    /// Decodes and processes the picture on a worker thread. Returns <c>null</c> when the file is
    /// missing, unreadable or not a picture. Never throws.
    /// </summary>
    /// <remarks>
    /// The existence check happens on the worker too: a path on a disconnected network share
    /// makes <see cref="File.Exists"/> block for the SMB timeout, and that must not be the UI thread.
    /// </remarks>
    /// <param name="cacheRoot">
    /// Папка профиля, куда кладётся готовый снимок. <c>null</c> — считать заново каждый раз;
    /// так работают вызовы, у которых профиля под рукой нет.
    /// </param>
    public static Task<BitmapSource?> LoadAsync(string? path, double saturation, double blur, string? cacheRoot = null) =>
        Task.Run(() => Load(path, saturation, blur, cacheRoot));

    /// <summary>
    /// Считает картинку заранее, ни на что не глядя.
    /// </summary>
    /// <remarks>
    /// Зовётся из <c>Program</c> до создания окна: пока WPF разбирает разметку главного окна,
    /// рабочий поток успевает распаковать и размыть фон. К первой отрисовке
    /// <see cref="Peek"/> уже попадает, и градиента-затычки человек не видит вовсе.
    /// Брошенная задача: не вышло — фон доедет обычным путём, как и раньше.
    /// </remarks>
    public static void Prewarm(string? path, double saturation, double blur, string? cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _ = LoadAsync(path, saturation, blur, cacheRoot);
    }

    /// <summary>
    /// Полный путь к картинке фона.
    /// </summary>
    /// <remarks>
    /// Настройки держат голое имя файла: картинка копируется в папку профиля и путешествует
    /// вместе с ним. Абсолютный путь всё ещё уважаем — так писали до этой перемены. Разрешение
    /// живёт здесь, а не у <c>AppearanceManager</c>, потому что тем же путём строится ключ
    /// кэша: разойдись эти две строки, прогрев считал бы впустую.
    /// </remarks>
    public static string ResolvePath(string? value, string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return Path.IsPathRooted(value) ? value : Path.Combine(dataRoot, value);
    }

    /// <summary>Cache lookup only — safe and instant on the UI thread. <c>null</c> means "not ready".</summary>
    /// <remarks>
    /// Округление обязано повторять <see cref="Load"/> до последнего знака. Пока его здесь не
    /// было, ключ от ползунка (<c>1.1506024096386072</c>) не совпадал с положенным в кэш
    /// (<c>1.15</c>) никогда, и каждое применение оформления заново мигало градиентом.
    /// </remarks>
    public static BitmapSource? Peek(string? path, double saturation, double blur)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = Key(path, Round(saturation), Round(blur, radius: true), stamp: null);
        lock (Gate)
        {
            foreach (var entry in Cache)
            {
                // The stamp is unknown without touching the disk, so match on the prefix.
                if (entry.Key.StartsWith(key, StringComparison.Ordinal))
                {
                    return entry.Image;
                }
            }
        }

        return null;
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Cache.Clear();
        }
    }

    private static BitmapSource? Load(string? path, double saturation, double blur, string? cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Rounded: the sliders move in fine steps and a 1 % change is invisible, but every
        // distinct value would otherwise be a separate cache entry and a separate pixel pass.
        var sat = Round(saturation);
        var rad = Round(blur, radius: true);

        string stamp;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return null;
            }

            // И время правки, и длина: у скопированного файла время переносится вместе с ним,
            // и подменённая на такую же по времени картинка иначе показывалась бы старой.
            stamp = string.Create(
                CultureInfo.InvariantCulture,
                $"{file.LastWriteTimeUtc.Ticks}-{file.Length}");
        }
        catch
        {
            return null;
        }

        var key = Key(path, sat, rad, stamp);
        lock (Gate)
        {
            for (var i = 0; i < Cache.Count; i++)
            {
                if (Cache[i].Key != key)
                {
                    continue;
                }

                var hit = Cache[i];
                Cache.RemoveAt(i);
                Cache.Add(hit);
                return hit.Image;
            }
        }

        try
        {
            var derived = DerivedPath(cacheRoot, key);
            var result = ReadDerived(derived);
            if (result is null)
            {
                result = Process(Decode(path, rad), sat, rad);
                result.Freeze();
                WriteDerived(derived, result);
            }

            lock (Gate)
            {
                Cache.Add((key, result));
                while (Cache.Count > CacheSize)
                {
                    Cache.RemoveAt(0);
                }
            }

            return result;
        }
        catch
        {
            // Corrupt, an unsupported codec, or a file another process holds exclusively —
            // the caller falls back to the gradient backdrop.
            return null;
        }
    }

    /// <summary>Округление ползунка. Одно на <see cref="Load"/> и <see cref="Peek"/>.</summary>
    private static double Round(double value, bool radius = false) => radius
        ? Math.Round(Math.Clamp(value, 0, 80), 1)
        : Math.Round(Math.Clamp(value, 0, 2), 2);

    private static string Key(string path, double saturation, double blur, string? stamp) =>
        string.Create(CultureInfo.InvariantCulture, $"{path}|{saturation}|{blur}|{stamp}");

    // ───────────────────────── готовый снимок на диске ─────────────────────────

    /// <summary>
    /// Куда класть уже посчитанную картинку. <c>null</c> — никуда.
    /// </summary>
    /// <remarks>
    /// Имя — отпечаток ключа: поменялись картинка, насыщенность или размытие — поменялось и
    /// имя, так что устаревший снимок никогда не будет прочитан как свежий. Подставлять его
    /// приходится ради запуска: исходные обои бывают и на сорок пять мегапикселей, и их
    /// распаковка с размытием стоит сотни миллисекунд на каждом старте. Готовый снимок — это
    /// PNG на полтора мегапикселя, он читается за десятки.
    /// </remarks>
    private static string? DerivedPath(string? cacheRoot, string key)
    {
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(cacheRoot, $"{DerivedStem}.{hash[..16]}{DerivedSuffix}");
    }

    /// <summary>
    /// Убирает прежние снимки, оставляя только что записанный.
    /// </summary>
    /// <remarks>
    /// Смену самих обоев подметает <c>MainWindow.RemoveStoredBackgrounds</c>, а вот ползунки
    /// насыщенности и размытия её не зовут: каждое их новое положение — новый отпечаток,
    /// и без этой уборки папка профиля обрастала бы снимками на каждый сдвиг ползунка.
    /// </remarks>
    private static void SweepDerived(string keep)
    {
        var directory = Path.GetDirectoryName(keep);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, DerivedStem + "*" + DerivedSuffix))
        {
            if (string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch
            {
                // Файл занят или недоступен — это мусор, а не беда: переживёт до следующего раза.
            }
        }
    }

    private static BitmapSource? ReadDerived(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // Недописанный или побитый снимок — не беда: посчитаем заново и перезапишем.
            return null;
        }
    }

    /// <remarks>
    /// Через временный файл: оборванная запись оставила бы обрезанный PNG под правильным
    /// именем, и следующий запуск читал бы его как готовый.
    /// </remarks>
    private static void WriteDerived(string? path, BitmapSource image)
    {
        if (path is null)
        {
            return;
        }

        // Без размытия рабочая картинка остаётся крупной, и PNG на неё вышел бы в несколько
        // мегабайт — чтение такого файла экономит уже немного, а место в папке профиля занимает
        // заметно. Размытый фон (обычный случай) считается по куда меньшему полотну и проходит.
        if ((long)image.PixelWidth * image.PixelHeight > MaxDerivedPixels)
        {
            return;
        }

        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(temporary))
            {
                encoder.Save(stream);
            }

            File.Move(temporary, path, overwrite: true);
            SweepDerived(path);
        }
        catch
        {
            // Диск полон, папка только для чтения — фон от этого не пострадает, просто
            // следующий запуск снова посчитает его сам.
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // И убрать не вышло. Хвост «.tmp» в архив данных всё равно не попадает.
            }
        }
    }

    /// <summary>
    /// Decodes straight to the working size. Dimensions come from the frame header
    /// (<see cref="BitmapCreateOptions.DelayCreation"/> + <see cref="BitmapCacheOption.None"/>),
    /// so the full-size pixels are never materialised just to be measured.
    /// </summary>
    private static BitmapSource Decode(string path, double blur)
    {
        var uri = new Uri(path, UriKind.Absolute);

        // Поток открываем и закрываем сами. С Uri и BitmapCacheOption.None декодер держит файл
        // открытым до сборки мусора, и картинка оставалась занятой процессом: следующая смена
        // обоев не могла переписать background.jpg поверх старого.
        int width, height;
        using (var probe = File.OpenRead(path))
        {
            var frame = BitmapDecoder.Create(
                probe,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];

            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        if (width <= 0 || height <= 0)
        {
            throw new NotSupportedException("Decoder reported an empty frame.");
        }

        // A stronger blur means fewer pixels have to survive: the downsample itself is the
        // low-pass filter, and the brush stretches the result back over the window.
        var scale = 1.0 / (1.0 + (blur / 6.0));
        var longEdge = Math.Max(width, height);
        var target = (int)Math.Round(Math.Min(longEdge, MaxEdge) * scale);
        target = Math.Clamp(target, Math.Min(MinEdge, longEdge), longEdge);

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

        // Set only one axis so the decoder keeps the aspect ratio, and pick it from the frame's
        // own orientation — a rotated phone JPEG reports the already-oriented size here.
        if (width >= height)
        {
            image.DecodePixelWidth = target;
        }
        else
        {
            image.DecodePixelHeight = target;
        }

        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource Process(BitmapSource source, double saturation, double blur)
    {
        var greyOrPunchy = Math.Abs(saturation - 1.0) >= 0.01;
        if (!greyOrPunchy && blur <= 0)
        {
            return source;
        }

        var bgra = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var width = bgra.PixelWidth;
        var height = bgra.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        if (greyOrPunchy)
        {
            Saturate(pixels, saturation);
        }

        if (blur > 0)
        {
            // The decode scale was itself derived from the blur, so in working pixels only a
            // small, near-constant residual softening is left. Three box passes approximate a
            // gaussian closely enough that no edge of the original detail survives.
            var radius = (int)Math.Clamp(Math.Round(2 + (blur / 20.0)), 1, 6);
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(pixels, width, height, radius);
            }
        }

        var target = new WriteableBitmap(width, height, bgra.DpiX, bgra.DpiY, PixelFormats.Bgra32, null);
        target.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, stride, 0);
        return target;
    }

    /// <summary>
    /// Pulls each pixel towards (0) or away from (&gt;1) its own luminance. Rec. 709 weights, so a
    /// greyscale pass keeps the perceived brightness of the original.
    /// </summary>
    private static void Saturate(byte[] pixels, double amount)
    {
        for (var i = 0; i < pixels.Length; i += 4)
        {
            double b = pixels[i];
            double g = pixels[i + 1];
            double r = pixels[i + 2];
            var luma = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            pixels[i] = Clamp(luma + ((b - luma) * amount));
            pixels[i + 1] = Clamp(luma + ((g - luma) * amount));
            pixels[i + 2] = Clamp(luma + ((r - luma) * amount));
        }
    }

    /// <summary>Separable box blur: a horizontal pass then a vertical one, alpha left alone.</summary>
    private static void BoxBlur(byte[] pixels, int width, int height, int radius)
    {
        var scratch = new byte[pixels.Length];
        BlurAxis(pixels, scratch, width, height, radius, horizontal: true);
        BlurAxis(scratch, pixels, width, height, radius, horizontal: false);
    }

    private static void BlurAxis(byte[] src, byte[] dst, int width, int height, int radius, bool horizontal)
    {
        var outer = horizontal ? height : width;
        var inner = horizontal ? width : height;
        var step = horizontal ? 4 : width * 4;
        var jump = horizontal ? width * 4 : 4;

        for (var o = 0; o < outer; o++)
        {
            var line = o * jump;
            int sumB = 0, sumG = 0, sumR = 0, count = 0;

            // Seed the window with [0, radius].
            for (var i = 0; i <= Math.Min(radius, inner - 1); i++)
            {
                var p = line + (i * step);
                sumB += src[p];
                sumG += src[p + 1];
                sumR += src[p + 2];
                count++;
            }

            for (var i = 0; i < inner; i++)
            {
                var p = line + (i * step);
                dst[p] = (byte)(sumB / count);
                dst[p + 1] = (byte)(sumG / count);
                dst[p + 2] = (byte)(sumR / count);
                dst[p + 3] = src[p + 3];

                // Slide the window: drop i-radius, take i+radius+1.
                var leaving = i - radius;
                if (leaving >= 0)
                {
                    var q = line + (leaving * step);
                    sumB -= src[q];
                    sumG -= src[q + 1];
                    sumR -= src[q + 2];
                    count--;
                }

                var entering = i + radius + 1;
                if (entering < inner)
                {
                    var q = line + (entering * step);
                    sumB += src[q];
                    sumG += src[q + 1];
                    sumR += src[q + 2];
                    count++;
                }
            }
        }
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(value, 0, 255);
}
