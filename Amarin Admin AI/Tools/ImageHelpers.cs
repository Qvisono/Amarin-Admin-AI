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

        using var stream = File.OpenRead(path);
        using var original = new Bitmap(stream);
        using var resized = Downscale(original);
        using var output = new MemoryStream();
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
    public static ImageAttachment FromBytes(byte[] data, string mimeType, string? label = null)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            using var original = new Bitmap(stream);
            using var resized = Downscale(original);
            if (ReferenceEquals(resized, original) || resized.Size == original.Size)
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

    public static ImageAttachment FromBitmap(Bitmap bitmap, string label, string mimeType = "image/png")
    {
        using var resized = Downscale(bitmap);
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