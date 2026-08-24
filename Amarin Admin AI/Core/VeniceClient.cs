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

    public event Action<string, string>? ModelFallback;

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
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public Task<ChatCompletionResponse> CreateChatCompletionAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        VeniceParameters veniceParameters,
        CancellationToken cancellationToken = default) =>
        CreateChatCompletionAsync(new ChatCompletionRequest
        {
            Model = model,
            Messages = messages.ToList(),
            Tools = tools,
            ToolChoice = toolChoice,
            VeniceParameters = veniceParameters
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

    private async Task<ChatCompletionResponse> SendChatCompletionAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        CancellationToken cancellationToken)
    {
        var payload = prepareMessages
            ? new ChatCompletionRequest
            {
                Model = model,
                Messages = ApiContextLimiter.Prepare(request.Messages),
                Tools = request.Tools,
                ToolChoice = request.ToolChoice,
                Temperature = request.Temperature,
                VeniceParameters = request.VeniceParameters
            }
            : new ChatCompletionRequest
            {
                Model = model,
                Messages = request.Messages,
                Tools = request.Tools,
                ToolChoice = request.ToolChoice,
                Temperature = request.Temperature,
                VeniceParameters = request.VeniceParameters
            };

        var serializeWatch = Stopwatch.StartNew();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, VeniceJsonContext.Default.ChatCompletionRequest);
        var serializeMs = serializeWatch.ElapsedMilliseconds;

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher
        };

        var httpWatch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken)
            .ConfigureAwait(false);
        var httpMs = httpWatch.ElapsedMilliseconds;
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        PerfLog.Write(
            $"venice serialize_ms={serializeMs} http_ms={httpMs} status={(int)response.StatusCode} model={model}");

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

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

    public void ResetRequestCost() => RequestCost = VeniceCost.Zero;

    private void RecordCost(ChatCompletionResponse result)
    {
        if (result.Cost is null)
        {
            return;
        }

        RequestCost = RequestCost.Add(result.Cost.ToCost());
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

        RequestCost = RequestCost.Add(new VeniceCost { Usd = 0.01m, HasData = true });
        return string.IsNullOrWhiteSpace(markdown) ? "Страница пуста или контент не извлечён." : markdown;
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
                    Content = ChatContent.Text(
                        "You are a web research assistant. Search the web and return a concise summary in Russian " +
                        "with practical fixes and source references when available.")
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

        var text = ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content);
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