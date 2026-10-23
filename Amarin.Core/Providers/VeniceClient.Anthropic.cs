using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Запросы родным протоколом Anthropic (Messages API).
/// </summary>
/// <remarks>
/// Снаружи их не отличить от OpenAI-совместимых: тот же запрос на входе, тот же ответ и тот же
/// накопитель потока на выходе (<see cref="AnthropicMessages"/>, <see cref="AnthropicStream"/>).
/// Ключ и адрес садятся на запрос тем же <see cref="Request"/>, что и у всех.
/// </remarks>
public sealed partial class VeniceClient
{
    /// <summary>Идёт ли запрос к этой модели протоколом Anthropic.</summary>
    /// <remarks>
    /// У своего сервера, понимающего оба протокола, — по модели: Claude идёт родным протоколом
    /// (там подписанное размышление), остальные модели — OpenAI-совместимым.
    /// </remarks>
    private static bool UsesAnthropic(string model, ApiCredential credential)
    {
        if (credential.Provider.IsCustom && credential.Endpoint is { } endpoint)
        {
            return endpoint.UsesAnthropicFor(ModelRef.Bare(model));
        }

        return ProviderSpec.For(ModelRef.Of(model, credential.Provider)).Protocol == WireProtocol.AnthropicMessages;
    }

    private async Task<ChatCompletionResponse> SendAnthropicAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        using var response = await PostAnthropicWithRelaxAsync(
                request, model, prepareMessages, stream: false, credential, cancellationToken)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        ChatCompletionResponse result;
        try
        {
            result = AnthropicMessages.ParseResponse(body);
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"Anthropic returned non-JSON response ({ex.Message}). Body: {ProviderErrorText.Detail(body)}");
        }

        RecordCost(result, model, credential);
        return result;
    }

    private async Task<StreamedChatCompletion> StreamAnthropicAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        Action<string>? onText,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        using var response = await PostAnthropicWithRelaxAsync(
                request, model, prepareMessages, stream: true, credential, cancellationToken)
            .ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var accumulator = new ChatStreamAccumulator();
        var translator = new AnthropicStream();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            // Строки «event:» повторяют тип, который есть и в самих данных, — читаем только данные.
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ChatCompletionChunk? chunk;
            try
            {
                chunk = translator.Translate(line[5..].TrimStart());
            }
            catch (JsonException ex)
            {
                throw new VeniceApiException($"Anthropic returned non-JSON stream event ({ex.Message}).");
            }

            if (chunk is not null && accumulator.Apply(chunk))
            {
                onText?.Invoke(accumulator.Text);
            }
        }

        var cost = CostOf(
            accumulator.Cost, model, accumulator.PromptTokens, accumulator.CompletionTokens, accumulator.ServiceTier);
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
            Model = model,
            ReplayBlocks = translator.ReplayBlocks
        };
    }

    /// <summary>
    /// Отправляет запрос; на отказ из-за полей размышления — один повтор без них.
    /// </summary>
    /// <remarks>
    /// Поля размышления у моделей разных поколений приняты по-разному, а посредник человека
    /// может не знать части из них. Ответ без размышления лучше, чем никакого.
    /// </remarks>
    private async Task<HttpResponseMessage> PostAnthropicWithRelaxAsync(
        ChatCompletionRequest request,
        string model,
        bool prepareMessages,
        bool stream,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var shaped = prepareMessages
            ? new ChatCompletionRequest
            {
                Model = request.Model,
                Messages = ApiContextLimiter.Prepare(request.Messages),
                Tools = request.Tools,
                ToolChoice = request.ToolChoice,
                ReasoningChoice = request.ReasoningChoice
            }
            : request;

        var info = ResolveModelInfo?.Invoke(model);
        var official = !credential.Provider.IsCustom;
        var bare = ModelRef.Bare(model);

        var response = await PostAnthropicAsync(
                AnthropicMessages.Build(shaped, bare, info, stream, official), model, credential, cancellationToken)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var status = (int)response.StatusCode;
        var error = ExtractErrorMessage(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        response.Dispose();

        if (!AnthropicMessages.MayRelax(status, error))
        {
            throw ApiError(credential, status, error);
        }

        var retry = await PostAnthropicAsync(
                AnthropicMessages.Build(shaped, bare, info, stream, official, relaxed: true),
                model,
                credential,
                cancellationToken)
            .ConfigureAwait(false);
        if (retry.IsSuccessStatusCode)
        {
            return retry;
        }

        status = (int)retry.StatusCode;
        error = ExtractErrorMessage(await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        retry.Dispose();
        throw ApiError(credential, status, error);
    }

    private async Task<HttpResponseMessage> PostAnthropicAsync(
        AnthropicBody body,
        string model,
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var content = new StringContent(body.Json.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        var httpRequest = Request(HttpMethod.Post, "messages", credential);
        httpRequest.Content = content;
        if (body.Betas is { Length: > 0 } betas)
        {
            httpRequest.Headers.Add("anthropic-beta", betas);
        }

        var watch = Stopwatch.StartNew();
        var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        PerfLog.Write(
            $"llm provider={credential.Provider} protocol=anthropic http_ms={watch.ElapsedMilliseconds} " +
            $"status={(int)response.StatusCode} model={model}");
        return response;
    }
}
