using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

public sealed class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required List<ChatMessage> Messages { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; init; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; init; } = 0.2;

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Stream { get; init; }

    /// <summary>
    /// Просит расход токенов у потокового ответа. Ставится только вместе с <see cref="Stream"/>:
    /// обычный запрос сообщает расход сам, а часть моделей без потока отвергает это поле.
    /// </summary>
    [JsonPropertyName("stream_options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StreamOptions? StreamOptions { get; init; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("reasoning")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReasoningConfig? Reasoning { get; init; }

    /// <summary>
    /// Надстройки Venice. <c>null</c> — и поля в теле запроса нет вовсе.
    /// </summary>
    /// <remarks>
    /// Тип обнуляемый, а не атрибут <c>JsonIgnore</c>: <see cref="VeniceJsonContext"/> и так
    /// пропускает <c>null</c>, а поле уходило на провод всегда лишь потому, что было
    /// необнуляемым с инициализатором. Другим провайдерам это поле незнакомо.
    /// </remarks>
    [JsonPropertyName("venice_parameters")]
    public VeniceParameters? VeniceParameters { get; init; }

    /// <summary>
    /// Просьба посчитать деньги. Только OpenRouter: без неё он не кладёт цену в <c>usage</c>,
    /// и списание в журнал трат не попало бы вовсе.
    /// </summary>
    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageAccounting? Usage { get; init; }

    /// <summary>
    /// Плагины OpenRouter: поиск в сети приходит сюда. У Venice того же добивается
    /// <c>venice_parameters.enable_web_search</c>.
    /// </summary>
    [JsonPropertyName("plugins")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RequestPlugin>? Plugins { get; init; }

    /// <summary>
    /// Чего хочет вызывающий. Не сериализуется: <see cref="VeniceClient"/> подгоняет значение под
    /// модель запроса (и запасную тоже) перед отправкой.
    /// </summary>
    [JsonIgnore]
    public ReasoningChoice? ReasoningChoice { get; init; }
}

/// <summary>Просьба к OpenRouter вернуть цену запроса вместе с токенами.</summary>
public sealed class UsageAccounting
{
    [JsonPropertyName("include")]
    public bool Include { get; init; } = true;
}

/// <summary>Плагин OpenRouter. Поиск в сети — <c>id: "web"</c>.</summary>
public sealed class RequestPlugin
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("max_results")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxResults { get; init; }

    /// <summary>Чем искать: <c>exa</c>, <c>parallel</c>, <c>perplexity</c>, <c>native</c>…</summary>
    /// <remarks>Пусто — решает OpenRouter. Цена у движков отличается десятикратно.</remarks>
    [JsonPropertyName("engine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Engine { get; init; }

    /// <summary>Режим выбранного движка: у каждого свой набор, и без движка он бессмыслен.</summary>
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }
}

public sealed class StreamOptions
{
    [JsonPropertyName("include_usage")]
    public bool IncludeUsage { get; init; } = true;
}

/// <summary>
/// Сколько токенов запрос стоил на деле. <see cref="PromptTokens"/> — весь прочитанный моделью
/// контекст (системный промпт, история, ответы инструментов) — ровно то, что показывает кольцо
/// контекста; своим подсчётом это можно лишь угадать.
/// </summary>
public sealed class VeniceUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; init; }

    /// <summary>
    /// Цена запроса в долларах. Так её сообщает OpenRouter — и только когда об этом попросили
    /// (<see cref="UsageAccounting"/>). Venice кладёт цену отдельным полем <c>cost</c> рядом,
    /// объектом с двумя валютами.
    /// </summary>
    [JsonPropertyName("cost")]
    public decimal? Cost { get; init; }
}

public sealed class VeniceParameters
{
    [JsonPropertyName("include_venice_system_prompt")]
    public bool IncludeVeniceSystemPrompt { get; init; }

