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
