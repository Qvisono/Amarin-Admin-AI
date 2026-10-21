using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Amarin.UI;

/// <summary>Подмена системного курсора: что делает Windows, вынесено, чтобы тесты её не трогали.</summary>
internal interface ICursorSystem
{
    /// <summary>Ставит картинку всем стандартным курсорам Windows.</summary>
    void Replace(BitmapSource image, int hotspotX, int hotspotY);

    /// <summary>Возвращает курсоры из схемы, выбранной человеком.</summary>
    void Reset();
}

/// <summary>
/// Курсор Amarin на время управления: мягкий красно-розовый наконечник вместо стрелки Windows —
/// чтобы было видно, что мышь сейчас у ИИ.
/// </summary>
/// <remarks>
/// <para>
/// Подменяются системные курсоры (<c>SetSystemCursor</c>): своё окно поверх курсора чужие окна
/// не прячет. Значит, курсор обязан вернуться, что бы ни случилось: конец управления, выход
/// процесса, необработанная ошибка. На жёсткое падение, после которого не выполнится ничего,
/// лежит метка: следующий запуск видит её и возвращает схему человека.
/// </para>
/// <para>
/// Возврат — <c>SPI_SETCURSORS</c>: Windows перечитывает схему курсоров из реестра, то есть ту,
/// что выбрал человек.
/// </para>
/// </remarks>
internal sealed class ComputerCursor
{
    private readonly string _marker;
    private readonly ICursorSystem _system;
    private bool _applied;
    private bool _hooked;

    /// <param name="marker">Файл-метка подмены; по умолчанию — в данных машины.</param>
    /// <param name="system">Что делает Windows; подменяется в тестах.</param>
    public ComputerCursor(string? marker = null, ICursorSystem? system = null)
    {
        _marker = marker ?? DefaultMarker;
        _system = system ?? new WindowsCursors();
    }

    /// <summary>Метка в данных машины, а не профиля: курсор один на пользователя Windows.</summary>
    internal static string DefaultMarker =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmarinAdminAI", "cursor-replaced");

    public void Apply()
    {
        if (_applied)
        {
            return;
        }

        // Метка — до подмены: упади процесс между ними, следующий запуск вернёт курсор.
        if (Path.GetDirectoryName(_marker) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(_marker, "");
        var size = Size();
        _system.Replace(Render(size), Hotspot(size), Hotspot(size));
        _applied = true;

        if (!_hooked)
        {
            _hooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
        }
    }

    public void Restore()
    {
        if (!_applied)
        {
            return;
        }

        _applied = false;
        _system.Reset();
        TryDelete(_marker);
    }

    /// <summary>Возвращает курсор, подменённый процессом, который не успел вернуть его сам.</summary>
    public static void RestoreLeftover(string? marker = null, ICursorSystem? system = null)
    {
        marker ??= DefaultMarker;
        if (!File.Exists(marker))
        {
            return;
        }

        (system ?? new WindowsCursors()).Reset();
        TryDelete(marker);
    }

    /// <summary>
    /// Размер курсора: базовый размер из «Параметры → Указатель мыши» на масштаб главного монитора.
    /// </summary>
    internal static int Size()
    {
        var baseSize = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Cursors", "CursorBaseSize", 32) is int value and >= 32
            ? value
            : 32;
        return (int)Math.Round(baseSize * GetDpiForSystem() / 96.0);
    }

    /// <summary>Точка нажатия — кончик наконечника, в пикселях картинки.</summary>
    internal static int Hotspot(int size) => (int)Math.Round(Tip * size / Grid);

    /// <summary>
    /// Рисунок: округлый треугольник наконечника, красно-розовый, с бликом и белой обводкой. Сетка
    /// 24 на 24, кончик — в точке <see cref="Tip"/>.
    /// </summary>
    internal static BitmapSource Render(int size)
    {
        var shape = Geometry.Parse("M 0,0 L 15,7.1 L 7.9,9.1 L 5.1,16.1 Z");
        var highlight = Geometry.Parse("M 2.3,2.9 L 9.1,6.4 L 5.4,7.4 Z");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size / Grid, size / Grid));
            context.PushTransform(new TranslateTransform(Tip, Tip));

            // Толстая обводка тем же цветом скругляет углы: так наконечник мягкий, а не колючий.
            var body = new SolidColorBrush(Body);
            context.DrawGeometry(body, new Pen(body, 3.2) { LineJoin = PenLineJoin.Round }, shape);
            context.DrawGeometry(null, new Pen(Brushes.White, 1.3) { LineJoin = PenLineJoin.Round }, shape);
            context.DrawGeometry(new SolidColorBrush(Glint), null, highlight);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Сетка рисунка и место кончика в ней.</summary>
    private const double Grid = 24;

    private const double Tip = 2.6;

    private static readonly Color Body = Color.FromRgb(0xF2, 0x41, 0x6B);
    private static readonly Color Glint = Color.FromRgb(0xFF, 0x8F, 0xAB);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Метка останется — следующий запуск вернёт курсор ещё раз, это безвредно.
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>Настоящая подмена курсоров Windows.</summary>
    private sealed class WindowsCursors : ICursorSystem
    {
        /// <summary>Стандартные курсоры: стрелка, текст, ожидание, руки, размеры и запрет.</summary>
        private static readonly uint[] Standard =
        [
            32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650
        ];

        public void Replace(BitmapSource image, int hotspotX, int hotspotY)
        {
            var cursor = Create(image, hotspotX, hotspotY);
            if (cursor == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // SetSystemCursor забирает дескриптор себе: каждому курсору — своя копия.
                foreach (var id in Standard)
                {
                    _ = SetSystemCursor(CopyIcon(cursor), id);
                }
            }
            finally
            {
                _ = DestroyIcon(cursor);
            }
        }

        public void Reset() => _ = SystemParametersInfoW(SpiSetCursors, 0, IntPtr.Zero, 0);

        /// <summary>
        /// Курсор из картинки: цвет — 32 бита с прямой альфой, маска пустая, как у курсоров с
        /// полупрозрачностью.
        /// </summary>
        private static IntPtr Create(BitmapSource image, int hotspotX, int hotspotY)
        {
            var pixels = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var width = pixels.PixelWidth;
            var height = pixels.PixelHeight;
            var data = new byte[width * height * 4];
            pixels.CopyPixels(data, width * 4, 0);

            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32
            };
            var color = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
            if (color == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
            try
            {
                Marshal.Copy(data, 0, bits, data.Length);
                var info = new IconInfo { Icon = false, HotspotX = hotspotX, HotspotY = hotspotY, Mask = mask, Color = color };
                return CreateIconIndirect(ref info);
            }
            finally
            {
                _ = DeleteObject(color);
                _ = DeleteObject(mask);
            }
        }

        private const uint SpiSetCursors = 0x0057;

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool Icon;
            public int HotspotX;
            public int HotspotY;
            public IntPtr Mask;
            public IntPtr Color;
        }

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("user32.dll")]
        private static extern IntPtr CreateIconIndirect(ref IconInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr CopyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetSystemCursor(IntPtr cursor, uint id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoW(uint action, uint param, IntPtr value, uint winIni);
    }
}
