using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Amarin.Tools;

/// <summary>Что происходит с альфа-каналом растра.</summary>
internal enum AlphaState
{
    /// <summary>Формата с альфой нет — 24bpp, Format32bppRgb, индексированные. Чинить нечего.</summary>
    None,

    /// <summary>Канал есть, и он весь по 255: прозрачности нет.</summary>
    Opaque,

    /// <summary>Канал есть, и он весь по нулю — мусор из CF_DIB.</summary>
    Dead,

    /// <summary>Настоящая прозрачность: значения разные.</summary>
    Real
}

/// <summary>
/// Приведение альфа-канала к виду, в котором картинку видно и человеку, и модели.
/// </summary>
/// <remarks>
/// Telegram, Discord, SwarmUI и всё на Chromium кладут картинку в буфер обмена как CF_DIB,
/// у которого старший байт пикселя форматом не определён — и оставляют там нули. WPF читает это
/// как <c>Bgra32</c> с нулевой альфой, кодировщик пишет насквозь прозрачный PNG: в композере
/// была видна серая подложка карточки, а модель видела чёрный прямоугольник, потому что её
/// декодер кладёт прозрачное на чёрное. Отсюда два разных случая: мусорный канал надо снять
/// (цвета под ним целы), настоящую прозрачность — подложить фоном.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class ImageAlpha
{
    /// <summary>
    /// Возвращает исправленный растр или <c>null</c>, когда исправлять нечего.
    /// </summary>
    /// <remarks>
    /// Контракт нарочно такой: вернувшийся объект всегда новый и всегда на вызывающем, а
    /// <c>null</c> означает «пользуйся исходным». Так вызов пишется одной строкой
    /// <c>using var fixedUp = ImageAlpha.TryFlatten(bmp, Color.White);</c> без двойного Dispose.
    /// </remarks>
    /// <param name="backdrop">
    /// Чем подложить под настоящую прозрачность. Белый: декодер модели кладёт прозрачное на
    /// чёрное, и тёмный логотип в PNG без фона приезжал ей чёрным по чёрному.
    /// </param>
    internal static Bitmap? TryFlatten(Bitmap source, Color backdrop)
    {
        var state = Inspect(source);
        if (state is AlphaState.None or AlphaState.Opaque)
        {
            return null;
        }

        return state == AlphaState.Dead ? ForceOpaque(source) : Composite(source, backdrop);
    }

    /// <summary>
    /// Приводит альфу к фону всегда, когда она не сплошь непрозрачная — и мусорную, и настоящую.
    /// </summary>
    /// <remarks>
    /// Для картинки, пришедшей готовым файлом (с диска, из сети), нулевая альфа осмысленна по
    /// определению формата: там нет неопределённого старшего байта, который можно списать на
    /// мусор. Поэтому «оживлять» канал нельзя — можно только подложить фон.
    /// </remarks>
    internal static Bitmap? TryComposite(Bitmap source, Color backdrop)
    {
        var state = Inspect(source);
        return state is AlphaState.None or AlphaState.Opaque ? null : Composite(source, backdrop);
    }

    /// <summary>Быстрый осмотр канала — отдельно от починки, чтобы было что проверить тестом.</summary>
    internal static AlphaState Inspect(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        // Индексированные форматы (GIF, PNG-8) сюда не попадают намеренно: прозрачность у них
        // задана элементом палитры, а не каналом, и битого CF_DIB в 8bpp не бывает.
        if (!Image.IsAlphaPixelFormat(bitmap.PixelFormat) || bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return AlphaState.None;
        }

        var (min, max) = AlphaRange(bitmap);
        if (min == 255)
        {
            return AlphaState.Opaque;
        }

        // Ложного срабатывания тут быть не может: картинка, где каждый пиксель полностью
        // прозрачен, визуально пуста — терять в ней нечего.
        return max == 0 ? AlphaState.Dead : AlphaState.Real;
    }

    private static (byte Min, byte Max) AlphaRange(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);

        // Format32bppArgb в LockBits просим намеренно: GDI+ сам приведёт к нему и
        // Format32bppPArgb (сняв домножение), и 64-битные форматы.
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[bitmap.Width * 4];
            byte min = 255;
            byte max = 0;

            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(data.Scan0 + (y * data.Stride), row, 0, row.Length);
                for (var i = 3; i < row.Length; i += 4)
                {
                    var alpha = row[i];
                    if (alpha < min)
                    {
                        min = alpha;
                    }

                    if (alpha > max)
                    {
                        max = alpha;
                    }
                }

                // Дальше смотреть нечего: и «мусорный», и «непрозрачный» уже исключены.
                if (min == 0 && max == 255)
                {
                    break;
                }
            }

            return (min, max);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>Снимает мусорный канал, не трогая цвета: картинка под ним целая.</summary>
    private static Bitmap ForceOpaque(Bitmap source)
    {
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        copy.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        var read = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var write = copy.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[source.Width * 4];
                for (var y = 0; y < source.Height; y++)
                {
                    Marshal.Copy(read.Scan0 + (y * read.Stride), row, 0, row.Length);
                    for (var i = 3; i < row.Length; i += 4)
                    {
                        row[i] = 255;
                    }

                    Marshal.Copy(row, 0, write.Scan0 + (y * write.Stride), row.Length);
                }
            }
            finally
            {
                copy.UnlockBits(write);
            }
        }
        finally
        {
            source.UnlockBits(read);
        }

        return copy;
    }

    private static Bitmap Composite(Bitmap source, Color backdrop)
    {
        var flattened = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        flattened.SetResolution(source.HorizontalResolution, source.VerticalResolution);

        using var graphics = Graphics.FromImage(flattened);
        graphics.Clear(backdrop);
        graphics.CompositingMode = CompositingMode.SourceOver;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        // Единицы прибиты к пикселям намеренно: DrawImage(source, 0, 0) меряет по DPI растра,
        // и картинка из буфера, у которой в заголовке 72 dpi, приезжала растянутой.
        graphics.DrawImage(
            source,
            new Rectangle(0, 0, source.Width, source.Height),
            0,
            0,
            source.Width,
            source.Height,
            GraphicsUnit.Pixel);

        return flattened;
    }
}
