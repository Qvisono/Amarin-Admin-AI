using System.Drawing;
using System.Drawing.Imaging;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Альфа-канал картинки из буфера обмена.
/// </summary>
/// <remarks>
/// Telegram, Discord и SwarmUI кладут картинку в буфер как CF_DIB, у которого старший байт
/// пикселя форматом не определён, и оставляют там нули. Прочитав их как прозрачность, программа
/// отдавала модели насквозь прозрачный PNG: человек видел в композере серую плашку, модель —
/// чёрный прямоугольник. Тесты без WPF: здесь только System.Drawing, окон не нужно.
/// </remarks>
public sealed class ImageAlphaTests
{
    private static Bitmap Filled(int width, int height, PixelFormat format, Color color)
    {
        var bitmap = new Bitmap(width, height, format);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color);
            }
        }

        return bitmap;
    }

    [Fact]
    public void An_all_zero_alpha_channel_is_rubbish_and_the_colour_under_it_survives()
    {
        using var source = Filled(4, 4, PixelFormat.Format32bppArgb, Color.FromArgb(0, 12, 34, 56));
        Assert.Equal(AlphaState.Dead, ImageAlpha.Inspect(source));

        using var fixedUp = ImageAlpha.TryFlatten(source, Color.White);
        Assert.NotNull(fixedUp);
        Assert.Equal(Color.FromArgb(255, 12, 34, 56), fixedUp!.GetPixel(2, 2));
    }

    [Fact]
    public void Real_transparency_is_backed_with_the_given_colour()
    {
        // Половина прозрачная, половина красная: канал осмысленный, значит подкладываем фон,
        // а не объявляем его мусорным.
        using var source = new Bitmap(2, 1, PixelFormat.Format32bppArgb);
        source.SetPixel(0, 0, Color.FromArgb(0, 0, 0, 0));
        source.SetPixel(1, 0, Color.FromArgb(255, 255, 0, 0));
        Assert.Equal(AlphaState.Real, ImageAlpha.Inspect(source));

        using var fixedUp = ImageAlpha.TryFlatten(source, Color.White);
        Assert.NotNull(fixedUp);
        Assert.Equal(Color.FromArgb(255, 255, 255, 255), fixedUp!.GetPixel(0, 0));
        Assert.Equal(Color.FromArgb(255, 255, 0, 0), fixedUp.GetPixel(1, 0));
    }

    [Fact]
    public void A_file_from_disk_keeps_its_colours_but_never_its_transparency()
    {
        // У картинки, пришедшей готовым файлом, нулевая альфа осмысленна по определению формата:
        // «оживлять» канал нельзя, можно только подложить фон.
        using var source = Filled(2, 2, PixelFormat.Format32bppArgb, Color.FromArgb(0, 12, 34, 56));

        using var flattened = ImageAlpha.TryComposite(source, Color.White);
        Assert.NotNull(flattened);
        Assert.Equal(Color.FromArgb(255, 255, 255, 255), flattened!.GetPixel(1, 1));
    }

    [Theory]
    [InlineData(PixelFormat.Format24bppRgb)]
    [InlineData(PixelFormat.Format32bppRgb)]
    [InlineData(PixelFormat.Format8bppIndexed)]
    public void A_format_without_an_alpha_channel_is_left_alone(PixelFormat format)
    {
        using var source = new Bitmap(4, 4, format);
        Assert.Equal(AlphaState.None, ImageAlpha.Inspect(source));
        Assert.Null(ImageAlpha.TryFlatten(source, Color.White));
        Assert.Null(ImageAlpha.TryComposite(source, Color.White));
    }

    [Fact]
    public void A_fully_opaque_channel_costs_no_copy()
    {
        using var source = Filled(4, 4, PixelFormat.Format32bppArgb, Color.FromArgb(255, 1, 2, 3));
        Assert.Equal(AlphaState.Opaque, ImageAlpha.Inspect(source));
        Assert.Null(ImageAlpha.TryFlatten(source, Color.White));
    }

    [Fact]
    public void The_attachment_a_pasted_picture_turns_into_is_actually_visible()
    {
        // Сквозная проверка того, что уедет модели: сегодня без правки здесь приходит A == 0.
        using var source = Filled(8, 8, PixelFormat.Format32bppArgb, Color.FromArgb(0, 0, 0, 255));
        var attachment = ImageHelpers.FromBitmap(source, "вставленное");

        using var stream = new MemoryStream(Convert.FromBase64String(attachment.Base64));
        using var decoded = new Bitmap(stream);
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), decoded.GetPixel(4, 4));
    }

    [Fact]
    public void A_picture_from_the_network_does_not_reach_the_model_transparent()
    {
        using var source = new Bitmap(2, 1, PixelFormat.Format32bppArgb);
        source.SetPixel(0, 0, Color.FromArgb(0, 0, 0, 0));
        source.SetPixel(1, 0, Color.FromArgb(255, 0, 128, 0));
        using var png = new MemoryStream();
        source.Save(png, ImageFormat.Png);

        var attachment = ImageHelpers.FromBytes(png.ToArray(), "image/png", "из сети");

        using var stream = new MemoryStream(Convert.FromBase64String(attachment.Base64));
        using var decoded = new Bitmap(stream);
        Assert.Equal(255, decoded.GetPixel(0, 0).A);
        Assert.Equal(Color.FromArgb(255, 0, 128, 0), decoded.GetPixel(1, 0));
    }
}
