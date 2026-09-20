using System.Text.Json;
using System.Text.Json.Serialization;

namespace Amarin.Core;

// ───────────────────────── POST /batches ─────────────────────────

/// <summary>
/// Тело заявки в пакетную очередь OpenRouter.
/// </summary>
/// <remarks>
/// Порядок полей здесь — часть контракта, а не оформление: сервер разбирает тело потоком,
/// чтобы принимать заявки на тысячи запросов не буферизуя их целиком, и отвечает <c>400</c>,
/// если <c>requests</c> встретился раньше <c>endpoint</c> и <c>model</c>. Отсюда
/// <see cref="JsonPropertyOrderAttribute"/> на каждом поле: полагаться на порядок объявления
/// значило бы, что безобидная перестановка строк ломает запрос, а понять это по ответу
/// «400 Bad Request» нельзя.
/// </remarks>
internal sealed class OpenRouterBatchSubmit
{
    /// <summary>Форма запросов внутри заявки. У чата она одна.</summary>
    [JsonPropertyOrder(0)]
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }

    /// <summary>
    /// Модель для всех запросов заявки — слаг без пометки варианта.
    /// </summary>
    /// <remarks>
    /// Именно без неё: пакетность задана самим эндпоинтом, а <c>:batch</c> в каталоге лишь
    /// показывает половинную цену. Тело запроса внутри заявки модель либо опускает, либо
    /// повторяет ровно эту строку — разошлись, и заявку отвергнут.
    /// </remarks>
    [JsonPropertyOrder(1)]
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyOrder(2)]
    [JsonPropertyName("requests")]
    public required List<OpenRouterBatchItem> Requests { get; init; }
}

internal sealed class OpenRouterBatchItem
{
    /// <summary>Метка запроса: по ней результат находят в ответе очереди.</summary>
    [JsonPropertyName("custom_id")]
    public required string CustomId { get; init; }

    [JsonPropertyName("body")]
    public required ChatCompletionRequest Body { get; init; }
}

// ───────────────────────── GET /batches/{id} ─────────────────────────

internal sealed class OpenRouterBatchObject
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("request_counts")]
    public OpenRouterBatchCounts? RequestCounts { get; init; }

    /// <summary>Токены и цена всей заявки: у очереди цена одна на заявку, а не на запрос.</summary>
    [JsonPropertyName("usage")]
    public VeniceUsage? Usage { get; init; }

    [JsonPropertyName("results")]
    public List<OpenRouterBatchResult>? Results { get; init; }

    /// <summary>
    /// Отказ по всей заявке. Разбирается как <see cref="JsonElement"/>, а не готовым типом:
    /// вид этого поля нигде не описан, а разбор отказа — худшее место для второго отказа.
    /// </summary>
    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }
}

internal sealed class OpenRouterBatchCounts
{
    [JsonPropertyName("total")]
    public int? Total { get; init; }

    [JsonPropertyName("completed")]
    public int? Completed { get; init; }

    [JsonPropertyName("failed")]
    public int? Failed { get; init; }
}

internal sealed class OpenRouterBatchResult
{
    [JsonPropertyName("custom_id")]
    public string? CustomId { get; init; }

    [JsonPropertyName("response")]
    public OpenRouterBatchEnvelope? Response { get; init; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }
}

internal sealed class OpenRouterBatchEnvelope
{
    [JsonPropertyName("status_code")]
    public int? StatusCode { get; init; }

    /// <summary>Обычный ответ чата — тот же тип, что и у синхронного запроса.</summary>
    [JsonPropertyName("body")]
    public ChatCompletionResponse? Body { get; init; }
}

// ───────────────────────── правила очереди ─────────────────────────

/// <summary>
/// Всё, что программа знает о пакетной очереди OpenRouter помимо самих запросов: как назвать
/// модель, чего очередь не примет, как часто спрашивать о готовности и как прочитать итог.
/// </summary>
/// <remarks>
/// Отдельно от <see cref="VeniceClient"/>, потому что здесь нет ни сети, ни ключей: правила
/// проверяются тестами напрямую, без поддельного обработчика HTTP.
/// </remarks>
internal static class OpenRouterBatchPlan
{
    /// <summary>Форма запросов: у чата она одна.</summary>
    public const string ChatEndpoint = "/v1/chat/completions";

    /// <summary>Метка единственного запроса в заявке.</summary>
    public const string SingleRequestId = "amarin-1";

    /// <summary>Первый перерыв между опросами.</summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Потолок перерыва.
    /// </summary>
    /// <remarks>
    /// Полминуты — это 2880 опросов на полные сутки ожидания и не больше тридцати лишних
    /// секунд на заявке, которая управилась за минуту. Рост без потолка растянул бы перерыв
    /// до часов, и готовый ответ пролежал бы непрочитанным дольше, чем считался.
    /// </remarks>
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Сколько ждать перед следующим опросом.
    /// </summary>
    /// <remarks>
    /// Рост, а не постоянный шаг: короткие заявки заканчиваются за секунды, и спрашивать о них
    /// раз в полминуты значило бы дарить очереди эти полминуты; длинные идут часами, и частый
    /// опрос — это тысячи бессмысленных запросов подряд.
    /// </remarks>
    public static TimeSpan NextDelay(TimeSpan previous)
    {
        if (previous <= TimeSpan.Zero)
        {
            return FirstDelay;
        }

        var grown = TimeSpan.FromTicks((long)(previous.Ticks * 1.5));
        return grown > MaxDelay ? MaxDelay : grown;
    }

