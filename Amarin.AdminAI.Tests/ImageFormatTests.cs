using System.Drawing;
using System.Drawing.Imaging;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Формат картинки, которая уезжает модели.
/// </summary>
/// <remarks>
/// Ссылка на страницу Steam приводила `fetch_image` к превью в анимированном GIF — сервер отдавал
/// честный <c>image/gif</c>, а провайдер зрения отвечал «Downloaded response does not contain
/// a valid JPG, PNG, WebP, or ICO image» и ронял четырёхсоткой весь ход целиком, а не одну
/// картинку. Тесты без WPF: здесь только System.Drawing.
/// </remarks>
public sealed class ImageFormatTests
{
    private static byte[] Encode(ImageFormat format, int width = 8, int height = 8)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(255, 10, 200, 60));
        using var stream = new MemoryStream();
        bitmap.Save(stream, format);
        return stream.ToArray();
    }

    [Fact]
    public void A_gif_from_the_network_is_recoded_into_png()
    {
        var attachment = ImageHelpers.FromBytes(Encode(ImageFormat.Gif), "image/gif", "превью");

        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal("image/png", ImageHelpers.SniffMimeType(Convert.FromBase64String(attachment.Base64)));
    }

    [Theory]
    [InlineData("image/bmp")]
    [InlineData("image/tiff")]
    public void Formats_outside_the_accepted_set_do_not_reach_the_model(string mime)
    {
        var bytes = mime == "image/bmp" ? Encode(ImageFormat.Bmp) : Encode(ImageFormat.Tiff);

        var attachment = ImageHelpers.FromBytes(bytes, mime, "файл");

        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public void A_lying_content_type_does_not_decide_the_format()
    {
        // Сервер называет PNG джипегом — раньше это и уезжало в data:image/jpeg с байтами PNG.
        var attachment = ImageHelpers.FromBytes(Encode(ImageFormat.Png), "image/jpeg", "с сервера");

        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public void An_accepted_format_that_needs_nothing_is_passed_through_untouched()
    {
        var bytes = Encode(ImageFormat.Png);

        var attachment = ImageHelpers.FromBytes(bytes, "image/png", "как есть");

        Assert.Equal("image/png", attachment.MimeType);
        Assert.Equal(Convert.ToBase64String(bytes), attachment.Base64);
    }

    [Fact]
    public void Webp_is_recognised_although_gdi_cannot_open_it()
    {
        // RIFF....WEBP — заголовок, который GDI+ не откроет; перекодировать нечем, и такие байты
        // обязаны уехать как есть, а не под чужим типом.
        var bytes = new byte[32];
        "RIFF"u8.CopyTo(bytes);
        "WEBP"u8.CopyTo(bytes.AsSpan(8));

        Assert.Equal("image/webp", ImageHelpers.SniffMimeType(bytes));
        Assert.Equal("image/webp", ImageHelpers.FromBytes(bytes, "application/octet-stream").MimeType);
    }

    [Fact]
    public void An_unknown_signature_keeps_the_declared_type()
    {
        Assert.Null(ImageHelpers.SniffMimeType(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(
            "image/png",
            ImageHelpers.FromBytes([1, 2, 3, 4], "image/png").MimeType);
    }
}
