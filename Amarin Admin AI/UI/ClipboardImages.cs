using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media.Imaging;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Разбор переносимого объекта в картинку — общий для вставки и перетаскивания.
/// </summary>
/// <remarks>
/// Буфер обмена и перетаскивание — это один и тот же <see cref="IDataObject"/>, поэтому и код
/// один. Порядок форматов не случаен: Telegram, Discord, SwarmUI и всё на Chromium кладут в
/// буфер сразу несколько представлений одной картинки, а <c>Clipboard.GetImage()</c> выбирает
/// из них худшее — CF_DIB, у которого старший байт пикселя по формату не определён и на практике
/// нулевой. Из него выходил насквозь прозрачный PNG: в композере видно серую подложку карточки
/// вместо снимка, а модель видела чёрный прямоугольник, потому что её декодер кладёт прозрачное
/// на чёрное. Настоящий PNG лежит там же, в зарегистрированном формате «PNG», и берём его первым.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ClipboardImages
{
    /// <summary>Форматы с готовым файлом внутри — их отдаём как есть.</summary>
    private static readonly string[] EncodedFormats = ["PNG", "image/png"];

    /// <summary>
    /// Растровые форматы. «Format17» — это CF_DIBV5: известного имени у него в
    /// <see cref="DataFormats"/> нет, и запрашивается он по сгенерированному. Пробуется раньше
    /// CF_DIB потому, что Windows синтезирует CF_DIB из CF_DIBV5, теряя по дороге маску альфы.
    /// </summary>
    private static readonly string[] RasterFormats = ["Format17", DataFormats.Dib];

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Есть ли в объекте хоть что-то похожее на картинку — для курсора при перетаскивании.</summary>
    internal static bool Contains(IDataObject? data)
    {
        if (data is null)
        {
            return false;
        }

        foreach (var format in EncodedFormats)
        {
            if (IsPresent(data, format))
            {
                return true;
            }
        }

        foreach (var format in RasterFormats)
        {
            if (IsPresent(data, format))
            {
                return true;
            }
        }

        // autoConvert здесь нарочно: именно он синтезирует Bitmap из CF_DIB, и без него
        // источник, отдающий только DIB, показывал перечёркнутый курсор.
        return IsPresent(data, DataFormats.Bitmap, autoConvert: true);
    }

    /// <summary>
    /// Единственная точка разбора.
    /// </summary>
    /// <remarks>
    /// Чистая функция: ничего, кроме переданного объекта, не трогает — поэтому её можно проверить
    /// тестом на самодельном <see cref="IDataObject"/>, не притрагиваясь к настоящему буферу
    /// обмена, который является состоянием всей машины.
    /// </remarks>
    internal static ImageAttachment? TryRead(IDataObject? data, string label)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var format in EncodedFormats)
        {
            var bytes = TryGetBytes(data, format);

            // Проверка сигнатуры обязательна: встречаются программы, которые регистрируют
            // «PNG» и кладут туда путь к файлу — без неё мусор уехал бы модели под видом картинки.
            if (bytes is { Length: > 8 } && bytes.AsSpan(0, 8).SequenceEqual(PngMagic))
            {
                return ImageHelpers.FromBytes(bytes, "image/png", label);
            }
        }

        foreach (var format in RasterFormats)
        {
            var dib = TryGetBytes(data, format);
            if (dib is null)
            {
                continue;
            }

            using var bitmap = ClipboardNative.TryDecodeDib(dib);
            if (bitmap is not null)
            {
                return ImageHelpers.FromBitmap(bitmap, label);
            }
        }

        // Последний шанс — то же, что делает внутри Clipboard.GetImage(): автоконверсия CF_DIB
        // в BitmapSource. Альфу здесь чинит уже ImageHelpers.FromBitmap.
        using var converted = TryGetBitmap(data);
        return converted is null ? null : ImageHelpers.FromBitmap(converted, label);
    }

    /// <summary>Байты зарегистрированного формата: WPF отдаёт HGLOBAL как <see cref="MemoryStream"/>.</summary>
    internal static byte[]? TryGetBytes(IDataObject data, string format)
    {
        try
        {
            if (!data.GetDataPresent(format, autoConvert: false))
            {
                return null;
            }

            return data.GetData(format, autoConvert: false) switch
            {
                byte[] bytes => bytes,
                MemoryStream memory => memory.ToArray(),
                Stream stream => ReadAll(stream),
                _ => null
            };
        }
        catch (Exception ex) when (IsTransferFailure(ex))
        {
            // Владелец буфера умер между проверкой и чтением. Каждый формат ловит свой отказ
            // сам: один общий catch на весь разбор отменил бы и все следующие форматы.
            return null;
        }
    }

    private static Bitmap? TryGetBitmap(IDataObject data)
    {
        try
        {
            if (data.GetData(DataFormats.Bitmap, autoConvert: true) is not BitmapSource source)
            {
                return null;
            }

            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch (Exception ex) when (IsTransferFailure(ex))
        {
            return null;
        }
    }

    private static bool IsPresent(IDataObject data, string format, bool autoConvert = false)
    {
        try
        {
            return data.GetDataPresent(format, autoConvert);
        }
        catch (Exception ex) when (IsTransferFailure(ex))
        {
            return false;
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static bool IsTransferFailure(Exception ex) =>
        ex is ExternalException or NotSupportedException or IOException or OutOfMemoryException
            or ArgumentException or InvalidOperationException;
}
