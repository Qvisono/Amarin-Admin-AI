using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly string _primaryModel;

    public VeniceBalance? LastBalance { get; private set; }

    public VeniceCost RequestCost { get; private set; } = VeniceCost.Zero;

    /// <summary>Охраняет накопленную сумму: параллельные вызовы инструментов делят один клиент.</summary>
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
        ChargeLabel.Value is { } label && !string.IsNullOrWhiteSpace(label) ? label : model;

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
    /// Ищет возможности модели в <c>/models</c>, чтобы запасной путь снимал
    /// <c>reasoning_effort</c> у моделей, которые его отвергают (Grok). Null, пока каталог не загружен.
    /// </summary>
    public Func<string, VeniceModelInfo?>? ResolveModelInfo { get; set; }

    /// <summary>
    /// Модель по умолчанию — та, с которой запустилось приложение. Ход чата ею не пользуется:
    /// у него своя, в <see cref="VeniceTurnContext"/>. Раньше рядом стоял и сеттер, общий на всё
    /// приложение, — два одновременных хода перетоптали бы друг другу модель.
    /// </summary>
    public string ActiveModel => _options.Model;

    /// <summary>Чей сервер отвечает прямо сейчас. Едет вместе с активным ключом.</summary>
    public LlmProvider ActiveProvider => _options.Provider;

    /// <remarks>
    /// Ни <c>BaseAddress</c>, ни <c>Authorization</c> на клиента не садятся — и то и другое
    /// ставит <see cref="Request"/> на сам запрос. Раньше адрес прилипал к клиенту через
    /// <c>??=</c>, и сменить провайдера у живого клиента было нельзя: пересоздать его негде —
    /// <c>AppServices.Venice</c> объявлен <c>required init</c>, а <c>ChatEngine._venice</c> —
    /// <c>readonly</c>.
    /// </remarks>
    public VeniceClient(HttpClient http, AgentOptions options)
    {
        _http = http;
        _options = options;
        _primaryModel = options.Model;
    }

    /// <summary>
    /// Запрос к провайдеру вместе с ключом. Единственное место, где программа предъявляет
    /// ключ, — и единственное, где решается, чьему серверу он предъявлен.
    /// </summary>
    /// <remarks>
    /// И заголовок, и адрес садятся на сам запрос, а не на клиента. Заголовок — потому что на
    /// <c>DefaultRequestHeaders</c> выходило двумя бедами сразу: клиент нёс <c>Bearer</c> в
    /// любой запрос, куда бы тот ни шёл (GitHub отвечал на чужой ключ 401, а сам ключ уезжал
    /// на посторонний сервер), и присваивание через <c>??=</c> намертво запоминало первый
    /// ключ. Адрес — по той же причине: провайдер меняется вместе с активным ключом, и
    /// прилипший к клиенту <c>BaseAddress</c> отправил бы модели OpenRouter в Venice.
    /// Правило «для нового адресата — свой клиент через <see cref="HttpClients.Create"/>»
    /// остаётся в силе: у клиентов разные таймауты и представление.
    /// </remarks>
    /// <param name="credentialOverride">
    /// Чужие учётные данные — когда страница настроек спрашивает баланс ключа, который сейчас
    /// не активен, или когда рисование картинок ищет ключ Venice при активном ключе OpenRouter.
    /// Пусто — берутся активные.
    /// </param>
    private HttpRequestMessage Request(
        HttpMethod method,
        string path,
        ApiCredential? credentialOverride = null)
    {
        var credential = credentialOverride ?? _options.Credential;

        var request = new HttpRequestMessage(method, EndpointFor(credential, path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        if (!string.IsNullOrWhiteSpace(credential.Secret))
        {
            if (credential.Provider.IsCustom)
            {
                // Свой сервер: каким заголовком он берёт ключ, заранее не узнать. Шлюзы под
                // Claude Code ждут x-api-key, OpenAI-совместимые — Bearer, а ai.starimg.ru
                // требует Bearer и на /v1/messages. Ключ уходит туда же, куда и так уходит.
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
                request.Headers.Add("x-api-key", credential.Secret);
                request.Headers.Add("anthropic-version", AnthropicMessages.ApiVersion);
            }
            else if (ProviderSpec.For(credential.Provider).Protocol == WireProtocol.AnthropicMessages)
            {
                // Anthropic ключ берёт своим заголовком, а Bearer у него значит токен входа — с
                // обычным ключом такой запрос был бы отвергнут.
                request.Headers.Add("x-api-key", credential.Secret);
                request.Headers.Add("anthropic-version", AnthropicMessages.ApiVersion);
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
            }
        }

        return request;
    }

    /// <summary>
    /// Отказ провайдера строкой, которую увидит человек.
    /// </summary>
    /// <remarks>
    /// Имя берётся из ключа запроса, а не зашито: «Venice API error (404)» на ходе через
    /// OpenRouter отправляло человека чинить не тот провайдер — ключ Venice тут ни при чём.
    /// </remarks>
    private static VeniceApiException ApiError(ApiCredential credential, int status, string message) =>
        new($"{ProviderSpec.Of(credential).Name} API error ({status}): {message}")
        {
            StatusCode = status
        };

    /// <summary>Учётные данные запроса: свои у слота либо выбранный ключ программы.</summary>
    private ApiCredential Resolve(ApiCredential? credential) => credential ?? _options.Credential;

    /// <summary>
    /// Тот ли это ключ, что выбран на странице «Key &amp; Info».
    /// </summary>
    /// <remarks>
    /// Остаток на плашке в композере принадлежит выбранному ключу. Слоты моделей платят каждый
    /// своим, и заголовок чата, оплаченный вторым ключом, поставил бы человеку на плашку чужую
    /// цифру — а он решил бы, что деньги кончаются не там, где на самом деле. Держателя ключей
    /// нет вовсе — клиент собран одной строкой в тесте, и делить нечего.
    /// </remarks>
    private bool IsSelectedKey(ApiCredential credential) =>
        _options.Keys is not { } keys || keys.Credential.Equals(credential);

    /// <summary>
    /// Отказ, который читает модель: у провайдера этой модели нет ни одного ключа.
    /// </summary>
    /// <remarks>
    /// Без этой проверки запрос уходит без заголовка <c>Authorization</c> и возвращается
    /// четырёхсоткой чужого сервера — строкой, из которой человеку не понять, что он всего лишь
    /// выбрал модель провайдера, ключ которого забыл добавить.
    /// </remarks>
    private static ApiCredential RequireKey(ApiCredential credential, string model)
    {
        if (credential.IsEmpty)
        {
            var name = ProviderSpec.For(credential.Provider).Name;
            throw new VeniceApiException(
                $"Модель {model} работает через {name}, а ключа {name} в программе нет. " +
                $"Скажи пользователю добавить ключ {name} на странице настроек «Key & Info». " +
                "Не повторяй этот вызов.");
        }

        return credential;
    }

    /// <summary>Уровень размышления, если он из числа общепринятых. Иначе — ничего.</summary>
    private static string? Common(string? effort) =>
        ReasoningPolicy.CommonEfforts.FirstOrDefault(
            known => known.Equals(effort?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Абсолютный адрес: база провайдера плюс путь запроса.</summary>
    /// <remarks>
    /// У незнакомого провайдера адреса нет, и запрос не уходит никуда. До 1.34.0 такой
    /// провайдер получал адрес Venice — вместе с ним туда уехал бы и ключ.
    /// </remarks>
    private Uri EndpointFor(ApiCredential credential, string path)
    {
        // Переопределение из appsettings.json касается только Venice; у остальных
        // провайдеров переопределять нечего, и адрес берётся из их описания, а у своего
        // сервера человека — из записи, которая едет вместе с ключом.
        var spec = ProviderSpec.Of(credential);
        var baseUrl = credential.Provider == LlmProvider.Venice ? _options.BaseUrl : spec.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new VeniceApiException(
                $"Провайдер {spec.Name} программе незнаком: адреса его сервера нет, и запрос не " +
                "отправлен. Скажи пользователю выбрать модель другого провайдера. Не повторяй этот вызов.");
        }

        return new Uri(baseUrl.TrimEnd('/') + "/" + path.TrimStart('/'));
    }

    /// <param name="credential">
    /// Ключ этого слота моделей. Пусто — платим выбранным ключом программы. Общий клиент
    /// обслуживает слоты разных провайдеров сразу, и ключ обязан ехать на запросе, а не жить
    /// полем клиента: два хода перетоптали бы поле друг другу.
    /// </param>
    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        VeniceParameters veniceParameters,
        CancellationToken cancellationToken = default,
        ReasoningChoice? reasoning = null,
        ApiCredential? credential = null) =>
        CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = model,
            Messages = messages.ToList(),
            Tools = tools,
            ToolChoice = toolChoice,
            VeniceParameters = veniceParameters,
            ReasoningChoice = reasoning
        }, prepareMessages: true, credential, cancellationToken);

    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default,
        ApiCredential? credential = null) =>
        CreateChatCompletionAsync(request, prepareMessages: false, credential, cancellationToken);

    private async Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        bool prepareMessages,
        ApiCredential? credential,
        CancellationToken cancellationToken)
    {
        // Метод берёт модель параметром и кладёт её в запрос — значит и перебор обязан начинаться
        // с неё. Раньше он стартовал с поля клиента, то есть с модели, которую никто не просил.
        var startingModel = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;
        var key = RequireKey(Resolve(credential), startingModel);
        await GuardSpendAsync(key, cancellationToken).ConfigureAwait(false);
        VeniceApiException? lastOverload = null;

        // Провайдер цепочки — из ключа запроса, а не из клиента: замена модели обязана остаться
        // на сервере, которому этот ключ предъявляют.
        foreach (var model in VeniceModelFallback.GetModelsFrom(
                     startingModel, _primaryModel, key.Provider))
        {
            try
            {
                var result = await SendChatCompletionAsync(
                        request, model, prepareMessages, key, cancellationToken)
                    .ConfigureAwait(false);

                if (!model.Equals(startingModel, StringComparison.OrdinalIgnoreCase))
                {
                    // Модель программы сдвигает только запрос без своего ключа. У запроса со
                    // своим ключом модель принадлежит слоту, и общая подмена увела бы слот
                    // к чужому провайдеру.
                    if (credential is null)
                    {
                        _options.Model = model;
                    }

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
            ?? new VeniceApiException(Loc.Get("S.Provider.AllOverloaded"));
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
        ReasoningChoice? reasoning = null,
        ApiCredential? credential = null) =>
        StreamChatCompletionAsync(new ChatCompletionRequest
        {
            Model = model,
            Messages = messages.ToList(),
            Tools = tools,
            ToolChoice = toolChoice,
            Stream = true,
            VeniceParameters = veniceParameters,
            ReasoningChoice = reasoning
        }, primaryModel, prepareMessages: true, onText, credential, cancellationToken);

    private async Task<StreamedChatCompletion> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        string primaryModel,
        bool prepareMessages,
        Action<string>? onText,
        ApiCredential? credential,
        CancellationToken cancellationToken)
    {
        var startingModel = string.IsNullOrWhiteSpace(request.Model) ? _options.Model : request.Model;
        var key = RequireKey(Resolve(credential), startingModel);
        await GuardSpendAsync(key, cancellationToken).ConfigureAwait(false);
        VeniceApiException? lastOverload = null;

        foreach (var model in VeniceModelFallback.GetModelsFrom(
                     startingModel, primaryModel, key.Provider))
        {
            try
            {
                var result = await SendChatCompletionStreamingAsync(
                        request, model, prepareMessages, onText, key, cancellationToken)
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
            ?? new VeniceApiException(Loc.Get("S.Provider.AllOverloaded"));
    }

    private async Task<ChatCompletionResponse> SendChatCompletionAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        if (IsBatchRoute(model))
        {
            return await RunBatchAsync(
                    request, model, prepareMessages, onProgress: null, credential, cancellationToken)
                .ConfigureAwait(false);
        }

        if (UsesAnthropic(model, credential))
        {
            return await SendAnthropicAsync(request, model, prepareMessages, credential, cancellationToken)
                .ConfigureAwait(false);
        }

        var payload = BuildPayload(request, model, prepareMessages, stream: false, credential);
        using var response = await PostCompletionAsync(payload, model, credential, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = ExtractErrorMessage(body);
            if (NoteToolGrammarFailure(payload, model, error, out var friendly))
            {
                throw new VeniceApiException(friendly);
            }

            if (Relaxed(payload, model, (int)response.StatusCode, error) is { } relaxed)
            {
                payload = relaxed;
                using var retry = await PostCompletionAsync(payload, model, credential, cancellationToken)
                    .ConfigureAwait(false);
                body = await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!retry.IsSuccessStatusCode)
                {
                    throw ApiError(credential, (int)retry.StatusCode, ExtractErrorMessage(body));
                }

                return ReadCompletion(body, model, credential);
            }

            throw ApiError(credential, (int)response.StatusCode, error);
        }

        return ReadCompletion(body, model, credential);
    }

    private ChatCompletionResponse ReadCompletion(string body, string model, ApiCredential credential)
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

        RecordCost(result, model, credential);
        return result;
    }

    private async Task<StreamedChatCompletion> SendChatCompletionStreamingAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        Action<string>? onText,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        if (IsBatchRoute(model))
        {
            // Потока у очереди нет вовсе, поэтому «поток» здесь — это ожидание с отчётом о
            // состоянии заявки в том же месте, где обычно проступает ответ, и весь текст разом
            // в конце. Иначе ход стоял бы пустым и молчал — минуты, а то и часы.
            var answer = await RunBatchAsync(
                    request, model, prepareMessages, onText, credential, cancellationToken)
                .ConfigureAwait(false);

            return ToStreamed(answer, model, onText);
        }

        if (UsesAnthropic(model, credential))
        {
            return await StreamAnthropicAsync(request, model, prepareMessages, onText, credential, cancellationToken)
                .ConfigureAwait(false);
        }

        var payload = BuildPayload(request, model, prepareMessages, stream: true, credential);
        var response = await PostCompletionAsync(payload, model, credential, cancellationToken)
            .ConfigureAwait(false);
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

            if (Relaxed(payload, model, status, error) is not { } relaxed)
            {
                throw ApiError(credential, status, error);
            }

            payload = relaxed;
            response = await PostCompletionAsync(payload, model, credential, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var retryBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                status = (int)response.StatusCode;
                response.Dispose();
                throw ApiError(credential, status, ExtractErrorMessage(retryBody));
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

            var cost = CostOf(accumulator.Cost, model, accumulator.PromptTokens, accumulator.CompletionTokens);
            Book(cost, model, credential);

            return new StreamedChatCompletion
            {
                Text = accumulator.Text,
                ReasoningText = accumulator.ReasoningText,
                InlineReasoning = accumulator.InlineReasoning,
                ThinkingElapsed = accumulator.ThinkingElapsed,
                ToolCalls = accumulator.BuildToolCalls(),
                FinishReason = accumulator.FinishReason,
                Cost = cost,
                PromptTokens = accumulator.PromptTokens,
                CompletionTokens = accumulator.CompletionTokens,
                TotalTokens = accumulator.TotalTokens,
                Model = model
            };
        }
    }

    /// <summary>
    /// Готовый ответ в том виде, в каком его ждёт потоковый путь.
    /// </summary>
    /// <remarks>
    /// Размышление отделяется тем же разбором, что и у потока: модели, пишущие мысли тегами
    /// внутри ответа, делают это независимо от того, каким путём ответ приехал, и без разбора
    /// теги ушли бы в переписку как текст.
    /// </remarks>
    private static StreamedChatCompletion ToStreamed(
        ChatCompletionResponse answer,
        string model,
        Action<string>? onText)
    {
        var choice = answer.Choices.FirstOrDefault();
        var raw = choice?.Message.Content is { ValueKind: JsonValueKind.String } text
            ? text.GetString() ?? ""
            : "";
        var (inlineReasoning, body) = ReasoningSplit.Split(raw);

        onText?.Invoke(body);

        return new StreamedChatCompletion
        {
            Text = body,
            InlineReasoning = inlineReasoning,
            ToolCalls = choice?.Message.ToolCalls ?? [],
            FinishReason = choice?.FinishReason,
            Cost = answer.ReportedCost() ?? VeniceCost.Zero,
            PromptTokens = answer.Usage?.PromptTokens ?? 0,
            CompletionTokens = answer.Usage?.CompletionTokens ?? 0,
            TotalTokens = answer.Usage?.TotalTokens ?? 0,
            Model = model,
            ReplayBlocks = choice?.Message.ReplayBlocks
        };
    }

    private async Task<HttpResponseMessage> PostCompletionAsync(
        ChatCompletionRequest payload,
        string model,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var serializeWatch = Stopwatch.StartNew();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, VeniceJsonContext.Default.ChatCompletionRequest);
        var serializeMs = serializeWatch.ElapsedMilliseconds;

        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var httpRequest = Request(HttpMethod.Post, "chat/completions", credential);
        httpRequest.Content = content;

        var httpWatch = Stopwatch.StartNew();
        var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        UpdateBalanceFromHeaders(response, credential);

        // Провайдер в строке — единственная подсказка, когда переключение пошло не так:
        // по одной модели и статусу не видно, чьему серверу запрос вообще уехал.
        PerfLog.Write(
            $"llm provider={credential.Provider} serialize_ms={serializeMs} " +
            $"http_ms={httpWatch.ElapsedMilliseconds} status={(int)response.StatusCode} model={model}");
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

    /// <remarks>
    /// Копирует все поля, включая <c>stream_options</c>: до 1.34.0 оно терялось, и потоковый
    /// повтор приходил без расхода токенов — цена этого ответа не попадала в журнал трат.
    /// </remarks>
    private static ChatCompletionRequest WithReasoningEffort(ChatCompletionRequest request, string? effort) =>
        new()
        {
            Model = request.Model,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            Stream = request.Stream,
            StreamOptions = request.StreamOptions,
            ReasoningEffort = effort,
            Reasoning = request.Reasoning,
            Thinking = request.Thinking,
            EnableThinking = request.EnableThinking,
            VeniceParameters = request.VeniceParameters,
            Usage = request.Usage,
            Plugins = request.Plugins,
            ReasoningChoice = request.ReasoningChoice
        };

    /// <summary>
    /// Тело без всего, что не обязательно: полей размышления, температуры и просьбы о расходе.
    /// </summary>
    private static ChatCompletionRequest WithoutOptionalFields(ChatCompletionRequest request) =>
        new()
        {
            Model = request.Model,
            Messages = request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = null,
            Stream = request.Stream,
            StreamOptions = null,
            ReasoningChoice = request.ReasoningChoice
        };

    /// <summary>
    /// Тело запроса по форме, которую понимает сервер провайдера модели.
    /// </summary>
    /// <remarks>
    /// Выбор по описанию провайдера, а не «Venice — иначе OpenRouter»: прежде любой третий
    /// провайдер получил бы надстройки OpenRouter, которых его сервер не знает.
    /// </remarks>
    private ChatCompletionRequest BuildPayload(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream,
        ApiCredential credential)
    {
        // Описание — по ключу запроса, если он того же провайдера, что и модель: у своего
        // сервера человека адрес и протоколы живут рядом с ключом, а не в описании провайдера.
        var provider = ModelRef.Of(model, _options.Provider);
        var spec = credential.Provider == provider ? ProviderSpec.Of(credential) : ProviderSpec.For(provider);
        return spec.Dialect switch
        {
            PayloadDialect.Venice => BuildVenicePayload(request, model, prepareMessages, stream),
            PayloadDialect.OpenRouter => BuildOpenRouterPayload(request, model, prepareMessages, stream),
            _ => BuildGenericPayload(request, model, spec, prepareMessages, stream)
        };
    }

    /// <summary>
    /// Тело для OpenAI-совместимого сервера без надстроек: OpenAI, Gemini, xAI, DeepSeek, Kimi,
    /// Mistral, GLM, Qwen, Groq и свои серверы людей.
    /// </summary>
    /// <remarks>
    /// Только поля, которые знает любой такой сервер, плюс размышление тем полем, которым его
    /// понимает этот провайдер (<see cref="ProviderSpec.Reasoning"/>). Если сервер всё же отвергнет
    /// необязательное поле, запрос один раз повторяется без них (<see cref="Relaxed"/>).
    /// </remarks>
    private ChatCompletionRequest BuildGenericPayload(
        ChatCompletionRequest request,
        string model,
        ProviderSpec spec,
        bool prepareMessages,
        bool stream)
    {
        var info = ResolveModelInfo?.Invoke(model);
        var hasTools = request.Tools is { Count: > 0 };
        var effort = request.ReasoningEffort;
        ThinkingSwitch? thinking = null;
        bool? enableThinking = null;

        if (request.ReasoningChoice is { } choice)
        {
            var on = !choice.DisableThinking;
            effort = null;

            switch (spec.Reasoning)
            {
                case ReasoningDialect.ThinkingSwitch:
                    thinking = on ? ThinkingSwitch.On : ThinkingSwitch.Off;
                    effort = on ? ReasoningPolicy.ClampEffort(choice.Effort, info, hasTools) ?? Common(choice.Effort) : null;
                    break;

                case ReasoningDialect.EnableThinking:
                    enableThinking = on;
                    break;

                default:
                    effort = GenericEffort(choice, info, model, hasTools, spec);
                    break;
            }
        }

        return new ChatCompletionRequest
        {
            Model = ModelRef.Bare(model),
            Messages = prepareMessages ? ApiContextLimiter.Prepare(request.Messages) : request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = spec.SendsTemperature ? request.Temperature : null,
            Stream = stream,
            StreamOptions = stream ? new StreamOptions() : null,
            ReasoningEffort = effort,
            Thinking = thinking,
            EnableThinking = enableThinking,
            ReasoningChoice = request.ReasoningChoice
        };
    }

    /// <summary>
    /// Уровень размышления для сервера, у которого оно задаётся одним полем <c>reasoning_effort</c>.
    /// </summary>
    /// <remarks>
    /// «Без размышления» — значение <c>none</c>, только если модель его знает: незнакомое значение
    /// сервер отвергает. У семейства GPT-5.4+ рядом с инструментами допустимо одно <c>none</c>
    /// (<see cref="ReasoningPolicy.ToWire"/>), и это правило провайдер наследует от OpenAI.
    /// </remarks>
    private static string? GenericEffort(
        ReasoningChoice choice,
        VeniceModelInfo? info,
        string model,
        bool hasTools,
        ProviderSpec spec)
    {
        if (hasTools && spec.BlocksEffortWithTools && !ReasoningPolicy.AllowsEffortWithTools(info, model))
        {
            return ReasoningPolicy.None;
        }

        if (choice.DisableThinking)
        {
            return ReasoningPolicy.HasNoneOption(info) ? ReasoningPolicy.None : null;
        }

        return ReasoningPolicy.ClampEffort(choice.Effort, info, hasTools) ?? Common(choice.Effort);
    }

    /// <summary>
    /// Тело для повтора после отказа, если отказ можно обойти. <c>null</c> — повторять нечего.
    /// </summary>
    /// <remarks>
    /// Два обхода. Отказ усилия рядом с инструментами (GPT-5.x) — повтор с <c>none</c>. Отказ
    /// необязательного поля у сервера без надстроек — повтор без полей размышления и прочего
    /// необязательного: у провайдеров это поле устроено по-разному даже между их же моделями
    /// (Kimi K3 и K2, GLM-5), и перечислить все различия заранее нельзя.
    /// </remarks>
    private static ChatCompletionRequest? Relaxed(
        ChatCompletionRequest payload,
        string model,
        int status,
        string error)
    {
        if (ShouldRetryToolsEffortConflict(payload, model, error))
        {
            return WithReasoningEffort(payload, ReasoningPolicy.None);
        }

        var dialect = ProviderSpec.For(ModelRef.Of(model)).Dialect;
        if (dialect == PayloadDialect.Generic && status is 400 or 422 && NamesOptionalField(payload, error))
        {
            return WithoutOptionalFields(payload);
        }

        return null;
    }

    /// <summary>Отказ называет одно из необязательных полей, которые есть в этом теле.</summary>
    private static bool NamesOptionalField(ChatCompletionRequest payload, string error)
    {
        bool Names(string field) => error.Contains(field, StringComparison.OrdinalIgnoreCase);

        return (payload.ReasoningEffort is not null && Names("reasoning_effort")) ||
               (payload.Thinking is not null && Names("thinking")) ||
               (payload.EnableThinking is not null && Names("enable_thinking")) ||
               (payload.StreamOptions is not null && Names("stream_options")) ||
               (payload.Temperature is not null && Names("temperature"));
    }

    private ChatCompletionRequest BuildVenicePayload(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream)
    {
        var venice = request.VeniceParameters ?? new VeniceParameters();
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
            // Даже без ReasoningChoice GPT-5.6 отвечает 400: по умолчанию у неё medium, а рядом с
            // инструментами он недопустим.
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

    /// <summary>
    /// Тело запроса для OpenRouter.
    /// </summary>
    /// <remarks>
    /// Отличий от Venice три, и все обязательные. Надстроек <c>venice_parameters</c> здесь нет
    /// вовсе — чужому серверу они незнакомы. Размышление уходит вложенным объектом
    /// <c>reasoning</c>, а не верхним <c>reasoning_effort</c>: так его описывает OpenRouter, и
    /// так же его понимают все модели за ним. Выключенное размышление — отсутствие поля, а не
    /// <c>exclude: true</c>: последнее означает «думай, но не показывай», и токены за это
    /// списывают, тогда как переключатель в программе задуман как экономия.
    /// <para>
    /// Про запрет усилия рядом с инструментами здесь не спрашиваем: это свойство Venice, а не
    /// моделей, и разбор имени модели ошибся бы на тех же именах у другого провайдера.
    /// </para>
    /// </remarks>
    private ChatCompletionRequest BuildOpenRouterPayload(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream)
    {
        var info = ResolveModelInfo?.Invoke(model);
        var choice = request.ReasoningChoice;
        ReasoningConfig? reasoning = null;

        if (choice is { DisableThinking: false } thinking &&
            info?.ModelSpec?.Capabilities?.SupportsReasoning != false)
        {
            // Каталог мог ещё не доехать — тогда обрезать уровень не по чему, и просьба
            // человека уходит как есть: low/medium/high OpenRouter принимает у всех.
            // Уровня нет вовсе — просим размышление без уровня, а не пустой объект: пустой
            // означал бы «ничего не просили», и включённое размышление молча пропало бы.
            var effort = ReasoningPolicy.ClampEffort(thinking.Effort, info, withTools: true)
                         ?? Common(thinking.Effort);

            reasoning = effort is null
                ? new ReasoningConfig { Enabled = true }
                : new ReasoningConfig { Effort = effort };
        }
        else if (choice is null && request.Reasoning is { } asked)
        {
            reasoning = asked;
        }

        return new ChatCompletionRequest
        {
            // Приставка провайдера — наша выдумка для настроек и переписок; на провод уходит
            // идентификатор, каким его знает сам OpenRouter. Пометка пакетного варианта
            // снимается здесь же: обычный эндпоинт отвечает на неё 404 с требованием очереди,
            // а сюда с такой моделью приходит только служебная работа, которой очередь
            // не полагается (см. IsBatchRoute). Тело заявки эту строку всё равно перепишет
            // своим слагом (OpenRouterBatchPlan.ToBatchBody), так что пакетный путь не задет.
            Model = ModelRef.WithoutBatchVariant(ModelRef.Bare(model)),
            Messages = prepareMessages ? ApiContextLimiter.Prepare(request.Messages) : request.Messages,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Temperature = request.Temperature,
            Stream = stream,
            StreamOptions = stream ? new StreamOptions() : null,
            Reasoning = reasoning,
            Usage = new UsageAccounting(),
            Plugins = request.Plugins
        };
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

        return ProviderErrorText.Detail(body);
    }
}

public class VeniceApiException(string message) : Exception(message)
{
    /// <summary>
    /// HTTP-код отказа, если отказал сам сервер. <c>null</c> — ответа не было вовсе или он не
    /// разобрался.
    /// </summary>
    /// <remarks>
    /// Код полем, а не только числом в тексте: по нему плашка выбора модели объясняет отказ
    /// человеку (<see cref="ProviderErrorText.ForCatalog"/>), и разбирать для этого собственную
    /// строку исключения было бы хрупко.
    /// </remarks>
    public int? StatusCode { get; init; }
}

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

/// <summary>
/// Провайдер остатка на ключе не сообщает вовсе — у него нет такого запроса.
/// </summary>
/// <remarks>
/// Не сбой и не повод спрашивать снова после каждого ответа: плашка остатка показывает такой
/// ключ без цифры, а страница ключей — траты по журналу программы.
/// </remarks>
public sealed class BalanceNotReportedException(string message) : VeniceApiException(message);