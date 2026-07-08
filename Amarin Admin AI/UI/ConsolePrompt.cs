using Spectre.Console;

namespace Amarin.UI;

public sealed class ConsolePrompt
{
    private const string PrimaryPrompt = "Вы › ";

    public static void PrepareForInput() => ConsoleInputRestore.PrepareForInput();

    public static string ReadSimpleLine(string promptMarkup)
    {
        ConsoleInputRestore.Restore();
        AnsiConsole.Markup(promptMarkup);
        return Console.ReadLine() ?? string.Empty;
    }

    public string? ReadLine()
    {
        ConsoleInputRestore.Restore();
        Console.Out.Flush();
        Console.Write(PrimaryPrompt);
        Console.Out.Flush();

        var text = Console.ReadLine();
        if (text is null)
        {
            return null;
        }

        if (text.Trim() == "/")
        {
            var command = CommandPalette.Run();
            return command ?? string.Empty;
        }

        return text;
    }
}