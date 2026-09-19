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

    /// <summary>
    /// Форматы, которые принимают endpoint'ы зрения у всех провайдеров. Остальное надо
    /// перекодировать: GIF, BMP, TIFF и ICO часть моделей отвергает четырёхсоткой на весь запрос.
    /// </summary>
    private static readonly HashSet<string> VisionFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp"
    };

    public static bool IsImageFile(string path) =>
        ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Определяет формат по сигнатуре самих байт; <c>null</c> — сигнатура незнакомая.
    /// </summary>
    /// <remarks>
    /// Заголовку <c>Content-Type</c> верить нельзя, и дело не только во вранье сервера: Steam
    /// честно отдавал <c>image/gif</c> по ссылке на превью, а провайдер зрения отвечал
    /// «Downloaded response does not contain a valid JPG, PNG, WebP, or ICO image» и ронял весь
    /// ход. Формат нужен настоящий, чтобы решить, перекодировать картинку или отдать как есть.
    /// </remarks>
    public static string? SniffMimeType(ReadOnlySpan<byte> data)
    {
        if (Starts(data, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (Starts(data, [0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (Starts(data, "GIF8"u8))
        {
            return "image/gif";
        }

        if (Starts(data, "RIFF"u8) && data.Length >= 12 && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (Starts(data, "BM"u8))
        {
            return "image/bmp";
        }

        if (Starts(data, [0x49, 0x49, 0x2A, 0x00]) || Starts(data, [0x4D, 0x4D, 0x00, 0x2A]))
        {
            return "image/tiff";
        }

        if (Starts(data, [0x00, 0x00, 0x01, 0x00]))
        {
            return "image/x-icon";
        }

        // AVIF и HEIC — контейнер ISO-BMFF: размер коробки, "ftyp", марка.
        if (data.Length >= 12 && data[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = data[8..12];
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
            {
                return "image/avif";
            }

            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) ||
                brand.SequenceEqual("mif1"u8))
            {
                return "image/heic";
            }
        }

        return null;
    }

    private static bool Starts(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);

    public static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".tif" or ".tiff" => "image/tiff",
        _ => "image/png"
    };

    public static ImageAttachment? FromFile(string path)
    {
        if (!File.Exists(path) || !IsImageFile(path))
        {
            return null;
        }

        // Через общий путь: файл с диска тоже бывает в формате, который модель не примет
        // (.gif, .bmp, .tif все трое в списке расширений), и расширение о содержимом не говорит.
        return FromBytes(File.ReadAllBytes(path), GuessMimeType(path), Path.GetFileName(path));
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
    /// нечего — непрозрачная картинка нужного размера в формате из <see cref="VisionFormats"/>,
    /// или формат, который GDI+ не открывает.
    /// </remarks>
    public static ImageAttachment FromBytes(byte[] data, string mimeType, string? label = null)
    {
        // Заявленный тип — только подсказка: по ссылке на превью Steam приезжал анимированный
        // GIF, провайдер зрения его не принял и завалил четырёхсоткой весь ход, а не одну
        // картинку. Формат берём из байт и всё, чего нет в списке принимаемых, перекодируем.
        var actual = SniffMimeType(data) ?? mimeType;
        var accepted = VisionFormats.Contains(actual);

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var original = new Bitmap(stream);
            using var opaque = ImageAlpha.TryComposite(original, Color.White);
            using var resized = Downscale(opaque ?? original);
            if (accepted && opaque is null && resized.Size == original.Size)
            {
                return new ImageAttachment(Convert.ToBase64String(data), actual, label);
            }

            using var output = new MemoryStream();
            var format = actual.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
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
            // GDI+ не открыл — так он отвечает на WebP и AVIF. Перекодировать нечем, отдаём
            // как есть: WebP модель поймёт сама, а на остальном лучшего варианта у нас нет.
            return new ImageAttachment(Convert.ToBase64String(data), actual, label);
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