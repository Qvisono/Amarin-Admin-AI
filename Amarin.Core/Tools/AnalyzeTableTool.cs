using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Анализ таблицы CSV или Excel: отбор строк, группировка и итоги по всем строкам файла, а по
/// просьбе — готовый блок диаграммы для ответа.
/// </summary>
/// <remarks>
/// Только чтение: файл не меняется. Модель получает итог, посчитанный программой (<see cref="TableQuery"/>),
/// и не складывает строки сама — на сотнях строк она ошибается и не видит того, что за окном чтения.
/// </remarks>
public sealed class AnalyzeTableTool : ITool
{
    public string Name => "analyze_table";

    public string Description =>
        "Filter, group and total a CSV, TSV or Excel table over all of its rows and get the result as a table. The first " +
        "row is the header; a column is named by its header or by its column letter. filter keeps the rows that pass every " +
        "condition; group_by with aggregate gives one row per group, aggregate alone gives totals over the kept rows, and " +
        "without both the kept rows themselves come back (columns picks which). Numbers are read as shown, with spaces " +
        "between thousands, a decimal comma, percent and currency signs. sort orders the result by its column names; totals " +
        "adds a Total row. chart also returns a chart block of the result for your reply: put it in unchanged. The file is " +
        "not changed. Relative paths go to the user's Downloads folder.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "CSV, TSV or Excel file, or an amarin-attachment: handle" },
            "sheet": { "type": "string", "description": "Excel: sheet name (default: the first sheet with values)" },
            "filter": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "column": { "type": "string" },
                  "op": { "type": "string", "enum": ["eq", "ne", "gt", "ge", "lt", "le", "contains", "not_contains", "empty", "not_empty"] },
                  "value": { "type": "string" }
                },
                "required": ["column", "op"]
              }
            },
            "group_by": { "type": "array", "items": { "type": "string" } },
            "aggregate": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "column": { "type": "string", "description": "Not needed for count of rows" },
                  "fn": { "type": "string", "enum": ["sum", "avg", "min", "max", "count", "distinct"] }
                },
                "required": ["fn"]
              }
            },
            "columns": { "type": "array", "items": { "type": "string" }, "description": "Columns to show when rows come back" },
            "sort": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "column": { "type": "string", "description": "A column of the result" },
                  "desc": { "type": "boolean" }
                },
                "required": ["column"]
              }
            },
            "limit": { "type": "integer", "description": "Result rows to show at most (default 100)" },
            "totals": { "type": "boolean", "description": "Add a Total row" },
            "chart": {
              "type": "object",
              "properties": {
                "type": { "type": "string", "enum": ["bar", "line", "pie"] },
                "title": { "type": "string" },
                "label": { "type": "string", "description": "Result column for the labels (default: the first)" },
                "values": { "type": "array", "items": { "type": "string" }, "description": "Result columns to draw (default: every numeric one)" }
              },
              "required": ["type"]
            }
          },
          "required": ["path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Analyze(arguments), cancellationToken);

    private static ToolResult Analyze(JsonElement arguments)
    {
        if (!FileToolPaths.TryResolveSource(FileToolPaths.String(arguments, "path"), out var source, out var error))
        {
            return ToolResult.Fail(error ?? "Invalid path.");
        }

        if (!source.InMemory)
        {
            if (SensitivePaths.IsSensitive(source.Path, out var secret))
            {
                return ToolResult.Fail(secret);
            }

            if (!File.Exists(source.Path))
            {
                return ToolResult.Fail($"File not found: {source.Path}. Pass the full path of an existing table.");
            }
        }

        try
        {
            var table = TableQuery.Load(source, FileToolPaths.String(arguments, "sheet"));
            var request = new TableRequest(
                [.. Items(arguments, "filter").Select(item => new TableFilter(
                    Required(item, "column", "filter"),
                    (FileToolPaths.String(item, "op") ?? "eq").Trim().ToLowerInvariant(),
                    Value(item)))],
                Strings(arguments, "group_by"),
                [.. Items(arguments, "aggregate").Select(item => new TableAggregate(
                    FileToolPaths.String(item, "column"),
                    Required(item, "fn", "aggregate").Trim().ToLowerInvariant()))],
                Strings(arguments, "columns"),
                [.. Items(arguments, "sort").Select(item => new TableSort(Required(item, "column", "sort"), FileToolPaths.Flag(item, "desc")))],
                FileToolPaths.Int(arguments, "limit") ?? TableQuery.DefaultLimit,
                FileToolPaths.Flag(arguments, "totals"));
            var result = TableQuery.Run(table, request);
            var report = new StringBuilder(TableQuery.Format(result));

            if (arguments.TryGetProperty("chart", out var chart) && chart.ValueKind == JsonValueKind.Object)
            {
                var spec = TableQuery.Chart(
                    result,
                    (FileToolPaths.String(chart, "type") ?? ChartSpec.Bar).Trim().ToLowerInvariant(),
                    FileToolPaths.String(chart, "title"),
                    FileToolPaths.String(chart, "label"),
                    Strings(chart, "values"));
                report.Append("\n\nChart block for your reply - put it in unchanged:\n").Append(spec.ToFence());
            }

            return ToolResult.Ok(report.ToString());
        }
        catch (DocumentException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"The file could not be read: {ex.Message}. If it is open in another program, ask the user to close it.");
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    private static List<string> Strings(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.Array => [.. value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString()).Where(item => item.Length > 0)],
                JsonValueKind.String when value.GetString() is { Length: > 0 } single => [single],
                _ => []
            }
            : [];

    /// <summary>Значение условия: строкой или числом — модели пишут по-разному.</summary>
    private static string? Value(JsonElement item) =>
        item.TryGetProperty("value", out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText()
            }
            : null;

    private static string Required(JsonElement item, string name, string where) =>
        FileToolPaths.String(item, name) is { Length: > 0 } value
            ? value
            : throw new DocumentException($"Every {where} item needs {name}.");
}
