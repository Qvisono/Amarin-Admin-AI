using System.Text;

namespace Amarin.Core;

/// <summary>Чем кончилась проверка своего сервера.</summary>
internal enum ServerProbeFailure
{
    None,

    /// <summary>Адрес не похож на адрес API или небезопасен.</summary>
    BadAddress,

    /// <summary>Сервер ответил, что ключ не годится.</summary>
    KeyRejected,

    /// <summary>По адресу нет ни списка моделей, ни известного протокола.</summary>
    NotAnApi,

    /// <summary>Сервер не ответил.</summary>
    Unreachable
}

/// <summary>Итог проверки: адрес и протоколы сервера либо причина отказа.</summary>
internal sealed record ServerProbe(ProviderEndpoint? Endpoint, ServerProbeFailure Failure, string? Detail = null);

public sealed partial class VeniceClient
{
    /// <summary>
    /// Проверяет ключ перед сохранением тем запросом, который у провайдера есть.
    /// </summary>
    /// <remarks>
    /// У Venice и OpenRouter — остаток, как и прежде: он же отличает неверный ключ. У остальных —
    /// список моделей: он требует ключа у всех, а остатка у большинства нет вовсе.
    /// </remarks>
    /// <exception cref="VeniceApiException">Ключ отвергнут или сервер не ответил как надо.</exception>
    internal async Task CheckKeyAsync(ApiCredential credential, CancellationToken cancellationToken)
    {
        var spec = ProviderSpec.Of(credential);
        if (spec.Balance is BalanceSource.VeniceRateLimits or BalanceSource.OpenRouterCredits)
        {
            await GetRateLimitsAsync(credential, cancellationToken).ConfigureAwait(false);
            return;
        }

        await ListTextModelsAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Узнаёт у своего сервера, где живёт его API и какими протоколами он говорит.
    /// </summary>
    /// <remarks>
    /// Ни один запрос модель не запускает: список моделей бесплатен, а пробы протоколов идут с
    /// пустым телом — сервер, который протокол знает, отвечает на них 400 «нет поля model», а
    /// который не знает — 404. Адрес пробуется и как дан, и с <c>/v1</c>: перепродавцы пишут его
    /// на сайте по-разному (у ai.starimg.ru <c>/models</c> — 404, <c>/v1/models</c> — есть).
    /// </remarks>
    internal async Task<ServerProbe> ProbeServerAsync(string address, string secret, CancellationToken cancellationToken)
    {
        if (EndpointAddress.Normalize(address, out _) is not { } normalized)
        {
            return new ServerProbe(null, ServerProbeFailure.BadAddress);
        }

        var name = EndpointAddress.HostOf(normalized);
        string? lastDetail = null;
        var reached = false;

        foreach (var candidate in EndpointAddress.Candidates(normalized))
        {
            var credential = ProbeCredential(name, candidate, secret);
            int status;
            string body;
            try
            {
                (status, body) = await SendProbeAsync(HttpMethod.Get, "models", credential, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                lastDetail = exception.Message;
                continue;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastDetail = "timeout";
                continue;
            }

            reached = true;
            if (status is 401 or 403)
            {
                return new ServerProbe(null, ServerProbeFailure.KeyRejected, ExtractErrorMessage(body));
            }

            if (status is < 200 or >= 300)
            {
                lastDetail = $"{status}: {ExtractErrorMessage(body)}";
                continue;
            }

            var listed = ModelListParser.ProtocolOf(body);
            var openAi = await AnswersProtocolAsync("chat/completions", credential, cancellationToken).ConfigureAwait(false);
            var anthropic = await AnswersProtocolAsync("messages", credential, cancellationToken).ConfigureAwait(false);

            // Список ответил, а пробы промолчали (шлюз отвечает 200 на всё или прячет 404):
            // верим форме списка, а без неё — самому распространённому, OpenAI.
            if (!openAi && !anthropic)
            {
                openAi = listed != WireProtocol.AnthropicMessages;
                anthropic = listed == WireProtocol.AnthropicMessages;
            }

            return new ServerProbe(new ProviderEndpoint(name, candidate, openAi, anthropic), ServerProbeFailure.None);
        }

        return new ServerProbe(null, reached ? ServerProbeFailure.NotAnApi : ServerProbeFailure.Unreachable, lastDetail);
    }

    private static ApiCredential ProbeCredential(string name, string baseUrl, string secret) =>
        new(LlmProvider.Custom("probe"), secret, new ProviderEndpoint(name, baseUrl, OpenAiChat: true, AnthropicMessages: true));

    /// <summary>Знает ли сервер этот протокол: на пустое тело он отвечает «нет полей», а не «нет пути».</summary>
    private async Task<bool> AnswersProtocolAsync(string path, ApiCredential credential, CancellationToken cancellationToken)
    {
        try
        {
            var (status, _) = await SendProbeAsync(HttpMethod.Post, path, credential, "{}", cancellationToken)
                .ConfigureAwait(false);
            return status is 400 or 422;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<(int Status, string Body)> SendProbeAsync(
        HttpMethod method,
        string path,
        ApiCredential credential,
        string? json,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));

        using var request = Request(method, path, credential);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        return ((int)response.StatusCode, body);
    }
}