    [JsonPropertyName("enable_web_search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnableWebSearch { get; init; }

    [JsonPropertyName("enable_web_citations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableWebCitations { get; init; }

    [JsonPropertyName("enable_x_search")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableXSearch { get; init; }

    [JsonPropertyName("disable_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DisableThinking { get; init; }

    [JsonPropertyName("strip_thinking_response")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StripThinkingResponse { get; init; }
}

/// <summary>
/// Вложенный объект <c>reasoning</c> запроса чата — им выключают размышление Venice
/// (<c>enabled: false</c>); сама сила идёт полем верхнего уровня.
/// </summary>
public sealed class ReasoningConfig
{
    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; init; }

    [JsonPropertyName("effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; init; }
}

public sealed class ChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Content { get; init; }

    /// <summary>Копия поля потокового кусочка, только для чтения; обратно в API не уходит.</summary>
    [JsonPropertyName("reasoning_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ReasoningContent { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// Ссылки, которыми OpenRouter подтверждает найденное в сети. Только для чтения: наружу
    /// сообщения собираются заново, и это поле в них не попадает.
    /// </summary>
    [JsonPropertyName("annotations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Annotations { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

public sealed class ToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required FunctionCall Function { get; init; }
}

public sealed class FunctionCall
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    [JsonConverter(typeof(ToolArgumentsJsonConverter))]
    public required string Arguments { get; init; }
}

public sealed class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required FunctionDefinition Function { get; init; }
}

public sealed class FunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("parameters")]
    public required JsonElement Parameters { get; init; }
}

public sealed class ChatCompletionResponse
{
    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; init; } = [];

    [JsonPropertyName("cost")]
    public VeniceCostResponse? Cost { get; init; }

    [JsonPropertyName("usage")]
    public VeniceUsage? Usage { get; init; }

    [JsonPropertyName("error")]
    public VeniceError? Error { get; init; }

    /// <summary>
    /// Во что обошёлся ответ — так, как назвал провайдер. <c>null</c> — цены не назвали или
    /// ответ бесплатный.
    /// </summary>
    /// <remarks>
    /// Venice называет цену своим полем <c>cost</c>, OpenRouter — внутри <c>usage</c>, и только
    /// если её просили (см. <c>UsageAccounting</c>). Правило одно на всех, кто читает цену ответа:
    /// по нему клиент списывает деньги в журнал трат, и по нему же маршрутизатор, заголовок,
    /// сводка, защитник и отчёт ставят свою строку в разбивку под сообщением. Прежде они читали
    /// только <c>cost</c>, и у слотов на OpenRouter их строки пропадали из разбивки, хотя деньги
    /// на график уходили.
    /// </remarks>
    public VeniceCost? ReportedCost() =>
        Cost is not null ? Cost.ToCost()
        : Usage?.Cost is { } usd and > 0m ? new VeniceCost { Usd = usd, HasData = true }
        : null;
}

public sealed class ChatChoice
{
    [JsonPropertyName("message")]
    public required ChatMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class VeniceError
{
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class ChatCompletionChunk
{
    [JsonPropertyName("choices")]
    public List<ChatChunkChoice> Choices { get; init; } = [];

    [JsonPropertyName("cost")]
    public VeniceCostResponse? Cost { get; init; }

    /// <summary>
    /// Приходит один раз, в последнем кусочке с пустым <see cref="Choices"/>. Кто читает его после
    /// выбора варианта из списка, не увидит его никогда.
    /// </summary>
    [JsonPropertyName("usage")]
    public VeniceUsage? Usage { get; init; }

    [JsonPropertyName("error")]
    public VeniceError? Error { get; init; }
}

public sealed class ChatChunkChoice
{
    [JsonPropertyName("delta")]
    public ChatMessageDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

public sealed class ChatMessageDelta
{
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    public JsonElement? Content { get; init; }

    /// <summary>
    /// Рассуждающие модели (grok-4-x) сначала шлют сюда размышление и только потом заполняют
    /// <see cref="Content"/>. disable_thinking/strip_thinking_response Venice для них не учитывает —
    /// поле приходит, просим мы его или нет.
    /// </summary>
    [JsonPropertyName("reasoning_content")]
    public JsonElement? ReasoningContent { get; init; }

    /// <summary>
    /// То же самое у OpenRouter — он называет поле короче. Два имени одного смысла: связать
    /// оба с одним свойством генератор кода не умеет, поэтому склейка живёт на чтении,
    /// в <see cref="ChatStreamAccumulator"/>.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public JsonElement? Reasoning { get; init; }

    [JsonPropertyName("tool_calls")]
    public List<ToolCallDelta>? ToolCalls { get; init; }
}

public sealed class ToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public FunctionCallDelta? Function { get; init; }
}

public sealed class FunctionCallDelta
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    [JsonConverter(typeof(ToolArgumentsJsonConverter))]
    public string? Arguments { get; init; }
}

public sealed class StreamedChatCompletion
{
    public string Text { get; init; } = "";

    /// <summary>
    /// Размышление из <c>reasoning_content</c>. Ответом показывается, только если модель больше
    /// ничего не дала, — см. <c>ChatEngine.StreamWithRetryAsync</c>.
    /// </summary>
    public string ReasoningText { get; init; } = "";

    /// <summary>
    /// Размышление, которое модель написала в <c>content</c> тегами вида <c>&lt;think&gt;</c>, а
    /// не отдельным каналом. Из <see cref="Text"/> уже вырезано.
    /// </summary>
    public string InlineReasoning { get; init; } = "";

    /// <summary>Время от первого кусочка до первого слова ответа.</summary>
    public TimeSpan ThinkingElapsed { get; init; }

    public List<ToolCall> ToolCalls { get; init; } = [];

    public string? FinishReason { get; init; }

    public VeniceCost Cost { get; init; } = VeniceCost.Zero;

    /// <summary>
    /// Контекст, прочитанный моделью в этом запросе, по счёту провайдера. Ноль — API промолчал, и
    /// вызывающий берёт прикидку, а не пустоту.
    /// </summary>
    public int PromptTokens { get; init; }

    public int TotalTokens { get; init; }

    public string Model { get; init; } = "";
}

public sealed class VeniceModelsListResponse
{
    [JsonPropertyName("data")]
    public List<VeniceModelInfo> Data { get; init; } = [];

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

public sealed class VeniceModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("object")]
    public string? Object { get; init; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("created")]
    public long? Created { get; init; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    [JsonPropertyName("model_spec")]
    public VeniceModelSpec? ModelSpec { get; init; }
}

public sealed class VeniceModelSpec
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("availableContextTokens")]
    public int? AvailableContextTokens { get; init; }

    [JsonPropertyName("offline")]
    public bool Offline { get; init; }

    [JsonPropertyName("beta")]
    public bool Beta { get; init; }

    [JsonPropertyName("traits")]
    public List<string>? Traits { get; init; }

    [JsonPropertyName("capabilities")]
    public VeniceModelCapabilities? Capabilities { get; init; }

    /// <summary>
    /// Цены модели (E2): у текстовых — <c>input</c>/<c>output</c>, доллары за миллион токенов,
    /// по спецификации Venice (<c>veniceai/api-docs</c>, <c>model_spec.pricing</c>). У моделей
    /// OpenRouter заполняется при сведении каталога, в тех же единицах.
    /// </summary>
    [JsonPropertyName("pricing")]
    public VeniceModelPricing? Pricing { get; init; }
}

/// <summary>Цены за миллион токенов. У картинок и речи там другие поля — их здесь не читаем.</summary>
public sealed class VeniceModelPricing
{
    [JsonPropertyName("input")]
    public VeniceUnitPrice? Input { get; init; }

    [JsonPropertyName("output")]
    public VeniceUnitPrice? Output { get; init; }
}

public sealed class VeniceUnitPrice
{
    /// <summary>
    /// Доллары. Числом по спецификации; разборщик терпит и строку: ошибка типа в одной цене не
    /// должна ронять весь список моделей.
    /// </summary>
    [JsonPropertyName("usd")]
    [JsonConverter(typeof(LenientDecimalConverter))]
    public decimal? Usd { get; init; }
}

/// <summary>
/// Десятичное из числа или строки; всё остальное — «не знаем». Каталог моделей важнее цены:
/// без него не открыть ни одного чата.
/// </summary>
public sealed class LenientDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number when reader.TryGetDecimal(out var number):
                return number;
            case JsonTokenType.String when decimal.TryParse(
                reader.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed):
                return parsed;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is { } number)
        {
            writer.WriteNumberValue(number);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public sealed class VeniceModelCapabilities
{
    [JsonPropertyName("optimizedForCode")]
    public bool OptimizedForCode { get; init; }

    [JsonPropertyName("supportsFunctionCalling")]
    public bool SupportsFunctionCalling { get; init; }

    [JsonPropertyName("supportsReasoning")]
    public bool SupportsReasoning { get; init; }

    [JsonPropertyName("supportsReasoningEffort")]
    public bool SupportsReasoningEffort { get; init; }

    /// <summary>
    /// False — <c>/chat/completions</c> не принимает инструменты вместе с <c>reasoning_effort</c>,
    /// отличным от none. Null — каталог не сказал, решаем по известным семействам.
    /// </summary>
    [JsonPropertyName("supportsReasoningEffortWithTools")]
    public bool? SupportsReasoningEffortWithTools { get; init; }

    [JsonPropertyName("reasoningEffortOptions")]
    public List<string>? ReasoningEffortOptions { get; init; }

    [JsonPropertyName("defaultReasoningEffort")]
    public string? DefaultReasoningEffort { get; init; }

    [JsonPropertyName("supportsVision")]
    public bool SupportsVision { get; init; }

    [JsonPropertyName("quantization")]
    public string? Quantization { get; init; }
}
/// <summary>
/// Запрос к рисованию Venice. Отдельно от запросов чата: у API другая форма, а рисующих моделей
/// нет в текстовом каталоге.
/// </summary>
public sealed class ImageGenerateRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("prompt")]
    public required string Prompt { get; init; }

    [JsonPropertyName("negative_prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NegativePrompt { get; init; }

    // Размер в пикселях и соотношение сторон — взаимоисключающие: диффузионные модели берут
    // width/height, а линейка nano-banana на Gemini — aspect_ratio с уровнем разрешения и пиксели
    // отвергает. Оба поля могут быть пустыми, чтобы на провод ушло только одно.
    [JsonPropertyName("width")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Height { get; init; }

    [JsonPropertyName("aspect_ratio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AspectRatio { get; init; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Resolution { get; init; }

    [JsonPropertyName("format")]
    public string Format { get; init; } = "png";

    /// <summary>Base64 в теле JSON, а не байтами, — чтобы сразу класть в сообщение.</summary>
    [JsonPropertyName("return_binary")]
    public bool ReturnBinary { get; init; }

    [JsonPropertyName("safe_mode")]
    public bool SafeMode { get; init; }

    [JsonPropertyName("hide_watermark")]
    public bool HideWatermark { get; init; } = true;
}

public sealed class ImageGenerateResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("images")]
    public List<string> Images { get; init; } = [];

    /// <summary>
    /// Та же форма, что у ответа чата: картинка попадает в счёт сообщения, как всё остальное. Если
    /// поле не пришло, вызывающий считает цену по изменению остатка.
    /// </summary>
    [JsonPropertyName("cost")]
    public VeniceCostResponse? Cost { get; init; }
}
