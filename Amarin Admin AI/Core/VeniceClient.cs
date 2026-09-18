using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

public sealed class VeniceClient
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private string _primaryModel;

    public VeniceBalance? LastBalance { get; private set; }

    public VeniceCost RequestCost { get; private set; } = VeniceCost.Zero;

    /// <summary>Guards the running total: parallel tool calls share one client.</summary>
    private readonly Lock _costGate = new();

    /// <summary>
    /// Чем пометить следующее списание, если это не просто ответ модели.
    /// </summary>
    /// <remarks>
    /// AsyncLocal, а не поле: клиент общий, а инструменты раунда работают параллельно — общее
    /// поле они перетоптали бы, и поиск в сети оказался бы записан на чужой запрос. Здесь же
    /// пометка живёт ровно в той цепочке вызовов, которая её поставила.
    /// </remarks>
    private static readonly AsyncLocal<string?> ChargeLabel = new();

    /// <summary>Пометка для журнала трат: своя, если её поставили, иначе модель хода.</summary>
    private static string ChargeSku(string model) =>
        string.IsNullOrWhiteSpace(ChargeLabel.Value) ? model : ChargeLabel.Value!;

    /// <summary>
    /// Помечает всё, что спишется внутри области, служебной статьёй расхода.
    /// </summary>
    /// <remarks>
    /// Сводки, заголовки, маршрутизатор, защита, перевод интерфейса и разъяснения платятся
    /// токенами обычной модели, и без пометки их деньги сливаются в журнале со строкой этой
    /// модели: на вопрос «сколько ушло на сводки» ответить нечем.
    /// <para>
    /// Прежнее значение возвращается на место, а не обнуляется: области вкладываются — поиск
    /// в сети умеет случиться внутри служебного запроса, — и обнуление стёрло бы внешнюю пометку
    /// до конца вызова.
    /// </para>
    /// </remarks>
    /// <param name="sku">Статья расхода из <see cref="VeniceSku"/>.</param>
    public static IDisposable ChargeAs(string sku)
    {
        var previous = ChargeLabel.Value;
        ChargeLabel.Value = sku;
        return new ChargeScope(previous);
    }

    private sealed class ChargeScope : IDisposable
    {
        private readonly string? _previous;
        private bool _closed;

        public ChargeScope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            ChargeLabel.Value = _previous;
        }
    }

    public event Action<string, string>? ModelFallback;

    /// <summary>
    /// Looks up <c>/models</c> capabilities so fallback can drop <c>reasoning_effort</c> on
    /// models that reject it (Grok). Null until the catalogue has been loaded.
    /// </summary>
    public Func<string, VeniceModelInfo?>? ResolveModelInfo { get; set; }

    /// <summary>
    /// Модель по умолчанию — та, с которой запустилось приложение. Ход чата ею не пользуется:
    /// у него своя, в <see cref="VeniceTurnContext"/>. Раньше рядом стоял и сеттер, общий на всё
    /// приложение, — два одновременных хода перетоптали бы друг другу модель.
    /// </summary>
    public string ActiveModel => _options.Model;

    /// <remarks>
    /// BaseAddress ставится один раз на клиента и остаётся общим: клиент делят с генератором
    /// заголовка и сводкой, адрес у них тот же. А вот <c>Authorization</c> здесь больше не
    /// ставится — см. <see cref="Request"/>.
    /// </remarks>
    public VeniceClient(HttpClient http, AgentOptions options)
    {
        _http = http;
        _options = options;
        _primaryModel = options.Model;
        _http.BaseAddress ??= new Uri(_options.BaseUrl.TrimEnd('/') + "/");
    }

    /// <summary>
    /// Запрос к Venice вместе с ключом. Единственное место, где программа предъявляет ключ.
    /// </summary>
    /// <remarks>
    /// Заголовок садится на сам запрос, а не на <c>DefaultRequestHeaders</c> клиента. Так было
    /// раньше, и выходило двумя бедами сразу: клиент нёс <c>Bearer</c> в любой запрос, куда бы
    /// тот ни шёл (GitHub отвечал на чужой ключ 401, а сам ключ уезжал на посторонний сервер),
    /// и присваивание через <c>??=</c> намертво запоминало первый ключ — сменить его у живого
    /// клиента было нельзя. Правило «для нового адресата — свой клиент через
    /// <see cref="HttpClients.Create"/>» остаётся в силе: у клиентов разные таймауты и
    /// представление.
    /// </remarks>
    /// <param name="apiKeyOverride">
    /// Чужой ключ — когда страница настроек спрашивает баланс и траты ключа, который сейчас
    /// не активен. Пусто — берётся активный.
    /// </param>
    private HttpRequestMessage Request(HttpMethod method, string path, string? apiKeyOverride = null)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        var key = string.IsNullOrWhiteSpace(apiKeyOverride) ? _options.ApiKey : apiKeyOverride;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        return request;
    }

    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        VeniceParameters veniceParameters,
        CancellationToken cancellationToken = default,
        ReasoningChoice? reasoning = null) =>
        CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = model,
            Messages = messages.ToList(),
            Tools = tools,
            ToolChoice = toolChoice,
            VeniceParameters = veniceParameters,
            ReasoningChoice = reasoning
        }, prepareMessages: true, cancellationToken);

    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default) =>
        CreateChatCompletionAsync(request, prepareMessages: false, cancellationToken);

    private async Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        bool prepareMessages,
        CancellationToken cancellationToken)
    {
        // Метод берёт модель параметром и кладёт её в запрос — значит и перебор обязан начинаться
        // с неё. Раньше он стартовал с поля клиента, то есть с модели, которую никто не просил.
        var startingModel = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;
        VeniceApiException? lastOverload = null;

        foreach (var model in VeniceModelFallback.GetModelsFrom(startingModel, _primaryModel))
        {
            try
            {
                var result = await SendChatCompletionAsync(request, model, prepareMessages, cancellationToken)
                    .ConfigureAwait(false);

                if (!model.Equals(startingModel, StringComparison.OrdinalIgnoreCase))
                {
                    _options.Model = model;
                    ModelFallback?.Invoke(startingModel, model);
                }

                return result;
            }
            catch (VeniceApiException ex) when (VeniceModelFallback.IsModelOverloaded(ex))
            {
                lastOverload = ex;
            }
        }

        throw lastOverload
            ?? new VeniceApiException("Все модели в цепочке fallback перегружены. Повторите запрос позже.");
    }

    /// <param name="model">Модель этого хода. Съезжает сама, если сработал fallback.</param>
    /// <param name="primaryModel">
    /// Модель, которую ход запросил изначально, — корень цепочки fallback. Отдельно от
    /// <paramref name="model"/>: без неё перебор после отказа начался бы с уже упавшей модели.
    /// </param>
    public Task<StreamedChatCompletion> StreamChatCompletionAsync(
        string model,
        string primaryModel,
        IReadOnlyList<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        VeniceParameters veniceParameters,
        Action<string>? onText,
        CancellationToken cancellationToken = default,
        ReasoningChoice? reasoning = null) =>
        StreamChatCompletionAsync(new ChatCompletionRequest
        {
            Model = model,
            Messages = messages.ToList(),
            Tools = tools,
            ToolChoice = toolChoice,
            Stream = true,
            VeniceParameters = veniceParameters,
            ReasoningChoice = reasoning
        }, primaryModel, prepareMessages: true, onText, cancellationToken);

    private async Task<StreamedChatCompletion> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        string primaryModel,
        bool prepareMessages,
        Action<string>? onText,
        CancellationToken cancellationToken)
    {
        var startingModel = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;
        VeniceApiException? lastOverload = null;

        foreach (var model in VeniceModelFallback.GetModelsFrom(startingModel, primaryModel))
        {
            try
            {
                var result = await SendChatCompletionStreamingAsync(
                        request, model, prepareMessages, onText, cancellationToken)
                    .ConfigureAwait(false);

                if (!model.Equals(startingModel, StringComparison.OrdinalIgnoreCase))
                {
                    // Модель клиента не трогаем: ходов может идти несколько, и сработавшую
                    // каждый запоминает сам — по StreamedChatCompletion.Model.
                    ModelFallback?.Invoke(startingModel, model);
                }

                return result;
            }
            catch (VeniceApiException ex) when (VeniceModelFallback.IsModelOverloaded(ex))
            {
                lastOverload = ex;
            }
        }

        throw lastOverload
            ?? new VeniceApiException("Все модели в цепочке fallback перегружены. Повторите запрос позже.");
    }

    private async Task<ChatCompletionResponse> SendChatCompletionAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request, model, prepareMessages, stream: false);
        using var response = await PostCompletionAsync(payload, model, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = ExtractErrorMessage(body);
            if (NoteToolGrammarFailure(payload, model, error, out var friendly))
            {
                throw new VeniceApiException(friendly);
            }

            if (ShouldRetryToolsEffortConflict(payload, model, error))
            {
                payload = WithReasoningEffort(payload, ReasoningPolicy.None);
                using var retry = await PostCompletionAsync(payload, model, cancellationToken)
                    .ConfigureAwait(false);
                body = await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!retry.IsSuccessStatusCode)
                {
                    throw new VeniceApiException(
                        $"Venice API error ({(int)retry.StatusCode}): {ExtractErrorMessage(body)}");
                }

                return ReadCompletion(body, model);
            }

            throw new VeniceApiException($"Venice API error ({(int)response.StatusCode}): {error}");
        }

        return ReadCompletion(body, model);
    }

    private ChatCompletionResponse ReadCompletion(string body, string model)
    {
        ChatCompletionResponse result;
        try
        {
            result = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.ChatCompletionResponse)
                ?? throw new VeniceApiException("Empty response from Venice API.");
        }
        catch (JsonException ex)
        {
            var preview = string.IsNullOrWhiteSpace(body)
                ? "(empty body)"
                : body.Length > 240 ? body[..240] + "…" : body;
            throw new VeniceApiException(
                $"Venice API returned non-JSON response ({ex.Message}). Body: {preview}");
        }

        if (result.Error is not null)
        {
            throw new VeniceApiException(result.Error.Message ?? "Unknown Venice API error.");
        }

        RecordCost(result, ChargeSku(model));
        return result;
    }

    private async Task<StreamedChatCompletion> SendChatCompletionStreamingAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        Action<string>? onText,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request, model, prepareMessages, stream: true);
        var response = await PostCompletionAsync(payload, model, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            response.Dispose();
            var error = ExtractErrorMessage(errorBody);
            if (NoteToolGrammarFailure(payload, model, error, out var friendly))
            {
                throw new VeniceApiException(friendly);
            }

            if (!ShouldRetryToolsEffortConflict(payload, model, error))
            {
                throw new VeniceApiException($"Venice API error ({status}): {error}");
            }

            payload = WithReasoningEffort(payload, ReasoningPolicy.None);
            response = await PostCompletionAsync(payload, model, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retryBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                status = (int)response.StatusCode;
                response.Dispose();
                throw new VeniceApiException($"Venice API error ({status}): {ExtractErrorMessage(retryBody)}");
            }
        }

        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var accumulator = new ChatStreamAccumulator();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0 || line[0] == ':')
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = line[5..].TrimStart();
                if (data.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                ChatCompletionChunk chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize(data, VeniceJsonContext.Default.ChatCompletionChunk)
                            ?? new ChatCompletionChunk();
                }
                catch (JsonException ex)
                {
                    var preview = data.Length > 240 ? data[..240] + "…" : data;
                    throw new VeniceApiException(
                        $"Venice API returned non-JSON stream chunk ({ex.Message}). Body: {preview}");
                }

                // Отказ грамматики вызовов приезжает по потоку с кодом 200, а не четырёхсоткой:
                // ветка выше про неуспешный HTTP его не видит, и в чат попадал кусок грамматики
                // на пол-экрана. Ловим до Apply — накопитель не знает, какая это была модель.
                if (chunk.Error is { } chunkError &&
                    NoteToolGrammarFailure(payload, model, chunkError.Message ?? "", out var streamFriendly))
                {
                    throw new VeniceApiException(streamFriendly);
                }

                var added = accumulator.Apply(chunk);
                if (added)
                {
                    onText?.Invoke(accumulator.Text);
                }
            }

            if (accumulator.Cost.HasData)
            {
                AddCost(accumulator.Cost, ChargeSku(model));
            }

            return new StreamedChatCompletion
            {
                Text = accumulator.Text,
                ReasoningText = accumulator.ReasoningText,
                InlineReasoning = accumulator.InlineReasoning,
                ThinkingElapsed = accumulator.ThinkingElapsed,
                ToolCalls = accumulator.BuildToolCalls(),
                FinishReason = accumulator.FinishReason,
                Cost = accumulator.Cost,
                PromptTokens = accumulator.PromptTokens,
                TotalTokens = accumulator.TotalTokens,
                Model = model
            };
        }
    }

    private async Task<HttpResponseMessage> PostCompletionAsync(
        ChatCompletionRequest payload,
        string model,
        CancellationToken cancellationToken)
    {
        var serializeWatch = Stopwatch.StartNew();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, VeniceJsonContext.Default.ChatCompletionRequest);
        var serializeMs = serializeWatch.ElapsedMilliseconds;

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var httpRequest = Request(HttpMethod.Post, "chat/completions");
        httpRequest.Content = content;

        var httpWatch = Stopwatch.StartNew();
        var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        UpdateBalanceFromHeaders(response);
        PerfLog.Write(
            $"venice serialize_ms={serializeMs} http_ms={httpWatch.ElapsedMilliseconds} status={(int)response.StatusCode} model={model}");
        return response;
    }

    /// <summary>
    /// Ловит отказ компилятора грамматики вызовов и подменяет его понятной строкой.
    /// </summary>
    /// <remarks>
    /// Повтора без инструментов здесь нет намеренно: программа тем и занята, что даёт модели
    /// управлять компьютером, и ход без инструментов был бы не починкой, а тихой подменой —
    /// модель ответила бы текстом о действиях, которых не совершала. Вместо этого модель
    /// запоминается и уходит из списка, а человек получает строку о том, что делать.
    /// </remarks>
    private static bool NoteToolGrammarFailure(
        ChatCompletionRequest payload,
        string model,
        string error,
        out string friendly)
    {
        friendly = "";
        if (payload.Tools is not { Count: > 0 } || !VeniceToolSupport.IsToolGrammarFailure(error))
        {
            return false;
        }

        VeniceToolSupport.Remember(model);
        friendly = VeniceToolSupport.FailureText(model);
        return true;
    }

    private static bool ShouldRetryToolsEffortConflict(
        ChatCompletionRequest payload,
        string model,
        string error)
    {
        if (!ReasoningPolicy.IsToolsReasoningEffortConflict(error))
        {
            return false;
        }

        ReasoningPolicy.RememberToolsBlockEffort(model);
        return !string.Equals(payload.ReasoningEffort, ReasoningPolicy.None, StringComparison.OrdinalIgnoreCase);
    }

    private static ChatCompletionRequest WithReasoningEffort(ChatCompletionRequest request, string? effort) =>
        new()
        {
            Model = request.Model,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            Stream = request.Stream,
            ReasoningEffort = effort,
            Reasoning = request.Reasoning,
            VeniceParameters = request.VeniceParameters,
            ReasoningChoice = request.ReasoningChoice
        };

    private ChatCompletionRequest BuildPayload(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream)
    {
        var venice = request.VeniceParameters;
        var effort = request.ReasoningEffort;
        var reasoning = request.Reasoning;

        var hasTools = request.Tools is { Count: > 0 };
        var info = ResolveModelInfo?.Invoke(model);
        if (request.ReasoningChoice is { } choice)
        {
            var wire = ReasoningPolicy.ToWire(info, choice, hasTools, model);
            effort = wire.ReasoningEffort;
            reasoning = null;
            venice = new VeniceParameters
            {
                IncludeVeniceSystemPrompt = venice.IncludeVeniceSystemPrompt,
                EnableWebSearch = venice.EnableWebSearch,
                EnableWebCitations = venice.EnableWebCitations,
                EnableXSearch = venice.EnableXSearch,
                DisableThinking = wire.DisableThinking,
                StripThinkingResponse = venice.StripThinkingResponse ?? true
            };
        }
        else if (hasTools && !ReasoningPolicy.AllowsEffortWithTools(info, model))
        {
            // Callers that never set ReasoningChoice still 400 on GPT-5.6: the model
            // defaults to medium, which is illegal next to function tools.
            effort = ReasoningPolicy.None;
        }

        return new ChatCompletionRequest
        {
            Model = model,
            Messages = prepareMessages ? ApiContextLimiter.Prepare(request.Messages) : request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            Stream = stream,
            StreamOptions = stream ? new StreamOptions() : null,
            ReasoningEffort = effort,
            Reasoning = reasoning,
            VeniceParameters = venice
        };
    }

    public async Task<IReadOnlyList<VeniceModelInfo>> ListTextModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = Request(HttpMethod.Get, "models?type=text");

        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        VeniceModelsListResponse result;
        try
        {
            result = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.VeniceModelsListResponse)
                ?? throw new VeniceApiException("Empty models response from Venice API.");
        }
        catch (JsonException ex)
        {
            var preview = string.IsNullOrWhiteSpace(body)
                ? "(empty body)"
                : body.Length > 240 ? body[..240] + "…" : body;
            throw new VeniceApiException(
                $"Venice API returned non-JSON models response ({ex.Message}). Body: {preview}");
        }

        return result.Data;
    }

    /// <summary>
    /// Остаток и лимиты ключа. Единственный способ узнать остаток, не потратив ни цента:
    /// заголовки ответа его несут только после настоящего запроса к модели.
    /// </summary>
    /// <param name="apiKeyOverride">
    /// Чужой ключ — страница настроек показывает остаток и по тем ключам, что сейчас не активны.
    /// Второй <see cref="HttpClient"/> для этого не нужен: ключ едет на самом запросе.
    /// </param>
    public async Task<VeniceRateLimitsData> GetRateLimitsAsync(
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = Request(HttpMethod.Get, "api_keys/rate_limits", apiKeyOverride);
        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        // Только для активного ключа: чужой остаток на плашку ставить нельзя.
        if (string.IsNullOrWhiteSpace(apiKeyOverride))
        {
            UpdateBalanceFromHeaders(response);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        try
        {
            return JsonSerializer.Deserialize(body, VeniceJsonContext.Default.VeniceRateLimitsResponse)?.Data
                ?? throw new VeniceApiException("Empty rate limits response from Venice API.");
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"Venice API returned non-JSON rate limits response ({ex.Message}). Body: {Preview(body)}");
        }
    }

    /// <summary>
    /// Страница журнала трат Venice — то, за что с ключа списали на самом деле.
    /// </summary>
    /// <remarks>
    /// Сюда попадает всё: и ответы, и придуманные заголовки чатов, и скрытые сводки, и поиск
    /// в сети, и картинки — включая то, что программа у себя не считает (неудачные попытки из
    /// цепочки замен модели списываются, а до <c>AddCost</c> не доходят). Поэтому график трат
    /// строится по этому журналу, а не по внутреннему счётчику.
    /// <para>
    /// Границы периода Venice принимает только на первой странице: вместе с курсором фильтры
    /// слать нельзя, и продолжение обхода идёт одним лишь курсором.
    /// </para>
    /// </remarks>
    public async Task<VeniceUsagePage> GetUsageHistoryAsync(
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        string? cursor = null,
        int pageSize = 1000,
        string? apiKeyOverride = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string> { "pageSize=" + Math.Clamp(pageSize, 10, 1000) };
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            query.Add("cursor=" + Uri.EscapeDataString(cursor));
        }
        else
        {
            if (fromUtc is { } from)
            {
                query.Add("startTimestamp=" + Uri.EscapeDataString(Iso(from)));
            }

            if (toUtc is { } to)
            {
                query.Add("endTimestamp=" + Uri.EscapeDataString(Iso(to)));
            }
        }

        using var httpRequest = Request(
            HttpMethod.Get, "billing/usage-history?" + string.Join("&", query), apiKeyOverride);
        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // Отдельным исключением, потому что это не поломка, а свойство ключа: журнал трат
            // Venice отдаёт только админ-ключу, а работают люди обычным, для запросов к моделям.
            // Вызывающий по этому отказу переходит на собственный журнал программы.
            if (body.Contains("Admin API key", StringComparison.OrdinalIgnoreCase))
            {
                throw new VeniceAdminKeyRequiredException(
                    $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
            }

            throw new VeniceApiException(
                $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        try
        {
            return JsonSerializer.Deserialize(body, VeniceJsonContext.Default.VeniceUsagePage)
                ?? new VeniceUsagePage();
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"Venice API returned non-JSON usage response ({ex.Message}). Body: {Preview(body)}");
        }
    }

    private static string Iso(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string Preview(string body) =>
        string.IsNullOrWhiteSpace(body) ? "(empty body)"
        : body.Length > 240 ? body[..240] + "…"
        : body;

    public void ResetRequestCost()
    {
        lock (_costGate)
        {
            RequestCost = VeniceCost.Zero;
        }
    }

    private void RecordCost(ChatCompletionResponse result, string sku)
    {
        if (result.Cost is not null)
        {
            AddCost(result.Cost.ToCost(), sku);
        }
    }

    /// <summary>
    /// The one way money is booked. Tool calls in a round run in parallel and share this client,
    /// so the running total needs a gate; the same charge is also billed to whichever tool call
    /// is on the stack, which is what puts a price tag next to generate_image in the transcript.
    /// </summary>
    /// <param name="sku">
    /// За что списали: идентификатор модели либо служебная статья вроде <c>web-search-request</c>.
    /// Из этих пометок складывается разбивка «на что ушло» на странице «Key &amp; Info».
    /// </param>
    private void AddCost(VeniceCost cost, string sku)
    {
        lock (_costGate)
        {
            RequestCost = RequestCost.Add(cost);
        }

        // Собственный журнал трат: Venice свой отдаёт только админ-ключу, а работают
        // обычным. Здесь же, в единственной точке учёта, видны все деньги программы сразу.
        _options.SpendSink?.Invoke(_options.ApiKey, cost, sku);

        // Свой счёт у каждого хода чата: один общий RequestCost на несколько одновременных
        // ходов не делится. Сам он остаётся — им пользуется агент, у которого клиент на прогон.
        VeniceTurnScope.Current?.Add(cost);
        AgentRunScope.Charge(cost);
    }

    public async Task<string> ScrapeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ScrapeUrlRequest { Url = url },
            VeniceJsonContext.Default.ScrapeUrlRequest);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = Request(HttpMethod.Post, "augment/scrape");
        httpRequest.Content = content;
        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice scrape error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var markdown = root.TryGetProperty("content", out var contentProp)
            ? contentProp.GetString()
            : null;

        AddCost(new VeniceCost { Usd = 0.01m, HasData = true }, VeniceSku.Scrape);
        return string.IsNullOrWhiteSpace(markdown) ? "Страница пуста или контент не извлечён." : markdown;
    }

    /// <summary>
    /// Default image model — Google's "nano banana" through Venice. Costs more than a diffusion
    /// model, but it is the one that renders legible text inside the picture, which is the whole
    /// point for diagrams and infographics.
    /// </summary>
    public const string DefaultImageModel = "nano-banana-pro";

    /// <summary>True for the Gemini-backed line, which is sized by ratio rather than pixels.</summary>
    private static bool UsesAspectRatio(string model) =>
        model.StartsWith("nano-banana", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A drawn picture costs far more than the text around it, and until this was priced the
    /// message header showed only the chat tokens — a few hundredths of a cent for a turn that
    /// really cost cents. Used only when Venice reports neither a cost nor a usable balance delta.
    /// </summary>
    private const decimal FallbackImageUsd = 0.10m;

    /// <summary>
    /// Generates one image and returns it as base64 PNG. Venice bills this per image, and the
    /// charge is folded into <see cref="RequestCost"/> so it reaches the message header.
    /// </summary>
    /// <param name="aspectRatio">e.g. "3:4" for a portrait infographic. Ignored by pixel-sized models.</param>
    public async Task<string> GenerateImageAsync(
        string prompt,
        int width = 1024,
        int height = 1024,
        string? model = null,
        string? aspectRatio = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        var resolved = string.IsNullOrWhiteSpace(model) ? DefaultImageModel : model;
        var byRatio = UsesAspectRatio(resolved);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ImageGenerateRequest
            {
                Model = resolved,
                Prompt = prompt,
                Width = byRatio ? null : width,
                Height = byRatio ? null : height,
                AspectRatio = byRatio ? (aspectRatio ?? RatioFor(width, height)) : null,
                Resolution = byRatio ? "2K" : null
            },
            VeniceJsonContext.Default.ImageGenerateRequest);

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = Request(HttpMethod.Post, "image/generate");
        httpRequest.Content = content;

        var balanceBefore = LastBalance?.Usd;

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice image error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.ImageGenerateResponse);
        var image = result?.Images.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
        if (image is null)
        {
            throw new VeniceApiException("Venice image error: пустой ответ без изображения.");
        }

        AddCost(PriceImage(result!.Cost, balanceBefore, LastBalance?.Usd), resolved + "-image");
        return image;
    }

    /// <summary>
    /// What the picture cost, best source first: the number Venice put in the body, else how much
    /// the account balance moved across this one call, else a flat estimate. The balance reading
    /// is only trusted when it moved by a plausible amount — a top-up or a parallel request in
    /// flight would otherwise show up as a wild figure in the message header.
    /// </summary>
    private static VeniceCost PriceImage(VeniceCostResponse? reported, decimal? before, decimal? after)
    {
        var cost = reported?.ToCost();
        if (cost is { HasData: true })
        {
            return cost;
        }

        if (before is { } start && after is { } end)
        {
            var spent = start - end;
            if (spent > 0m && spent <= 1m)
            {
                return new VeniceCost { Usd = spent, HasData = true };
            }
        }

        return new VeniceCost { Usd = FallbackImageUsd, HasData = true };
    }

    /// <summary>Maps the caller's pixel intent onto the nearest ratio the ratio-based models take.</summary>
    private static string RatioFor(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "1:1";
        }

        var ratio = width / (double)height;
        return ratio switch
        {
            < 0.72 => "3:4",
            < 0.95 => "4:5",
            < 1.06 => "1:1",
            < 1.4 => "5:4",
            _ => "4:3"
        };
    }

    public async Task<string> SearchWebAsync(string query, CancellationToken cancellationToken = default)
    {
        // Поиск раньше «случайно» попадал на модель чата: её только что записал SetActiveModel.
        // Теперь поле клиента не дрейфует, поэтому модель хода берём из его собственного контекста.
        var model = VeniceTurnScope.Current?.ModelId ?? _options.Model;
        var useXSearch = _options.EnableXSearch
            ?? model.Contains("grok", StringComparison.OrdinalIgnoreCase);

        // Поиск оплачивается токенами модели, но в разбивке трат он обязан стоять своей
        // строкой: человек спрашивает «сколько ушло на интернет», а не «сколько ушло на Grok
        // во время поиска».
        using (ChargeAs(VeniceSku.WebSearch))
        {
            var response = await CreateChatCompletionAsync(new ChatCompletionRequest
            {
                Model = model,
                Messages =
                [
                    new ChatMessage
                    {
                        Role = "system",
                        // The links are the point, not decoration: the caller is a model that can
                        // fetch a picture or read a page, but only if it is handed an address. The
                        // old wording asked for "a summary with practical fixes" and got prose like
                        // "on DeviantArt, search the furrywallpaper tag" — advice no tool can act on.
                        Content = ChatContent.Text(
                            "You are a web research assistant. Search the web and answer concisely in Russian.\n" +
                            "ALWAYS end with a section 'Ссылки:' listing the full URLs you actually used, " +
                            "one per line, bare (no markdown, no shortening). Never write a link as a " +
                            "description like 'ищи по тегу X on site Y' - give the address itself.\n" +
                            "If the request is about pictures, art, wallpapers, photos or covers, list at " +
                            "least 5 URLs of pages that show a matching image, and direct file URLs " +
                            "(.jpg/.png/.webp) whenever the search results reveal them.")
                    },
                    new ChatMessage { Role = "user", Content = ChatContent.Text(query) }
                ],
                VeniceParameters = new VeniceParameters
                {
                    IncludeVeniceSystemPrompt = false,
                    EnableWebSearch = "on",
                    EnableWebCitations = _options.EnableWebCitations,
                    EnableXSearch = useXSearch ? true : null
                }
            }, cancellationToken).ConfigureAwait(false);

            var text = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;
            return string.IsNullOrWhiteSpace(text) ? "Результаты поиска не найдены." : text;
        }
    }

    private void UpdateBalanceFromHeaders(HttpResponseMessage response)
    {
        var usd = TryReadHeaderDecimal(response, "x-venice-balance-usd");
        var diem = TryReadHeaderDecimal(response, "x-venice-balance-diem");

        if (usd is null && diem is null)
        {
            return;
        }

        LastBalance = new VeniceBalance
        {
            CanConsume = true,
            Usd = usd,
            Diem = diem
        };
    }

    private static decimal? TryReadHeaderDecimal(HttpResponseMessage response, string headerName)
    {
        if (!response.Headers.TryGetValues(headerName, out var values))
        {
            return null;
        }

        var text = values.FirstOrDefault();
        return decimal.TryParse(text, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string ExtractErrorMessage(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.ChatCompletionResponse);
            if (!string.IsNullOrWhiteSpace(error?.Error?.Message))
            {
                return error.Error.Message;
            }
        }
        catch
        {
            // fall through
        }

        return string.IsNullOrWhiteSpace(body) ? "No details" : body;
    }
}

public class VeniceApiException(string message) : Exception(message);

/// <summary>
/// Venice отдаёт журнал трат только админ-ключу, а для запросов к моделям человек держит
/// обычный.
/// </summary>
/// <remarks>
/// Документация Venice утверждает, что <c>billing/usage-history</c> открыт обычному ключу.
/// На деле он отвечает <c>401 Admin API key required</c> — проверено на живом ключе. Это не
/// сбой сети и не повод показывать ошибку: программа просто берёт свой журнал трат.
/// </remarks>
public sealed class VeniceAdminKeyRequiredException(string message) : VeniceApiException(message);