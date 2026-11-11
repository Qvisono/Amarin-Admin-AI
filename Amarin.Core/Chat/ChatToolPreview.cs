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
    /// Вывод, каким его показывает журнал: целиком, но с потолком.
    /// </summary>
    /// <remarks>
    /// Список папки на десять тысяч строк в файле чата сделал бы переписки, которые долго
    /// открываются. Потолок далеко за пределами того, что человек прочтёт за раз, а краткий итог
    /// выше всё равно говорит, что случилось.
    /// </remarks>
    public static string ForJournal(ToolResult result)
    {
        const int maxChars = 4_000;
        var output = result.Output ?? "";
        return output.Length <= maxChars
            ? output
            : output[..maxChars] + "\n… [обрезано для журнала]";
    }

    /// <summary>Сколько знаков вывода инструмента уходит модели.</summary>
    internal const int ApiLimit = 12_000;

    /// <summary>
    /// Ответ агента чату уходит целиком (1.38.0-beta.4): обрезанный отчёт чат переспрашивал через
    /// <c>message_agent</c>, и это стоило лишнего прогона агента.
    /// </summary>
    public static bool IsAgentReport(string toolName) =>
        toolName is InitAgentTool.ToolName or MessageAgentTool.ToolName or AgentRefusedTool.ToolName;

    /// <summary>Модель увидит этот вывод обрезанным (D10): лента помечает такой вызов.</summary>
    public static bool IsCallTruncatedForApi(string toolName, ToolResult result) =>
        !IsAgentReport(toolName) && IsTruncatedForApi(result);

    public static bool IsTruncatedForApi(ToolResult result) =>
        (result.Success ? result.Output?.Length ?? 0 : (result.Output?.Length ?? 0) + "ERROR: ".Length) > ApiLimit;

    public static string FormatCallForApi(string toolName, ToolResult result) =>
        IsAgentReport(toolName) ? Text(result) : FormatForApi(result);

    private static string Text(ToolResult result) => result.Success ? result.Output : $"ERROR: {result.Output}";

    public static string FormatForApi(ToolResult result)
    {
        const int maxChars = ApiLimit;
        var output = Text(result);
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
