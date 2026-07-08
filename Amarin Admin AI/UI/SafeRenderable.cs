using Spectre.Console;
using Spectre.Console.Rendering;

namespace Amarin.UI;

internal static class SafeRenderable
{
    public static IRenderable FromMarkdown(string content)
    {
        try
        {
            return new Markup(MarkdownFormatter.ToSpectreMarkup(content));
        }
        catch
        {
            return new Text(content);
        }
    }

    public static void WriteAssistantPanel(string content, Func<string, IRenderable> buildBody)
    {
        try
        {
            ThreadSafeConsole.WriteAssistantPanel(buildBody(content));
        }
        catch
        {
            ThreadSafeConsole.WriteAssistantPanel(new Text(content));
        }
    }
}