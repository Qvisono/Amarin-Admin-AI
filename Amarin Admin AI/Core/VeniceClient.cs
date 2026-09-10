using System.Diagnostics;
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

    public event Action<string, string>? ModelFallback;

    /// <summary>
    /// Looks up <c>/models</c> capabilities so fallback can drop <c>reasoning_effort</c> on
    /// models that reject it (Grok). Null until the catalogue has been loaded.
    /// </summary>
    public Func<string, VeniceModelInfo?>? ResolveModelInfo { get; set; }

    public string ActiveModel => _options.Model;

    public void SetActiveModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        var normalized = model.Trim();
        _options.Model = normalized;
        _primaryModel = normalized;
    }

    public VeniceClient(HttpClient http, AgentOptions options)
    {
        _http = http;
        _options = options;
        _primaryModel = options.Model;
        _http.BaseAddress ??= new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization ??=
            new AuthenticationHeaderValue("Bearer", _options.ApiKey);
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
        var startingModel = _options.Model;
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

    public Task<StreamedChatCompletion> StreamChatCompletionAsync(
        IReadOnlyList<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        VeniceParameters veniceParameters,
        Action<string>? onText,
        CancellationToken cancellationToken = default,
        ReasoningChoice? reasoning = null) =>
        StreamChatCompletionAsync(new ChatCompletionRequest
        {
            Model = _options.Model,
            Messages = messages.ToList(),
            Tools = tools,
            ToolChoice = toolChoice,
            Stream = true,
            VeniceParameters = veniceParameters,
            ReasoningChoice = reasoning
        }, prepareMessages: true, onText, cancellationToken);

    private async Task<StreamedChatCompletion> StreamChatCompletionAsync(
        ChatCompletionRequest request,
        bool prepareMessages,
        Action<string>? onText,
        CancellationToken cancellationToken)
    {
        var startingModel = _options.Model;
        VeniceApiException? lastOverload = null;

        foreach (var model in VeniceModelFallback.GetModelsFrom(startingModel, _primaryModel))
        {
            try
            {
                var result = await SendChatCompletionStreamingAsync(
                        request, model, prepareMessages, onText, cancellationToken)
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

                return ReadCompletion(body);
            }

            throw new VeniceApiException($"Venice API error ({(int)response.StatusCode}): {error}");
        }

        return ReadCompletion(body);
    }

    private ChatCompletionResponse ReadCompletion(string body)
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

        RecordCost(result);
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

                var added = accumulator.Apply(chunk);
                if (added)
                {
                    onText?.Invoke(accumulator.Text);
                }
            }

            if (accumulator.Cost.HasData)
            {
                AddCost(accumulator.Cost);
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
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

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
            ReasoningEffort = effort,
            Reasoning = reasoning,
            VeniceParameters = venice
        };
    }

    public async Task<IReadOnlyList<VeniceModelInfo>> ListTextModelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, "models?type=text")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

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

    public void ResetRequestCost()
    {
        lock (_costGate)
        {
            RequestCost = VeniceCost.Zero;
        }
    }

    private void RecordCost(ChatCompletionResponse result)
    {
        if (result.Cost is not null)
        {
            AddCost(result.Cost.ToCost());
        }
    }

    /// <summary>
    /// The one way money is booked. Tool calls in a round run in parallel and share this client,
    /// so the running total needs a gate; the same charge is also billed to whichever tool call
    /// is on the stack, which is what puts a price tag next to generate_image in the transcript.
    /// </summary>
    private void AddCost(VeniceCost cost)
    {
        lock (_costGate)
        {
            RequestCost = RequestCost.Add(cost);
        }

        AgentRunScope.Charge(cost);
    }

    public async Task<string> ScrapeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ScrapeUrlRequest { Url = url },
            VeniceJsonContext.Default.ScrapeUrlRequest);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "augment/scrape")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };
        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

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

        AddCost(new VeniceCost { Usd = 0.01m, HasData = true });
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
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "image/generate")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        var balanceBefore = LastBalance?.Usd;

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

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

        AddCost(PriceImage(result!.Cost, balanceBefore, LastBalance?.Usd));
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
        var useXSearch = _options.EnableXSearch
            ?? _options.Model.Contains("grok", StringComparison.OrdinalIgnoreCase);

        var response = await CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = _options.Model,
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
                        "description like 'ищи по тегу X on site Y' — give the address itself.\n" +
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
        }, cancellationToken);

        var text = ReasoningSplit.Split(
            ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;
        return string.IsNullOrWhiteSpace(text) ? "Результаты поиска не найдены." : text;
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

public sealed class VeniceApiException(string message) : Exception(message);