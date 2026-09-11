using Amarin.Tools;

namespace Amarin.Core;

internal static class ChatToolPreview
{
    public static string Summarize(ToolResult result)
    {
        var output = result.Output ?? "";
        if (string.IsNullOrWhiteSpace(output))
        {
            return result.Success ? "готово" : "ошибка";
        }

        var lines = output
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("---", StringComparison.Ordinal))
            .ToList();

        if (lines.Count == 0)
        {
            return result.Success ? "готово" : "ошибка";
        }

        var first = ToSingleLine(lines[0], 70);
        return lines.Count == 1 ? first : $"{first} (+ещё {lines.Count - 1})";
    }

    /// <summary>
    /// The output as the journal shows it: whole, but capped.
    /// </summary>
    /// <remarks>
    /// A chat file already carries base64 images; letting a directory listing of ten thousand
    /// lines in beside them would make conversations that no longer open quickly. The cap is well
    /// past anything a person reads in one sitting, and the summary above still says what happened.
    /// </remarks>
    public static string ForJournal(ToolResult result)
    {
        const int maxChars = 4_000;
        var output = result.Output ?? "";
        return output.Length <= maxChars
            ? output
            : output[..maxChars] + "\n… [обрезано для журнала]";
    }

    public static string FormatForApi(ToolResult result)
    {
        const int maxChars = 12_000;
        var output = result.Success ? result.Output : $"ERROR: {result.Output}";
        return output.Length <= maxChars
            ? output
            : output[..maxChars] + "\n... [обрезано для контекста API]";
    }

    private static string ToSingleLine(string text, int maxLength)
    {
        var line = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        while (line.Contains("  ", StringComparison.Ordinal))
        {
            line = line.Replace("  ", " ", StringComparison.Ordinal);
        }

        return line.Length <= maxLength ? line : line[..maxLength] + "…";
    }
}
