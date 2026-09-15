using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выбор представления картинки из буфера обмена и перетаскивания.
/// </summary>
/// <remarks>
/// Telegram, Discord, SwarmUI и всё на Chromium кладут в буфер сразу несколько представлений
/// одной картинки. <c>Clipboard.GetImage()</c> брал худшее — CF_DIB, где старший байт пикселя
/// форматом не определён, — и картинка приезжала прозрачной. Настоящий PNG лежит рядом.
/// Настоящий буфер обмена здесь не трогается: это состояние всей машины, и тест, который в него
/// пишет, портит человеку работу и разваливает соседей. Разбор для того и вынесен в функцию от
/// <see cref="IDataObject"/>.
/// </remarks>
public sealed class ClipboardImageTests
{
    /// <summary>Подделка переносимого объекта: отдаёт ровно то, что в неё положили.</summary>
    private sealed class FakeData : IDataObject
    {
        private readonly Dictionary<string, object> _items = new(StringComparer.Ordinal);

        public FakeData Put(string format, object value)
        {
            _items[format] = value;
            return this;
        }

        public object? GetData(string format) => _items.GetValueOrDefault(format);

        public object? GetData(string format, bool autoConvert) => GetData(format);

        public object? GetData(Type format) => GetData(format.FullName!);

        public bool GetDataPresent(string format) => _items.ContainsKey(format);

        public bool GetDataPresent(string format, bool autoConvert) => GetDataPresent(format);

        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName!);

        public string[] GetFormats() => [.. _items.Keys];

        public string[] GetFormats(bool autoConvert) => GetFormats();

        public void SetData(object data) => throw new NotSupportedException();

        public void SetData(string format, object data) => Put(format, data);

        public void SetData(Type format, object data) => Put(format.FullName!, data);

