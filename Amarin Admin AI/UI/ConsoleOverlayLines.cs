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

    public static void Clear(int lineCount)
    {
        if (lineCount <= 0)
        {
            return;
        }

        for (var i = 0; i < lineCount; i++)
        {
            Console.Write("\x1b[1A\x1b[2K");
        }

        Console.Out.Flush();
    }
}