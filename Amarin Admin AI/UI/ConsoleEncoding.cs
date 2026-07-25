using System.Runtime.InteropServices;
using System.Text;

namespace Amarin.UI;

internal static class ConsoleEncoding
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    public static bool IsVirtualTerminalEnabled { get; private set; }

    public static void Configure()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetConsoleOutputCP(65001);
                SetConsoleCP(65001);
            }
            catch
            {
                // Best effort — manifest may already set UTF-8.
            }
        }

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = utf8;
        Console.OutputEncoding = utf8;
        IsVirtualTerminalEnabled = TryEnableVirtualTerminalProcessing();
        ResetTerminalState();
    }

    public static void WriteAnsi(string sequence)
    {
        if (!IsVirtualTerminalEnabled)
        {
            return;
        }

        try
        {
            Console.Write(sequence);
            Console.Out.Flush();
        }
        catch
        {
            // ignore console reset failures
        }
    }

    private static void ResetTerminalState()
    {
        try
        {
            // Recover from a previous crash while the alternate screen was active.
            WriteAnsi("\x1b[?1049l");
            Console.ResetColor();
            Console.CursorVisible = true;
            WriteAnsi("\x1b[0m");
            Console.Out.Flush();
        }
        catch
        {
            // ignore console reset failures
        }
    }

    private static bool TryEnableVirtualTerminalProcessing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        var enabled = false;

        foreach (var handleId in new[] { StdOutputHandle, StdErrorHandle })
        {
            var handle = GetStdHandle(handleId);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                continue;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                continue;
            }

            if (SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing))
            {
                enabled = true;
            }
        }

        return enabled;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}