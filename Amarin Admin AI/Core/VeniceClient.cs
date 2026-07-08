using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

public sealed class VeniceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly AgentOptions _options;

    public VeniceBalance? LastBalance { get; private set; }

    public VeniceCost RequestCost { get; private set; } = VeniceCost.Zero;

    public VeniceClient(HttpClient http, AgentOptions options)
    {
        _http = http;
        _options = options;
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

    public async Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken = default) =>
        await CreateChatCompletionAsync(request, prepareMessages: false, cancellationToken);

    private async Task<ChatCompletionResponse> CreateChatCompletionAsync(
        ChatCompletionRequest request,
        bool prepareMessages,
        CancellationToken cancellationToken)
    {
        var json = await Task.Run(() =>
        {
            var payload = prepareMessages
                ? new ChatCompletionRequest
                {
                    Model = request.Model,
                    Messages = ApiContextLimiter.Prepare(request.Messages),
                    Tools = request.Tools,
                    ToolChoice = request.ToolChoice,
                    Temperature = request.Temperature,
                    VeniceParameters = request.VeniceParameters
                }
                : request;

            return JsonSerializer.Serialize(payload, JsonOptions);
        }, cancellationToken).ConfigureAwait(false);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("chat/completions", content, cancellationToken)
            .ConfigureAwait(false);
        UpdateBalanceFromHeaders(response);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        ChatCompletionResponse result;
        try
        {
            result = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions)
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
        var json = JsonSerializer.Serialize(new { url }, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("augment/scrape", content, cancellationToken);
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
            var error = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
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