        public void SetData(string format, object data, bool autoConvert) => Put(format, data);
    }

    /// <summary>Настоящий PNG: 2x1, красный и зелёный, полностью непрозрачный.</summary>
    private static byte[] HealthyPng()
    {
        using var bitmap = new Bitmap(2, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.FromArgb(255, 255, 0, 0));
        bitmap.SetPixel(1, 0, Color.FromArgb(255, 0, 255, 0));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>
    /// 32-битный DIB синего цвета с нулями в старшем байте пикселя — ровно то, что кладут
    /// в буфер Telegram и Chromium.
    /// </summary>
    /// <param name="headerSize">40 — BITMAPINFOHEADER, 124 — BITMAPV5HEADER.</param>
    /// <param name="compression">0 — BI_RGB, 3 — BI_BITFIELDS (три маски перед пикселями).</param>
    /// <param name="alphaMask">bV5AlphaMask: только она обещает, что старший байт — прозрачность.</param>
    private static byte[] DeadAlphaDib(
        int width = 2,
        int height = 2,
        int headerSize = 40,
        uint compression = 0,
        uint alphaMask = 0)
    {
        var stride = width * 4;
        var masks = compression == 3 && headerSize == 40 ? 12 : 0;
        var dib = new byte[headerSize + masks + (stride * height)];

        var header = new List<byte>();
        header.AddRange(BitConverter.GetBytes(headerSize));     // biSize
        header.AddRange(BitConverter.GetBytes(width));          // biWidth
        header.AddRange(BitConverter.GetBytes(height));         // biHeight, снизу вверх
        header.AddRange(BitConverter.GetBytes((ushort)1));      // biPlanes
        header.AddRange(BitConverter.GetBytes((ushort)32));     // biBitCount
        header.AddRange(BitConverter.GetBytes(compression));    // biCompression
        header.AddRange(BitConverter.GetBytes((uint)(stride * height)));
        header.AddRange(new byte[16]);                          // разрешение и палитра
        header.CopyTo(dib);

        if (headerSize >= 124)
        {
            // bV5RedMask, bV5GreenMask, bV5BlueMask, bV5AlphaMask идут подряд с 40-го байта.
            BitConverter.GetBytes(0x00FF0000u).CopyTo(dib, 40);
            BitConverter.GetBytes(0x0000FF00u).CopyTo(dib, 44);
            BitConverter.GetBytes(0x000000FFu).CopyTo(dib, 48);
            BitConverter.GetBytes(alphaMask).CopyTo(dib, 52);
        }

        for (var i = headerSize + masks; i < dib.Length; i += 4)
        {
            dib[i] = 0xFF;      // синий
            dib[i + 1] = 0x00;
            dib[i + 2] = 0x00;
            dib[i + 3] = 0x00;  // «альфа», которой в BI_RGB не существует
        }

        return dib;
    }

    [Fact]
    public void A_header_that_never_promised_alpha_decodes_opaque()
    {
        // В BITMAPINFOHEADER с BI_RGB старший байт 32-битного пикселя форматом не определён.
        // Читая его как прозрачность, программа отдавала модели пустой PNG.
        using var bitmap = Amarin.Tools.ClipboardNative.TryDecodeDib(DeadAlphaDib());

        Assert.NotNull(bitmap);
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), bitmap!.GetPixel(0, 0));
    }

    [Fact]
    public void A_v5_header_with_an_alpha_mask_is_believed()
    {
        using var bitmap = Amarin.Tools.ClipboardNative.TryDecodeDib(
            DeadAlphaDib(headerSize: 124, alphaMask: 0xFF000000u));

        Assert.NotNull(bitmap);
        Assert.Equal(0, bitmap!.GetPixel(0, 0).A);
    }

    [Fact]
    public void The_masks_of_a_bitfields_dib_are_not_mistaken_for_pixels()
    {
        // Chromium кладёт именно BI_BITFIELDS: три маски лежат между заголовком и пикселями,
        // и без учёта этих двенадцати байт картинка приезжала сдвинутой.
        using var bitmap = Amarin.Tools.ClipboardNative.TryDecodeDib(
            DeadAlphaDib(compression: 3));

        Assert.NotNull(bitmap);
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), bitmap!.GetPixel(0, 0));
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), bitmap.GetPixel(1, 1));
    }

    [Fact]
    public void The_png_format_wins_over_the_dib_lying_next_to_it()
    {
        var png = HealthyPng();
        var data = new FakeData()
            .Put("PNG", new MemoryStream(png))
            .Put(DataFormats.Dib, new MemoryStream(DeadAlphaDib()));

        var attachment = ClipboardImages.TryRead(data, "вставленное");

        Assert.NotNull(attachment);
        Assert.Equal("image/png", attachment!.MimeType);
        Assert.Equal(Convert.ToBase64String(png), attachment.Base64);
    }

    [Fact]
    public void The_mime_spelling_of_the_png_format_is_tried_too()
    {
        var data = new FakeData().Put("image/png", HealthyPng());

        Assert.NotNull(ClipboardImages.TryRead(data, "вставленное"));
    }

    [Fact]
    public void A_format_that_only_claims_to_be_png_is_ignored()
    {
        // Часть программ регистрирует «PNG» и кладёт туда путь к файлу.
        var data = new FakeData().Put("PNG", new MemoryStream("C:\\tmp\\a.png"u8.ToArray()));

        Assert.Null(ClipboardImages.TryRead(data, "вставленное"));
    }

    [Fact]
    public void Bytes_arrive_whether_the_format_holds_a_stream_or_an_array()
    {
        var png = HealthyPng();
        var fromArray = ClipboardImages.TryGetBytes(new FakeData().Put("PNG", png), "PNG");
        var fromStream = ClipboardImages.TryGetBytes(
            new FakeData().Put("PNG", new MemoryStream(png)),
            "PNG");

        Assert.Equal(png, fromArray);
        Assert.Equal(png, fromStream);
    }

    [Fact]
    public void A_dib_with_a_dead_alpha_channel_still_produces_a_visible_picture()
    {
        var data = new FakeData().Put(DataFormats.Dib, new MemoryStream(DeadAlphaDib()));

        var attachment = ClipboardImages.TryRead(data, "вставленное");

        Assert.NotNull(attachment);
        using var stream = new MemoryStream(Convert.FromBase64String(attachment!.Base64));
        using var decoded = new Bitmap(stream);
        Assert.Equal(Color.FromArgb(255, 0, 0, 255), decoded.GetPixel(0, 0));
    }

    [Fact]
    public void An_object_with_nothing_but_text_is_not_a_picture()
    {
        var data = new FakeData().Put(DataFormats.UnicodeText, "привет");

        Assert.False(ClipboardImages.Contains(data));
        Assert.Null(ClipboardImages.TryRead(data, "вставленное"));
    }

    [Fact]
    public void A_source_that_offers_only_png_is_still_droppable()
    {
        // До правки курсор над композером показывал перечёркнутый круг: проверялся один
        // DataFormats.Bitmap, которого у такого источника нет.
        var data = new FakeData().Put("PNG", HealthyPng());

        Assert.True(ClipboardImages.Contains(data));
        Assert.True(Amarin.UI.MainWindow.HasDroppableAttachment(data));
    }
}
