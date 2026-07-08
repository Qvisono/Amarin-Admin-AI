namespace Amarin.UI;

internal static class ConsoleInputRestore
{
    private static readonly string[] SpinnerFrames =
    [
        "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"
    ];

    public static void PrepareForInput() => Restore(clearSpinnerLine: false);

    public static void Restore(bool clearSpinnerLine = false)
    {
        try
        {
            if (clearSpinnerLine)
            {
                var width = TryGetWindowWidth();
                Console.Write('\r');
                Console.Write(new string(' ', Math.Min(width - 1, 160)));
                Console.Write('\r');
            }

            Console.Write("\x1b[0m\x1b[?25h");
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch
        {
            // ignore console restore failures
        }
    }

    public static Task<T> RunWithSpinnerAsync<T>(string message, Func<Task<T>> action) =>
        Task.FromResult(RunWithSpinner(message, action));

    public static T RunWithSpinner<T>(string message, Func<Task<T>> action)
    {
        Restore();
        Console.Out.Flush();

        var work = Task.Run(action);
        var frame = 0;

        void Tick()
        {
            WriteSpinnerLine(message, SpinnerFrames[frame++ % SpinnerFrames.Length]);
        }

        try
        {
            Console.Write("\x1b[?25l");
            Tick();

            while (!work.IsCompleted)
            {
                Thread.Sleep(40);
                Tick();
            }

            return work.GetAwaiter().GetResult();
        }
        finally
        {
            Restore(clearSpinnerLine: true);
            Console.WriteLine();
            Console.Out.Flush();
        }
    }

    private static void WriteSpinnerLine(string message, string frame)
    {
        Console.Write($"\r\x1b[36m{message}\x1b[0m {frame}  ");
        Console.Out.Flush();
    }

    private static int TryGetWindowWidth()
    {
        try
        {
            return Math.Max(Console.WindowWidth, 40);
        }
        catch
        {
            return 120;
        }
    }
}