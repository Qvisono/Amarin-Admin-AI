using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
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
}
