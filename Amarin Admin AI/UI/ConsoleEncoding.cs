using System.Runtime.InteropServices;
using System.Text;

namespace Amarin.UI;

internal static class ConsoleEncoding
{
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
        ResetTerminalState();
    }

    private static void ResetTerminalState()
    {
        try
        {
            // Recover from a previous crash while the alternate screen was active.
            Console.Write("\x1b[?1049l\x1b[0m\x1b[?25h");
            Console.Out.Flush();
        }
        catch
        {
            // ignore console reset failures
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCP(uint codePage);
}