    /// <summary>
    /// Сколько ждать, пока принятая заявка станет видна очереди.
    /// </summary>
    /// <remarks>
    /// Между <c>202</c> и первым успешным <c>GET /batches/{id}</c> проходят секунды, и всё это
    /// время очередь отвечает 404 «Batch job not found» — заявка сохранена, но ещё не видна.
    /// Полторы минуты с запасом: меньше рискует оборвать ход на ровном месте, а больше
    /// означало бы, что на потерянной заявке человек ждёт молча дольше, чем стоит.
    /// </remarks>
    public static readonly TimeSpan VisibilityWindow = TimeSpan.FromSeconds(90);

    /// <summary>Статусы, после которых заявка уже не изменится.</summary>
    public static bool IsTerminal(string? status) =>
        Is(status, "completed") || Is(status, "failed") ||
        Is(status, "expired") || Is(status, "cancelled");

    public static bool IsCompleted(string? status) => Is(status, "completed");

    private static bool Is(string? status, string name) =>
        string.Equals(status?.Trim(), name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Идентификатор модели для заявки: без приставки провайдера и без пометки варианта.</summary>
    public static string SlugFor(string modelId) =>
        ModelRef.WithoutBatchVariant(ModelRef.Bare(modelId));

    /// <summary>
    /// Тело запроса, каким его примет очередь.
    /// </summary>
    /// <remarks>
    /// Снимается всё, что очередь отвергает или считает лишним. <c>stream</c> запрещён прямо:
    /// потока у заявки нет, ответ приезжает целиком. Плагин поиска очередь тоже отвергает —
    /// поиск в программе ходит отдельным инструментом и своим запросом, так что теряется здесь
    /// не поиск, а лишь надстройка над этим запросом. Просьба посчитать деньги не нужна: цену
    /// очередь сообщает по всей заявке сразу. Надстройки Venice сюда и не попадали бы, но
    /// снять их дешевле, чем доказывать это.
    /// </remarks>
    public static ChatCompletionRequest ToBatchBody(ChatCompletionRequest payload, string slug) =>
        new()
        {
            Model = slug,
            Messages = payload.Messages,
            Tools = payload.Tools,
            ToolChoice = payload.ToolChoice,
            Temperature = payload.Temperature,
            Stream = false,
            StreamOptions = null,
            ReasoningEffort = payload.ReasoningEffort,
            Reasoning = payload.Reasoning,
            VeniceParameters = null,
            Usage = null,
            Plugins = null
        };

    /// <summary>
    /// Чем эта переписка не годится для очереди, если не годится вовсе.
    /// </summary>
    /// <remarks>
    /// Вложения программа шлёт строкой <c>data:</c> с base64 — в синхронном запросе так и надо,
    /// а очередь отвергает такие части у всех провайдеров без исключения: картинку и документ
    /// она принимает только ссылкой, которую провайдер скачает сам. Отказ здесь, до заявки,
    /// стоит мгновения; отказ очереди приезжает минутами позже статусом <c>failed</c>, и
    /// человек всё это время ждёт ответа, которого не будет.
    /// </remarks>
    public static string? UnsupportedReason(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Content is not { ValueKind: JsonValueKind.Array } parts)
            {
                continue;
            }

            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (HasInlineData(part, "image_url", "url"))
                {
                    return "картинки";
                }

                if (HasInlineData(part, "file", "file_data"))
                {
                    return "документы";
                }
            }
        }

        return null;
    }

    private static bool HasInlineData(JsonElement part, string holder, string field) =>
        part.TryGetProperty(holder, out var inner) &&
        inner.ValueKind == JsonValueKind.Object &&
        inner.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text &&
        text.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ответ модели из готовой заявки — или понятный отказ, если ответа там нет.
    /// </summary>
    /// <remarks>
    /// Цена берётся у заявки целиком и приписывается ответу: у синхронного запроса её кладёт
    /// сам ответ, и весь учёт трат дальше по коду ищет её именно там.
    /// </remarks>
    public static ChatCompletionResponse ReadAnswer(OpenRouterBatchObject batch)
    {
        var result = batch.Results?.FirstOrDefault(
                         item => string.Equals(item.CustomId, SingleRequestId, StringComparison.Ordinal))
                     ?? batch.Results?.FirstOrDefault();

        if (result?.Error is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } failure)
        {
            throw new VeniceApiException(
                $"OpenRouter batch: запрос в заявке отказал — {Describe(failure)}");
        }

        var body = result?.Response?.Body
                   ?? throw new VeniceApiException(
                       "OpenRouter batch: заявка завершилась, но ответа в ней нет. " +
                       "Повторите запрос или выберите модель без пометки «(batch)».");

        if (body.Error is { } inner)
        {
            throw new VeniceApiException(inner.Message ?? "Unknown OpenRouter batch error.");
        }

        return body.Usage is null && batch.Usage is not null
            ? new ChatCompletionResponse
            {
                Choices = body.Choices,
                Cost = body.Cost,
                Usage = batch.Usage,
                Error = body.Error
            }
            : body;
    }

    /// <summary>Отказ заявки строкой: сообщение сервера, если оно там есть.</summary>
    public static string Describe(JsonElement? error)
    {
        if (error is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value)
        {
            return "причина не названа";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "причина не названа";
        }

        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
        {
            return message.GetString() ?? "причина не названа";
        }

        var raw = value.GetRawText();
        return raw.Length > 240 ? raw[..240] + "…" : raw;
    }
}
