using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Amarin.Tools;

internal static class ClipboardNative
{
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint CfDib = 8;

    public static bool HasText() => IsFormatAvailable(CfUnicodeText);
    public static bool HasFiles() => IsFormatAvailable(CfHdrop);
    public static bool HasImage() => IsFormatAvailable(CfDib);

    public static string? TryGetText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static IReadOnlyList<string> TryGetFiles()
    {
        var files = new List<string>();
        if (!OpenClipboard(IntPtr.Zero))
        {
            return files;
        }

        try
        {
            var handle = GetClipboardData(CfHdrop);
            if (handle == IntPtr.Zero)
            {
                return files;
            }

            var count = DragQueryFile(handle, uint.MaxValue, null!, 0);
            var buffer = new StringBuilder(1024);
            for (uint i = 0; i < count; i++)
            {
                buffer.Clear();
                DragQueryFile(handle, i, buffer, buffer.Capacity);
                if (buffer.Length > 0)
                {
                    files.Add(buffer.ToString());
                }
            }

            return files;
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static Bitmap? TryGetBitmap()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return null;
        }

        try
        {
            var handle = GetClipboardData(CfDib);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                return DibToBitmap(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool IsFormatAvailable(uint format) =>
        IsClipboardFormatAvailable(format);

    private static Bitmap DibToBitmap(IntPtr dibPtr)
    {
        var bmi = Marshal.PtrToStructure<BitmapInfoHeader>(dibPtr);
        var pixelOffset = (int)bmi.HeaderSize + GetColorTableSize(bmi);
        var stride = ((bmi.Width * bmi.BitCount + 31) / 32) * 4;
        var pixelData = IntPtr.Add(dibPtr, pixelOffset);

        var bitmap = new Bitmap(bmi.Width, Math.Abs(bmi.Height), PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

        try
        {
            var topDown = bmi.Height < 0;
            var height = Math.Abs(bmi.Height);

            for (var y = 0; y < height; y++)
            {
                var srcY = topDown ? y : height - 1 - y;
                var srcRow = IntPtr.Add(pixelData, srcY * stride);
                var destRow = bmpData.Scan0 + y * bmpData.Stride;
                CopyRow(srcRow, destRow, bmi.Width, bmi.BitCount, bmpData.Stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }

        return bitmap;
    }

    private static int GetColorTableSize(BitmapInfoHeader bmi)
    {
        if (bmi.BitCount > 8)
        {
            return 0;
        }

        var colors = bmi.ColorsUsed != 0 ? (int)bmi.ColorsUsed : 1 << bmi.BitCount;
        return colors * 4;
    }

    private static void CopyRow(IntPtr srcRow, IntPtr destRow, int width, ushort bitCount, int destStride)
    {
        unsafe
        {
            var dest = (byte*)destRow;
            for (var x = 0; x < width; x++)
            {
                byte r, g, b, a = 255;
                switch (bitCount)
                {
                    case 32:
                        b = ((byte*)srcRow)[x * 4];
                        g = ((byte*)srcRow)[x * 4 + 1];
                        r = ((byte*)srcRow)[x * 4 + 2];
                        a = ((byte*)srcRow)[x * 4 + 3];
                        break;
                    case 24:
                        b = ((byte*)srcRow)[x * 3];
                        g = ((byte*)srcRow)[x * 3 + 1];
                        r = ((byte*)srcRow)[x * 3 + 2];
                        break;
                    default:
                        r = g = b = 0;
                        break;
                }

                dest[x * 4] = b;
                dest[x * 4 + 1] = g;
                dest[x * 4 + 2] = r;
                dest[x * 4 + 3] = a;
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, int cch);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint HeaderSize;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint ImageSize;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }
}