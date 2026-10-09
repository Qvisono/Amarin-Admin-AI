using System.Buffers.Binary;
using DocumentFormat.OpenXml.Packaging;

namespace Amarin.Core;

/// <summary>Картинка для документа: формат по первым байтам и размер в точках — без декодирования.</summary>
/// <remarks>
/// Расширение и заявленный тип врут (по ссылке «.png» приезжает JPEG), а Word и PDF требуют
/// точного формата. Размер нужен, чтобы вписать картинку в страницу, не исказив пропорций.
/// </remarks>
internal static class ImageFormats
{
    internal readonly record struct Format(string MimeType, PartTypeInfo PartType);

    public static Format? Sniff(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
        {
            return new Format("image/png", ImagePartType.Png);
        }

        if (span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return new Format("image/jpeg", ImagePartType.Jpeg);
        }

        if (span.StartsWith("GIF8"u8))
        {
            return new Format("image/gif", ImagePartType.Gif);
        }

        if (span.StartsWith("BM"u8))
        {
            return new Format("image/bmp", ImagePartType.Bmp);
        }

        return null;
    }

    /// <summary>Ширина и высота в точках; не разобрали — ноль на ноль.</summary>
    public static (int Width, int Height) Size(byte[] bytes)
    {
        var span = bytes.AsSpan();
        try
        {
            if (span.Length >= 24 && span.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
            {
                return (BinaryPrimitives.ReadInt32BigEndian(span[16..]), BinaryPrimitives.ReadInt32BigEndian(span[20..]));
            }

            if (span.Length >= 10 && span.StartsWith("GIF8"u8))
            {
                return (BinaryPrimitives.ReadUInt16LittleEndian(span[6..]), BinaryPrimitives.ReadUInt16LittleEndian(span[8..]));
            }

            if (span.Length >= 26 && span.StartsWith("BM"u8))
            {
                return (BinaryPrimitives.ReadInt32LittleEndian(span[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(span[22..])));
            }

            if (span.Length >= 4 && span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
            {
                return JpegSize(span);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Обрезанный файл — размер неизвестен, картинка встанет по ширине страницы.
        }

        return (0, 0);
    }

    /// <summary>GIF или BMP в PNG — для PDF, который принимает только PNG и JPEG; не открылась — null.</summary>
    public static byte[]? ToPng(byte[] bytes)
    {
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            using var image = System.Drawing.Image.FromStream(input);
            using var output = new MemoryStream();
            image.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Размер JPEG — из маркера начала кадра (SOF0…SOF15, кроме DHT, JPG и DAC).</summary>
    private static (int Width, int Height) JpegSize(ReadOnlySpan<byte> span)
    {
        var at = 2;
        while (at + 9 < span.Length)
        {
            if (span[at] != 0xFF)
            {
                at++;
                continue;
            }

            var marker = span[at + 1];
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return (BinaryPrimitives.ReadUInt16BigEndian(span[(at + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(span[(at + 5)..]));
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                at += 2;
                continue;
            }

            at += 2 + BinaryPrimitives.ReadUInt16BigEndian(span[(at + 2)..]);
        }

        return (0, 0);
    }
}
