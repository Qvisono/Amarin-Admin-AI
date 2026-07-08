using System.Drawing;
using System.Drawing.Imaging;
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