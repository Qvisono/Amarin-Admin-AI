using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
internal static class ImageHelpers
{
    private const int MaxDimension = 1920;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff"
    };

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    public static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/png"
    };

    public static ImageFormat GetImageFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
        ".gif" => ImageFormat.Gif,
        ".bmp" => ImageFormat.Bmp,
        ".tif" or ".tiff" => ImageFormat.Tiff,
        _ => ImageFormat.Png
    };

    public static ImageAttachment? FromFile(string path)
    {
        if (!File.Exists(path) || !IsImageFile(path))
        {
            return null;
        }

        // Порядок using ломать нельзя: Bitmap, созданный из потока, требует, чтобы поток жил
        // всё время жизни растра, иначе Save падает с "A generic error occurred in GDI+".
        using var stream = File.OpenRead(path);
        using var original = new Bitmap(stream);

        // Прозрачность сводим на белое: модель кладёт прозрачное на чёрное, и логотип в PNG
        // без фона приезжал ей чёрным по чёрному. Файл с диска — не буфер обмена, нулевая
        // альфа тут осмысленна, поэтому подкладываем фон, а не «оживляем» канал.
        using var opaque = ImageAlpha.TryComposite(original, Color.White);
        using var resized = Downscale(opaque ?? original);
        using var output = new MemoryStream();

        // Сведённая на фон картинка непрозрачна, но исходный формат мог быть без альфы вовсе
        // (JPEG) — формат сохраняем тот же, что у файла, чтобы не раздувать вложение.
        resized.Save(output, GetImageFormat(path));
        return new ImageAttachment(
            Convert.ToBase64String(output.ToArray()),
            GuessMimeType(path),
            Path.GetFileName(path));
    }

    /// <summary>
    /// Wraps bytes that came off the network. Downscaling keeps a 4K wallpaper from being sent to
    /// the model as-is; formats GDI+ cannot open (WebP, AVIF) are passed through untouched rather
    /// than dropped — the model's vision endpoint understands more formats than System.Drawing.
    /// </summary>
    /// <remarks>
    /// Прозрачность здесь тоже сводится на белое, и ради этого приходится перекодировать даже
    /// картинку, которую не надо уменьшать: модель кладёт прозрачное на чёрное, и PNG без фона
    /// приезжал ей чёрным прямоугольником. Байты остаются нетронутыми только там, где менять
    /// нечего — непрозрачная картинка нужного размера, или формат, который GDI+ не открывает.
    /// </remarks>
    public static ImageAttachment FromBytes(byte[] data, string mimeType, string? label = null)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var original = new Bitmap(stream);
            using var opaque = ImageAlpha.TryComposite(original, Color.White);
            using var resized = Downscale(opaque ?? original);
            if (opaque is null && resized.Size == original.Size)
            {
                return new ImageAttachment(Convert.ToBase64String(data), mimeType, label);
            }

            using var output = new MemoryStream();
            var format = mimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                ? ImageFormat.Jpeg
                : ImageFormat.Png;
            resized.Save(output, format);
            return new ImageAttachment(
                Convert.ToBase64String(output.ToArray()),
                format == ImageFormat.Jpeg ? "image/jpeg" : "image/png",
                label);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or ExternalException)
        {
            return new ImageAttachment(Convert.ToBase64String(data), mimeType, label);
        }
    }

    /// <summary>
    /// Растр из памяти: буфер обмена, перетаскивание, снимок экрана.
    /// </summary>
    /// <remarks>
    /// Починка альфы идёт до уменьшения намеренно: на мусорном канале (нули по всей картинке)
    /// билинейная интерполяция в <see cref="Downscale"/> считает с нулевыми весами.
    /// </remarks>
    public static ImageAttachment FromBitmap(Bitmap bitmap, string label, string mimeType = "image/png")
    {
        using var opaque = ImageAlpha.TryFlatten(bitmap, Color.White);
        using var resized = Downscale(opaque ?? bitmap);
        using var stream = new MemoryStream();
        resized.Save(stream, ImageFormat.Png);
        return new ImageAttachment(Convert.ToBase64String(stream.ToArray()), mimeType, label);
    }

    public static Bitmap Downscale(Bitmap source)
    {
        var maxSide = Math.Max(source.Width, source.Height);
        if (maxSide <= MaxDimension)
        {
            return (Bitmap)source.Clone();
        }

        var ratio = MaxDimension / (double)maxSide;
        var width = (int)Math.Round(source.Width * ratio);
        var height = (int)Math.Round(source.Height * ratio);

        var resized = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(resized);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return resized;
    }
}