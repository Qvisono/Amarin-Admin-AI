using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

/// <summary>Ряд диаграммы: название и по значению на каждую подпись; пропуск — null.</summary>
internal sealed record ChartSeries(string Name, IReadOnlyList<double?> Values);

/// <summary>
/// Диаграмма в ответе чата — блок <c>```chart</c> с одним объектом JSON: <c>type</c> (bar, line,
/// pie), <c>title</c>, <c>labels</c> и <c>series</c> из <c>{name, values}</c>.
/// </summary>
/// <remarks>
/// <para>
/// Данные лежат в тексте ответа, а рисует их лента своим элементом в цветах темы: картинка не
/// перекрашивалась бы со сменой темы и была бы ещё одной копией тех же чисел. Блок пишет модель,
/// обычно копируя готовый из <c>analyze_table</c>, поэтому разбор терпим к лишним запятым и
/// комментариям, а недописанный (ответ ещё идёт) или негодный блок остаётся блоком кода.
/// </para>
/// <para>
/// Пределы держат ленту лёгкой: больше точек на ширине сообщения всё равно не различить.
/// </para>
/// </remarks>
internal sealed record ChartSpec(string Type, string? Title, IReadOnlyList<string> Labels, IReadOnlyList<ChartSeries> Series)
{
    /// <summary>Язык блока кода, который лента рисует диаграммой.</summary>
    public const string FenceLanguage = "chart";

    public const string Bar = "bar";

    public const string Line = "line";

    public const string Pie = "pie";

    public const int MaxSeries = 8;

    public const int MaxPoints = 200;

    /// <summary>Кириллица подписей — буквами, а не кодами <c>\uXXXX</c>: блок копирует модель и читает человек.</summary>
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryParse(string? json, [NotNullWhen(true)] out ChartSpec? spec)
    {
        spec = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json, Lenient);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var type = (Text(root, "type") ?? Bar).Trim().ToLowerInvariant();
            if (type is not (Bar or Line or Pie) ||
                !root.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var names = labels.EnumerateArray().Take(MaxPoints).Select(Label).ToList();
            var list = new List<ChartSeries>();
            foreach (var item in series.EnumerateArray().Take(type == Pie ? 1 : MaxSeries))
            {
                if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                var numbers = values.EnumerateArray().Take(names.Count).Select(Number).ToList();
                while (numbers.Count < names.Count)
                {
                    numbers.Add(null);
                }

                list.Add(new ChartSeries(Text(item, "name") ?? "", numbers));
            }

            if (names.Count == 0 || list.Count == 0 || list.All(item => item.Values.All(value => value is null)))
            {
                return false;
            }

            spec = new ChartSpec(type, Text(root, "title"), names, list);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Тот же блок одной строкой JSON — его кладёт в ответ <c>analyze_table</c>.</summary>
    public string ToJson()
    {
        var root = new JsonObject { ["type"] = Type };
        if (!string.IsNullOrWhiteSpace(Title))
        {
            root["title"] = Title;
        }

        root["labels"] = new JsonArray([.. Labels.Select(label => (JsonNode?)JsonValue.Create(label))]);
        root["series"] = new JsonArray([.. Series.Select(item => (JsonNode?)new JsonObject
        {
            ["name"] = item.Name,
            ["values"] = new JsonArray([.. item.Values.Select(value => (JsonNode?)(value is { } number ? JsonValue.Create(Math.Round(number, 6)) : null))])
        })]);
        return root.ToJsonString(Readable);
    }

    /// <summary>Блок для ответа целиком, с оградой.</summary>
    public string ToFence() => "```" + FenceLanguage + "\n" + ToJson() + "\n```";

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Label(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => element.GetRawText()
    };

    /// <summary>Число или строка с числом; остальное — пропуск, а не ноль: ноль на диаграмме — тоже данные.</summary>
    private static double? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetDouble(out var number) && double.IsFinite(number) => number,
        JsonValueKind.String when double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) => parsed,
        _ => null
    };
}
