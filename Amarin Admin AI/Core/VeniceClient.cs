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

        var request = new HttpRequestMessage(method, EndpointFor(credential.Provider, path))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        if (!string.IsNullOrWhiteSpace(credential.Secret))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);
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
        new($"{ProviderSpec.For(credential.Provider).Name} API error ({status}): {message}");

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
    private Uri EndpointFor(LlmProvider provider, string path)
    {
        // Переопределение из appsettings.json касается только Venice; у остальных
        // провайдеров переопределять нечего, и адрес берётся из их описания.
        var baseUrl = provider == LlmProvider.Venice
            ? _options.BaseUrl
            : ProviderSpec.For(provider).BaseUrl;

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
            ?? new VeniceApiException("Все модели в цепочке fallback перегружены. Повторите запрос позже.");
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

        var payload = BuildPayload(request, model, prepareMessages, stream: false);
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

            if (ShouldRetryToolsEffortConflict(payload, model, error))
            {
                payload = WithReasoningEffort(payload, ReasoningPolicy.None);
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

        RecordCost(result, ChargeSku(model), credential);
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

        var payload = BuildPayload(request, model, prepareMessages, stream: true);
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

            if (!ShouldRetryToolsEffortConflict(payload, model, error))
            {
                throw ApiError(credential, status, error);
            }

            payload = WithReasoningEffort(payload, ReasoningPolicy.None);
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

            if (accumulator.Cost.HasData)
            {
                AddCost(accumulator.Cost, ChargeSku(model), credential);
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

    // ───────────────────────── пакетная очередь OpenRouter ─────────────────────────

    /// <summary>
    /// Перерыв между опросами заявки. Подменяется в тестах: иначе каждая проверка ожидания
    /// стоила бы несколько настоящих секунд.
    /// </summary>
    internal Func<TimeSpan, CancellationToken, Task> BatchDelay { get; set; } = Task.Delay;

    /// <summary>Идёт ли этот запрос через очередь, а не через обычный эндпоинт.</summary>
    /// <remarks>
    /// Провайдер проверяется вместе с пометкой: <c>:batch</c> — выдумка OpenRouter, и у модели
    /// Venice такой хвост означал бы что угодно, только не очередь.
    /// <para>
    /// Очередь обслуживает только сам ответ человеку. Служебную работу — заголовок переписки,
    /// сводку, маршрутизатор, защиту, поиск в сети — ждёт не человек, а сам ход: маршрутизатор
    /// выбирает модель до первого слова, поиск отвечает внутри вызова инструмента, и сутки
    /// ожидания там означают просто зависший чат. Узнаётся она по пометке статьи расхода
    /// (<see cref="ChargeAs"/>) — той самой, которой уже помечены все одиннадцать таких мест;
    /// перечислять их по именам значило бы забыть двенадцатое. Работа агента приравнена к
    /// служебной по той же причине: агента зовут инструментом посреди раунда.
    /// </para>
    /// <para>
    /// Такой запрос уходит обычным путём к близнецу без пометки — его подставляет
    /// <see cref="BuildOpenRouterPayload"/>. Вдвое дороже, но за заголовок чата это центы,
    /// а очередь там не работает вовсе.
    /// </para>
    /// </remarks>
    private bool IsBatchRoute(string model) =>
        ModelRef.Of(model, _options.Provider) == LlmProvider.OpenRouter &&
        ModelRef.IsBatchOnly(model) &&
        string.IsNullOrWhiteSpace(ChargeLabel.Value) &&
        AgentRunScope.Current is null;

    /// <summary>
    /// Ведёт один запрос через пакетную очередь: кладёт заявку, ждёт её готовности и отдаёт
    /// ответ в том же виде, в каком его отдал бы обычный эндпоинт.
    /// </summary>
    /// <remarks>
    /// Снаружи это неотличимо от синхронного запроса — тем и ценно: агент, инструменты, учёт
    /// трат и запись переписки продолжают работать, ничего не зная про очередь. Плата за
    /// половинную цену — время: окно выполнения у очереди сутки, и ответ законно может идти
    /// часами. Отмена хода снимает и заявку, иначе человек платил бы за ответ, которого уже
    /// никто не прочитает.
    /// </remarks>
    /// <param name="onProgress">
    /// Куда писать, что происходит с заявкой. У потокового пути это то же место, где обычно
    /// проступает ответ: без единого слова ход выглядел бы зависшим.
    /// </param>
    private async Task<ChatCompletionResponse> RunBatchAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        Action<string>? onProgress,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var payload = BuildPayload(request, model, prepareMessages, stream: false);

        if (OpenRouterBatchPlan.UnsupportedReason(payload.Messages) is { } unsupported)
        {
            throw new VeniceApiException(Loc.Format("S.Batch.Unsupported", unsupported));
        }

        var slug = OpenRouterBatchPlan.SlugFor(model);
        var submit = new OpenRouterBatchSubmit
        {
            Endpoint = OpenRouterBatchPlan.ChatEndpoint,
            Model = slug,
            Requests =
            [
                new OpenRouterBatchItem
                {
                    CustomId = OpenRouterBatchPlan.SingleRequestId,
                    Body = OpenRouterBatchPlan.ToBatchBody(payload, slug)
                }
            ]
        };

        onProgress?.Invoke(Loc.Get("S.Batch.Submitting"));
        var batch = await SubmitBatchAsync(submit, model, credential, cancellationToken)
            .ConfigureAwait(false);

        var id = batch.Id;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new VeniceApiException(
                "OpenRouter batch: заявка принята без идентификатора, спросить о ней нечем. " +
                "Повторите запрос.");
        }

        try
        {
            batch = await AwaitBatchAsync(id, batch, onProgress, credential, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена хода не снимает заявку сама: она уже стоит в очереди провайдера и будет
            // посчитана. Снимаем её отдельно и молча — отменять отмену нечем, а человек и так
            // уже ушёл от этого ответа.
            await CancelBatchQuietlyAsync(id, credential).ConfigureAwait(false);
            throw;
        }

        if (!OpenRouterBatchPlan.IsCompleted(batch.Status))
        {
            throw new VeniceApiException(
                Loc.Format(
                    "S.Batch.Failed",
                    batch.Status ?? "?",
                    OpenRouterBatchPlan.Describe(batch.Error)));
        }

        var answer = OpenRouterBatchPlan.ReadAnswer(batch);
        RecordCost(answer, ChargeSku(model), credential);
        return answer;
    }

    private async Task<OpenRouterBatchObject> SubmitBatchAsync(
        OpenRouterBatchSubmit submit,
        string model,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            submit, VeniceJsonContext.Default.OpenRouterBatchSubmit);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var httpRequest = Request(HttpMethod.Post, "batches", credential);
        httpRequest.Content = content;

        using var response = await _http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        UpdateBalanceFromHeaders(response, credential);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        PerfLog.Write($"batch submit status={(int)response.StatusCode} model={model}");

        if (!response.IsSuccessStatusCode)
        {
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
        }

        return ParseBatch(body);
    }

    /// <summary>
    /// Опрашивает заявку, пока она не придёт к окончательному состоянию.
    /// </summary>
    /// <remarks>
    /// Первые секунды после <c>202</c> заявки ещё нет: очередь её приняла и сохранила, но
    /// спросить о ней нельзя — и <c>GET</c>, и список отвечают 404 «Batch job not found». Это
    /// нормальный ход дела, а не отказ, поэтому 404 внутри окна проявления означает «ещё не
    /// доехала» и опрос продолжается. Без этого первый же опрос обрывал ход ошибкой, которую
    /// человек не мог ни понять, ни обойти.
    /// <para>
    /// Окно отмеряется накопленными перерывами, а не часами: перерывы подменяются в тестах, и
    /// на настоящих часах проверка «заявка так и не проявилась» стоила бы полторы минуты. Со
    /// стороны сети это ещё и правильнее — медленные ответы удлиняют настоящее ожидание, а не
    /// укорачивают его.
    /// </para>
    /// </remarks>
    private async Task<OpenRouterBatchObject> AwaitBatchAsync(
        string id,
        OpenRouterBatchObject batch,
        Action<string>? onProgress,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.Zero;
        var waited = TimeSpan.Zero;
        var landed = false;

        while (!OpenRouterBatchPlan.IsTerminal(batch.Status))
        {
            onProgress?.Invoke(landed ? Describe(batch) : Loc.Get("S.Batch.Landing"));

            delay = OpenRouterBatchPlan.NextDelay(delay);
            waited += delay;
            await BatchDelay(delay, cancellationToken).ConfigureAwait(false);

            var polled = await PollBatchAsync(id, credential, cancellationToken).ConfigureAwait(false);
            if (polled is null)
            {
                // Уже отвечала — значит заявку удалили, и ждать её возвращения нечего.
                if (landed || waited > OpenRouterBatchPlan.VisibilityWindow)
                {
                    throw new VeniceApiException(Loc.Format("S.Batch.Lost", id));
                }

                continue;
            }

            landed = true;
            batch = polled;
        }

        return batch;
    }

    /// <summary>Состояние заявки — либо <c>null</c>, если очередь её пока не видит.</summary>
    private async Task<OpenRouterBatchObject?> PollBatchAsync(
        string id,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        using var httpRequest = Request(
            HttpMethod.Get, "batches/" + Uri.EscapeDataString(id), credential);
        using var response = await _http.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
        }

        return ParseBatch(body);
    }

    /// <summary>
    /// Снимает заявку, о судьбе которой уже никто не спросит.
    /// </summary>
    /// <remarks>
    /// Отдельного описания у этого эндпоинта нет, но состояния <c>cancelling</c> и
    /// <c>cancelled</c> у заявки есть, а сам API повторяет форму OpenAI — значит попытка
    /// уместна. Отказ не важен: худшее, что случится, — заявка доработает и будет посчитана,
    /// то есть ровно то, что было бы без попытки. Токен отмены сюда не передаётся намеренно:
    /// зовут это как раз из обработчика отмены, и общий токен отменил бы саму уборку.
    /// </remarks>
    private async Task CancelBatchQuietlyAsync(string id, ApiCredential credential)
    {
        try
        {
            using var httpRequest = Request(
                HttpMethod.Post, "batches/" + Uri.EscapeDataString(id) + "/cancel", credential);
            using var response = await _http.SendAsync(httpRequest, CancellationToken.None)
                .ConfigureAwait(false);
            PerfLog.Write($"batch cancel status={(int)response.StatusCode}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            PerfLog.Write($"batch cancel failed: {exception.Message}");
        }
    }

    private static OpenRouterBatchObject ParseBatch(string body)
    {
        try
        {
            return JsonSerializer.Deserialize(body, VeniceJsonContext.Default.OpenRouterBatchObject)
                   ?? throw new VeniceApiException("OpenRouter batch: пустой ответ очереди.");
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"OpenRouter batch: ответ очереди не разобрать ({ex.Message}). Body: {Preview(body)}");
        }
    }

    /// <summary>Состояние заявки строкой для человека: сам статус и сколько запросов готово.</summary>
    private static string Describe(OpenRouterBatchObject batch)
    {
        var status = Loc.Get("S.Batch.Status." + (batch.Status ?? "").Trim().ToLowerInvariant(), "");
        if (status.Length == 0)
        {
            status = batch.Status ?? "?";
        }

        var counts = batch.RequestCounts;
        return counts is { Total: > 1 }
            ? Loc.Format("S.Batch.WaitingCounted", status, counts.Completed ?? 0, counts.Total)
            : Loc.Format("S.Batch.Waiting", status);
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
            Cost = answer.Cost?.ToCost() ?? CostOf(answer.Usage),
            PromptTokens = answer.Usage?.PromptTokens ?? 0,
            TotalTokens = answer.Usage?.TotalTokens ?? 0,
            Model = model
        };
    }

    private static VeniceCost CostOf(VeniceUsage? usage) =>
        usage?.Cost is { } usd and > 0m
            ? new VeniceCost { Usd = usd, HasData = true }
            : VeniceCost.Zero;

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
            Usage = request.Usage,
            Plugins = request.Plugins,
            ReasoningChoice = request.ReasoningChoice
        };

    private ChatCompletionRequest BuildPayload(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream)
    {
        var provider = ModelRef.Of(model, _options.Provider);
        return provider == LlmProvider.Venice
            ? BuildVenicePayload(request, model, prepareMessages, stream)
            : BuildOpenRouterPayload(request, model, prepareMessages, stream);
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

    /// <summary>
    /// Читает ответ на GET, попутно обновляя остаток из заголовков. Отказ — исключение
    /// с именем провайдера в тексте: его увидит человек, и «Venice API error» на ключе
    /// OpenRouter сбил бы его с толку.
    /// </summary>
    private async Task<string> GetJsonAsync(
        string path,
        ApiCredential credential,
        bool trackBalance,
        CancellationToken cancellationToken)
    {
        using var httpRequest = Request(HttpMethod.Get, path, credential);
        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        if (trackBalance)
        {
            UpdateBalanceFromHeaders(response, credential);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"{ProviderSpec.For(credential.Provider).Name} API error " +
                $"({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        return body;
    }

    /// <summary>
    /// Остаток по ключу OpenRouter в том же виде, в каком его отдаёт Venice.
    /// </summary>
    /// <remarks>
    /// <c>GET /key</c> и проверяет ключ (неверный отвечает 401), и отдаёт остаток до потолка
    /// трат, если человек его задал. Потолка нет — остаток приходится досчитывать из
    /// <c>GET /credits</c>: вторым запросом, зато только тогда, когда первый не ответил.
    /// </remarks>
    private async Task<VeniceRateLimitsData> GetOpenRouterBalanceAsync(
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var keyBody = await GetJsonAsync("key", credential, trackBalance: false, cancellationToken)
            .ConfigureAwait(false);

        OpenRouterKeyData? key;
        try
        {
            key = JsonSerializer.Deserialize(keyBody, VeniceJsonContext.Default.OpenRouterKeyResponse)?.Data;
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"OpenRouter returned non-JSON key response ({ex.Message}). Body: {Preview(keyBody)}");
        }

        if (key?.LimitRemaining is not null)
        {
            return OpenRouterMapper.ToRateLimits(key, credits: null);
        }

        OpenRouterCreditsData? credits = null;
        try
        {
            var creditsBody = await GetJsonAsync("credits", credential, trackBalance: false, cancellationToken)
                .ConfigureAwait(false);
            credits = JsonSerializer
                .Deserialize(creditsBody, VeniceJsonContext.Default.OpenRouterCreditsResponse)?.Data;
        }
        catch (Exception exception) when (exception is VeniceApiException or JsonException)
        {
            // Ключ уже проверен и годен — не показать остаток хуже, чем объявить ключ плохим.
        }

        return OpenRouterMapper.ToRateLimits(key, credits);
    }

    /// <param name="credentialOverride">
    /// Ключ провайдера, чей каталог нужен. Каталогов теперь несколько — по одному на провайдера,
    /// у которого есть ключ, — потому что слоты моделей выбираются из всех сразу.
    /// </param>
    public async Task<IReadOnlyList<VeniceModelInfo>> ListTextModelsAsync(
        ApiCredential? credentialOverride = null,
        CancellationToken cancellationToken = default)
    {
        var credential = Resolve(credentialOverride);
        if (credential.Provider != LlmProvider.Venice)
        {
            return await ListOpenRouterModelsAsync(credential, cancellationToken).ConfigureAwait(false);
        }

        using var httpRequest = Request(HttpMethod.Get, "models?type=text", credential);

        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        UpdateBalanceFromHeaders(response, credential);
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
    /// Каталог OpenRouter, приведённый к тому же виду, что и каталог Venice.
    /// </summary>
    /// <remarks>
    /// Отбор по <c>supported_parameters=tools</c> делает сам OpenRouter: без вызова
    /// инструментов модель этой программе не нужна вовсе, а список без отбора — под четыре
    /// сотни строк, которые пришлось бы качать и разбирать целиком на каждое обновление.
    /// </remarks>
    private async Task<IReadOnlyList<VeniceModelInfo>> ListOpenRouterModelsAsync(
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var body = await GetJsonAsync(
                "models?supported_parameters=tools", credential, trackBalance: false, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var result = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.OpenRouterModelsResponse)
                ?? throw new VeniceApiException("Empty models response from OpenRouter.");
            return OpenRouterMapper.ToModels(result);
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"OpenRouter returned non-JSON models response ({ex.Message}). Body: {Preview(body)}");
        }
    }

    /// <summary>
    /// Остаток и лимиты ключа. Единственный способ узнать остаток, не потратив ни цента:
    /// заголовки ответа его несут только после настоящего запроса к модели.
    /// </summary>
    /// <param name="credentialOverride">
    /// Чужой ключ — страница настроек показывает остаток и по тем ключам, что сейчас не активны.
    /// Второй <see cref="HttpClient"/> для этого не нужен: и ключ, и адрес едут на самом запросе.
    /// </param>
    public async Task<VeniceRateLimitsData> GetRateLimitsAsync(
        ApiCredential? credentialOverride = null,
        CancellationToken cancellationToken = default)
    {
        var credential = credentialOverride ?? _options.Credential;
        if (credential.Provider != LlmProvider.Venice)
        {
            var open = await GetOpenRouterBalanceAsync(credential, cancellationToken)
                .ConfigureAwait(false);

            // Единственный источник остатка у OpenRouter: заголовков с ним он не шлёт, и без
            // этой строки его ключи не попадали бы в книгу остатков вовсе.
            NoteBalance(credential, open);
            return open;
        }

        using var httpRequest = Request(HttpMethod.Get, "api_keys/rate_limits", credential);
        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);

        UpdateBalanceFromHeaders(response, credential);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        try
        {
            var data = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.VeniceRateLimitsResponse)?.Data
                ?? throw new VeniceApiException("Empty rate limits response from Venice API.");
            NoteBalance(credential, data);
            return data;
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
        ApiCredential? credentialOverride = null,
        CancellationToken cancellationToken = default)
    {
        var credential = credentialOverride ?? _options.Credential;
        if (credential.Provider != LlmProvider.Venice)
        {
            // Не поломка, а свойство провайдера: журнала списаний у него нет вовсе. Тем же
            // исключением, что и отказ Venice без админ-ключа, — вызывающий на оба отвечает
            // одинаково, переходом на собственный журнал программы.
            throw new VeniceAdminKeyRequiredException(
                $"{ProviderSpec.For(credential.Provider).Name} не отдаёт журнал списаний.");
        }

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
            HttpMethod.Get, "billing/usage-history?" + string.Join("&", query), credential);
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

    private void RecordCost(ChatCompletionResponse result, string sku, ApiCredential credential)
    {
        if (result.Cost is not null)
        {
            AddCost(result.Cost.ToCost(), sku, credential);
            return;
        }

        // Своего поля цены у OpenRouter нет — он кладёт её в usage, и только если просили
        // (см. UsageAccounting). Без этой ветки деньги за ответ не попали бы в журнал трат
        // вовсе, и график по ключу OpenRouter остался бы пустым.
        if (result.Usage?.Cost is { } usd and > 0m)
        {
            AddCost(new VeniceCost { Usd = usd, HasData = true }, sku, credential);
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
    /// <param name="credential">
    /// Ключ, которым за это заплатили. Именно он, а не выбранный ключ программы: слоты моделей
    /// платят разными ключами, а рисование картинок и чтение страниц — всегда ключом Venice.
    /// Прежде списание записывалось на выбранный ключ, и деньги ключа Venice за картинку
    /// оказывались в журнале ключа OpenRouter — график врал у обоих.
    /// </param>
    private void AddCost(VeniceCost cost, string sku, ApiCredential credential)
    {
        lock (_costGate)
        {
            RequestCost = RequestCost.Add(cost);
        }

        // Собственный журнал трат: Venice свой отдаёт только админ-ключу, а работают
        // обычным. Здесь же, в единственной точке учёта, видны все деньги программы сразу.
        _options.SpendSink?.Invoke(credential.Secret, cost, sku);

        // Свой счёт у каждого хода чата: один общий RequestCost на несколько одновременных
        // ходов не делится. Сам он остаётся — им пользуется агент, у которого клиент на прогон.
        VeniceTurnScope.Current?.Add(cost);
        AgentRunScope.Charge(cost);
    }

    /// <summary>
    /// Ключ Venice для того, что умеет только Venice. Пустой — значит такого ключа в программе
    /// нет вовсе.
    /// </summary>
    private ApiCredential VeniceCredential() => _options.VeniceCredential;

    /// <summary>
    /// Отказ, который читает модель.
    /// </summary>
    /// <remarks>
    /// Пишем ей, что делать дальше, а не только что пошло не так: иначе она повторяет вызов
    /// раунд за раундом, пока не кончатся попытки, и человек платит за каждый.
    /// </remarks>
    private static ApiCredential RequireVenice(ApiCredential credential, string what)
    {
        if (credential.IsEmpty)
        {
            throw new VeniceApiException(
                $"{what} работает только через Venice, а ключа Venice в программе нет. " +
                "Скажи пользователю добавить ключ Venice на странице настроек «Key & Info». " +
                "Не повторяй этот вызов.");
        }

        return credential;
    }

    public async Task<string> ScrapeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var credential = RequireVenice(VeniceCredential(), "Чтение страниц");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ScrapeUrlRequest { Url = url },
            VeniceJsonContext.Default.ScrapeUrlRequest);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = Request(HttpMethod.Post, "augment/scrape", credential);
        httpRequest.Content = content;
        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        UpdateBalanceFromHeaders(response, credential);
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

        AddCost(new VeniceCost { Usd = 0.01m, HasData = true }, VeniceSku.Scrape, credential);
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

        var credential = RequireVenice(VeniceCredential(), "Рисование картинок");
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
        using var httpRequest = Request(HttpMethod.Post, "image/generate", credential);
        httpRequest.Content = content;

        var balanceBefore = LastBalance?.Usd;

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        UpdateBalanceFromHeaders(response, credential);
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

        AddCost(
            PriceImage(result!.Cost, balanceBefore, LastBalance?.Usd),
            resolved + "-image",
            credential);
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

    /// <summary>
    /// Чем платить за поиск в сети.
    /// </summary>
    /// <remarks>
    /// Поиск оплачивается токенами модели, которая его ведёт, — значит и ключом той же модели.
    /// Порядок такой:
    /// <list type="number">
    /// <item>ключ хода чата — он снят при его начале и съезжает вместе с моделью;</item>
    /// <item>ключ самой копии настроек, если он того же провайдера: у прогона агента ход
    /// подавлен (<c>VeniceTurnScope.Suppress</c>), и это единственное место, где виден ключ,
    /// назначенный слоту агента;</item>
    /// <item>ключ провайдера по умолчанию — когда клиент платит другому серверу.</item>
    /// </list>
    /// Без второго шага поиск внутри агента уходил с ключом провайдера по умолчанию, и его
    /// деньги попадали в журнал чужого ключа.
    /// </remarks>
    private ApiCredential SearchCredential(VeniceTurnContext? turn, LlmProvider provider)
    {
        if (turn?.Credential is { IsEmpty: false } fromTurn && fromTurn.Provider == provider)
        {
            return fromTurn;
        }

        var own = _options.Credential;
        if (own.Provider == provider && !own.IsEmpty)
        {
            return own;
        }

        return _options.Keys?.DefaultFor(provider) ?? own;
    }

    /// <param name="plan">
    /// Что человек выбрал для поиска: закреплённый провайдер с ключом (модель под него уже
    /// подобрана в <see cref="ModelSlots.WebSearch"/>) и движок OpenRouter. Пусто — искать
    /// моделью хода умолчательным движком, как было до появления настройки.
    /// </param>
    public async Task<string> SearchWebAsync(
        string query,
        WebSearchPlan? plan = null,
        CancellationToken cancellationToken = default)
    {
        // Поиск раньше «случайно» попадал на модель чата: её только что записал SetActiveModel.
        // Теперь поле клиента не дрейфует, поэтому модель хода берём из его собственного контекста.
        // Модель хода, а вне хода — своя, подобранная под провайдера: в _options.Model лежит
        // идентификатор из appsettings.json, то есть модель Venice, и на ключе OpenRouter
        // поиск уходил бы к модели, которой там нет.
        var turn = VeniceTurnScope.Current;

        // Пусто и «авто» — одно состояние: пусто приходит из нетронутых настроек, «авто» —
        // из плашки, где человек вернул выбор обратно.
        var fixedSearch = plan is { FollowsTurn: false } pinned ? pinned.Binding : (ModelBinding?)null;

        var model = fixedSearch?.ModelId
                    ?? turn?.ModelId
                    ?? ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Chat, _options.Model);
        var provider = ModelRef.Of(model, _options.Provider);

        // Закреплённый поиск платит своим ключом: он и выбирался ради того, чтобы деньги за
        // интернет шли с названного счёта, а не с того, на котором идёт разговор.
        var credential = fixedSearch is { } target
            ? RequireKey(_options.Keys?.CredentialFor(target) ?? _options.Credential, model)
            : SearchCredential(turn, provider);

        // Своя поисковая надстройка у каждого провайдера: у Venice — venice_parameters,
        // у OpenRouter — плагин. Поиск xAI живёт только у Venice.
        var useXSearch = provider == LlmProvider.Venice &&
                         (_options.EnableXSearch
                          ?? model.Contains("grok", StringComparison.OrdinalIgnoreCase));

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
                VeniceParameters = provider == LlmProvider.Venice
                    ? new VeniceParameters
                    {
                        IncludeVeniceSystemPrompt = false,
                        EnableWebSearch = "on",
                        EnableWebCitations = _options.EnableWebCitations,
                        EnableXSearch = useXSearch ? true : null
                    }
                    : null,
                // Движок — только у OpenRouter: у Venice поиск один, и лишние поля он
                // отвергает вместе со всем запросом.
                Plugins = provider == LlmProvider.Venice
                    ? null
                    :
                    [
                        new RequestPlugin
                        {
                            Id = "web",
                            MaxResults = 5,
                            Engine = plan?.Engine,
                            Mode = plan?.Mode
                        }
                    ]
            }, cancellationToken, credential).ConfigureAwait(false);

            var message = response.Choices.FirstOrDefault()?.Message;
            var text = ReasoningSplit.Split(ChatContent.ReadText(message?.Content) ?? "").Answer;
            text = WithCitations(text, message?.Annotations);
            return string.IsNullOrWhiteSpace(text) ? "Результаты поиска не найдены." : text;
        }
    }

    /// <summary>
    /// Дописывает к ответу ссылки, которые провайдер приложил отдельным полем.
    /// </summary>
    /// <remarks>
    /// Адреса — весь смысл поиска: зовёт его модель, которая умеет открыть страницу или забрать
    /// картинку, но только если ей дали адрес. Просьбы в системном промпте недостаточно —
    /// OpenRouter кладёт найденное в <c>annotations</c>, и модель, отвечающая по этим ссылкам,
    /// пересказать их текстом забывает. Уже названные в ответе не повторяем.
    /// </remarks>
    private static string WithCitations(string text, JsonElement? annotations)
    {
        if (annotations is not { ValueKind: JsonValueKind.Array } list)
        {
            return text;
        }

        var links = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (!item.TryGetProperty("url_citation", out var citation) ||
                !citation.TryGetProperty("url", out var url) ||
                url.GetString() is not { Length: > 0 } address ||
                links.Contains(address, StringComparer.OrdinalIgnoreCase) ||
                text.Contains(address, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            links.Add(address);
        }

        if (links.Count == 0)
        {
            return text;
        }

        var header = text.Contains("Ссылки:", StringComparison.Ordinal)
            ? Environment.NewLine
            : Environment.NewLine + Environment.NewLine + "Ссылки:" + Environment.NewLine;
        return text + header + string.Join(Environment.NewLine, links);
    }

    /// <param name="credential">
    /// Чей это остаток. Нужен книге остатков: заголовки приходят на ответах всех ключей, и
    /// без имени владельца сумма по ключам не складывается.
    /// </param>
    private void UpdateBalanceFromHeaders(HttpResponseMessage response, ApiCredential credential)
    {
        var usd = TryReadHeaderDecimal(response, "x-venice-balance-usd");
        var diem = TryReadHeaderDecimal(response, "x-venice-balance-diem");

        if (usd is null && diem is null)
        {
            return;
        }

        var balance = new VeniceBalance
        {
            CanConsume = true,
            Usd = usd,
            Diem = diem
        };

        _options.BalanceSink?.Invoke(credential, balance);

        // Поле клиента — остаток выбранного ключа и только его: на нём стоит запасной путь
        // страницы настроек, и чужая цифра там читалась бы как деньги человека.
        if (IsSelectedKey(credential))
        {
            LastBalance = balance;
        }
    }

    /// <summary>
    /// Остаток, названный провайдером в ответе на прямой запрос.
    /// </summary>
    /// <remarks>
    /// Доллары складываются с пакетными кредитами: это одни и те же деньги, номинированные
    /// в долларах, и показывать их двумя числами значило бы спрашивать человека, какое из них
    /// его остаток. DIEM живёт отдельно — у него свой курс.
    /// </remarks>
    private void NoteBalance(ApiCredential credential, VeniceRateLimitsData limits)
    {
        if (_options.BalanceSink is not { } sink || limits.Balances is not { } balances)
        {
            return;
        }

        sink(credential, new VeniceBalance
        {
            CanConsume = limits.AccessPermitted,
            Usd = (balances.Usd ?? 0m) + (balances.BundledCredits ?? 0m),
            Diem = balances.Diem
        });
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