namespace Amarin.UI;

internal static class ConsoleOverlayLines
{
    public static int CaptureTop()
    {
        try
        {
            return Console.CursorTop;
        }
        catch
        {
            return 0;
        }
    }

    public static int CountLines(int startTop)
    {
        try
        {
            var endTop = Console.CursorTop;
            return Math.Max(endTop - startTop + 1, 1);
        }
        catch
        {
            return 1;
        }
    }

    public static void ClearFrom(int startTop)
    {
        Clear(CountLines(startTop));
    }

    public static void Clear(int lineCount)
    {
        if (lineCount <= 0)
        {
            return;
        }

        try
        {
            var width = Math.Max(Console.WindowWidth, 1);
            var cursorTop = Console.CursorTop;

            for (var i = 0; i < lineCount; i++)
            {
                var row = cursorTop - i;
                if (row < 0)
                {
                    break;
                }

                Console.SetCursorPosition(0, row);
                Console.Write(new string(' ', width));
            }

            var targetRow = Math.Max(0, cursorTop - lineCount + 1);
            Console.SetCursorPosition(0, targetRow);
            Console.Out.Flush();
        }
        catch
        {
            if (ConsoleEncoding.IsVirtualTerminalEnabled)
            {
                for (var i = 0; i < lineCount; i++)
                {
                    Console.Write("\x1b[1A\x1b[2K");
                }

                Console.Out.Flush();
            }
        }
    }
}