using System.Globalization;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Список моделей OpenAI-совместимого сервера или Anthropic — в том же виде, каким программа
/// описывает модели Venice.
/// </summary>
/// <remarks>
/// Ответ <c>GET /models</c> у провайдеров похож только скелетом: <c>data[].id</c> есть у всех, а
/// остальное — у кого как. OpenAI отдаёт голые идентификаторы, Gemini приписывает к ним
/// <c>models/</c>, Anthropic — <c>display_name</c>, контекст и возможности, перепродавцы иногда
/// кладут цены по образцу OpenRouter. Разбор поэтому терпимый: чего нет — того модель не знает,
/// а не отказ всего списка.
/// <para>
/// Возможности, которых сервер не называет, считаются есть: инструменты нужны программе всегда,
/// и без такого допущения <see cref="VeniceModelCatalog.FilterAgentic"/> выбросил бы весь список
/// OpenAI. Модели, которые заведомо не про разговор (речь, картинки, вложения-векторы), отсеиваются
/// по имени.
/// </para>
/// </remarks>
internal static class ModelListParser
{
    /// <summary>Куски имени модели, которая не ведёт разговор, а рисует, говорит или считает векторы.</summary>
    private static readonly string[] NotForChat =
    [
        "embed", "tts", "whisper", "transcri", "dall-e", "dalle", "image", "imagen", "moderation",
        "realtime", "audio", "speech", "rerank", "sora", "veo", "lyria", "guard"
    ];

    private static readonly string[] EffortLevels = ["minimal", "low", "medium", "high", "xhigh", "max"];

    public static IReadOnlyList<VeniceModelInfo> Parse(string json, LlmProvider provider)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data
                : default;

        var models = new List<VeniceModelInfo>();
        if (items.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                Text(item, "id") is not { Length: > 0 } rawId)
            {
                continue;
            }

            var id = rawId.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ? rawId["models/".Length..] : rawId;
            if (!IsForChat(id))
            {
                continue;
            }

            models.Add(ToModel(item, ModelRef.Qualify(provider, id)));
        }

        return models;
    }

    /// <summary>
    /// С какого идентификатора просить следующую страницу. <c>null</c> — страница последняя.
    /// </summary>
    /// <remarks>Листание по образцу Anthropic: <c>has_more</c> и <c>last_id</c>.</remarks>
    public static string? NextPageAfter(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty("has_more", out var more) && more.ValueKind == JsonValueKind.True
            ? Text(root, "last_id")
            : null;
    }

    /// <summary>Какой протокол говорит ответ: у Anthropic у модели есть <c>"type": "model"</c>.</summary>
    public static WireProtocol? ProtocolOf(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (Text(item, "type") == "model")
                {
                    return WireProtocol.AnthropicMessages;
                }

                if (Text(item, "object") == "model")
                {
                    return WireProtocol.OpenAiChat;
                }
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsForChat(string id)
    {
        foreach (var needle in NotForChat)
        {
            if (id.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static VeniceModelInfo ToModel(JsonElement item, string id)
    {
        var context = Number(item, "context_length") ?? Number(item, "context_window") ??
                      Number(item, "max_input_tokens") ?? Number(item, "input_token_limit") ??
                      Number(item, "inputTokenLimit");
        var output = Number(item, "max_tokens") ?? Number(item, "max_output_tokens") ??
                     Number(item, "output_token_limit") ?? Number(item, "outputTokenLimit");

        var capabilities = item.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
            ? caps
            : default;
        var efforts = Efforts(capabilities);
        var thinks = Supported(capabilities, "thinking") ?? efforts is { Count: > 0 };

        return new VeniceModelInfo
        {
            Id = id,
            Object = "model",
            Type = "text",
            OwnedBy = Text(item, "owned_by"),
            Created = Created(item),
            ContextLength = context,
            ModelSpec = new VeniceModelSpec
            {
                Name = Text(item, "display_name") ?? Text(item, "name"),
                Description = Text(item, "description"),
                AvailableContextTokens = context,
                MaxOutputTokens = output,
                Pricing = Pricing(item),
                Capabilities = new VeniceModelCapabilities
                {
                    SupportsFunctionCalling = true,
                    SupportsReasoning = thinks,
                    SupportsReasoningEffort = efforts is not { Count: 0 },
                    ReasoningEffortOptions = efforts,
                    SupportsVision = Supported(capabilities, "image_input") ?? false
                }
            }
        };
    }

    /// <summary>
    /// Уровни размышления из <c>capabilities.effort</c> Anthropic. <c>null</c> — сервер не сказал,
    /// и уровни общие; пустой список — сказал, что уровней нет.
    /// </summary>
    private static List<string>? Efforts(JsonElement capabilities)
    {
        if (capabilities.ValueKind != JsonValueKind.Object ||
            !capabilities.TryGetProperty("effort", out var effort) ||
            effort.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var levels = new List<string>();
        foreach (var level in EffortLevels)
        {
            if (Supported(effort, level) == true)
            {
                levels.Add(level);
            }
        }

        return levels;
    }

    /// <summary>Значение <c>{"supported": true}</c> у вложенного поля. <c>null</c> — поля нет.</summary>
    private static bool? Supported(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.Object &&
        node.TryGetProperty("supported", out var supported) &&
        supported.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? supported.GetBoolean()
            : null;

    /// <summary>
    /// Цены по образцу OpenRouter — строки, доллары за токен, — в единицах Venice: за миллион.
    /// </summary>
    private static VeniceModelPricing? Pricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var input = Decimal(pricing, "prompt");
        var output = Decimal(pricing, "completion");
        return input is >= 0m && output is >= 0m
            ? new VeniceModelPricing
            {
                Input = new VeniceUnitPrice { Usd = input * 1_000_000m },
                Output = new VeniceUnitPrice { Usd = output * 1_000_000m }
            }
            : null;
    }

    private static long? Created(JsonElement item)
    {
        if (item.TryGetProperty("created", out var created) && created.TryGetInt64(out var unix))
        {
            return unix;
        }

        return Text(item, "created_at") is { } text &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToUnixTimeSeconds()
            : null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static int? Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) && number > 0
            ? number
            : null;

    private static decimal? Decimal(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
