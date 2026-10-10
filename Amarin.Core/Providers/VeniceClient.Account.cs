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
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
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
        var spec = ProviderSpec.Of(credential);
        if (spec.Protocol == WireProtocol.AnthropicMessages)
        {
            return await ListAnthropicModelsAsync(credential, cancellationToken).ConfigureAwait(false);
        }

        switch (spec.Dialect)
        {
            case PayloadDialect.OpenRouter:
                return await ListOpenRouterModelsAsync(credential, cancellationToken).ConfigureAwait(false);
            case PayloadDialect.Generic:
                return await ListGenericModelsAsync(credential, cancellationToken).ConfigureAwait(false);
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
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
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

    /// <summary>Каталог OpenAI-совместимого сервера без надстроек.</summary>
    private async Task<IReadOnlyList<VeniceModelInfo>> ListGenericModelsAsync(
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var body = await GetJsonAsync("models", credential, trackBalance: false, cancellationToken)
            .ConfigureAwait(false);
        return ParseModelList(body, credential);
    }

    /// <summary>
    /// Каталог Anthropic. Постранично: <c>/v1/models</c> отдаёт не больше тысячи моделей за раз.
    /// </summary>
    private async Task<IReadOnlyList<VeniceModelInfo>> ListAnthropicModelsAsync(
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var models = new List<VeniceModelInfo>();
        string? after = null;

        // Потолок страниц — от зацикленного ответа: сервер, повторяющий тот же last_id, иначе
        // держал бы плашку на «Загрузка…» до срока.
        for (var page = 0; page < 20; page++)
        {
            var path = after is null ? "models?limit=1000" : "models?limit=1000&after_id=" + Uri.EscapeDataString(after);
            var body = await GetJsonAsync(path, credential, trackBalance: false, cancellationToken)
                .ConfigureAwait(false);
            models.AddRange(ParseModelList(body, credential));

            var next = ModelListParser.NextPageAfter(body);
            if (next is null || next == after)
            {
                break;
            }

            after = next;
        }

        return models;
    }

    private static IReadOnlyList<VeniceModelInfo> ParseModelList(string body, ApiCredential credential)
    {
        try
        {
            return ModelListParser.Parse(body, credential.Provider);
        }
        catch (JsonException ex)
        {
            throw new VeniceApiException(
                $"{ProviderSpec.Of(credential).Name} returned non-JSON models response " +
                $"({ex.Message}). Body: {Preview(body)}");
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
        var spec = ProviderSpec.Of(credential);
        if (spec.Balance == BalanceSource.OpenRouterCredits)
        {
            var open = await GetOpenRouterBalanceAsync(credential, cancellationToken)
                .ConfigureAwait(false);

            // Единственный источник остатка у OpenRouter: заголовков с ним он не шлёт, и без
            // этой строки его ключи не попадали бы в книгу остатков вовсе.
            NoteBalance(credential, open);
            return open;
        }

        if (spec.Balance != BalanceSource.VeniceRateLimits)
        {
            var other = await GetOtherBalanceAsync(credential, spec, cancellationToken).ConfigureAwait(false);
            NoteBalance(credential, other);
            return other;
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
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
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
    /// Остаток у провайдера, который не Venice и не OpenRouter.
    /// </summary>
    /// <remarks>
    /// Провайдер без такого запроса — <see cref="BalanceNotReportedException"/>: это его свойство,
    /// а не отказ ключа, и спрашивать снова после каждого ответа незачем. Шлюз перепродавца,
    /// который ответил, но остатка не назвал, — тот же случай.
    /// </remarks>
    private async Task<VeniceRateLimitsData> GetOtherBalanceAsync(
        ApiCredential credential,
        ProviderSpec spec,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (spec.Balance)
            {
                case BalanceSource.DeepSeekBalance:
                    return ProviderBalances.FromDeepSeek(
                        await GetJsonAsync("user/balance", credential, trackBalance: false, cancellationToken)
                            .ConfigureAwait(false));

                case BalanceSource.MoonshotBalance:
                    return ProviderBalances.FromMoonshot(
                        await GetJsonAsync("users/me/balance", credential, trackBalance: false, cancellationToken)
                            .ConfigureAwait(false));

                case BalanceSource.ResellerBilling:
                    var subscription = await GetJsonAsync(
                            "dashboard/billing/subscription", credential, trackBalance: false, cancellationToken)
                        .ConfigureAwait(false);
                    var today = DateTime.UtcNow.Date;
                    var usage = await GetJsonAsync(
                            "dashboard/billing/usage?start_date=" + today.AddDays(-90).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                            "&end_date=" + today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            credential,
                            trackBalance: false,
                            cancellationToken)
                        .ConfigureAwait(false);
                    return ProviderBalances.FromResellerBilling(subscription, usage)
                           ?? throw new BalanceNotReportedException($"{spec.Name} не сообщает остаток на ключе.");
            }
        }
        catch (JsonException)
        {
            throw new BalanceNotReportedException($"{spec.Name} не сообщает остаток на ключе.");
        }
        catch (VeniceApiException exception) when (
            spec.Balance == BalanceSource.ResellerBilling && exception.StatusCode is 404 or 405 or 501)
        {
            // Шлюз без этого адреса — не отказ ключа: такой перепродавец показывает остаток
            // только у себя на сайте.
            throw new BalanceNotReportedException($"{spec.Name} не сообщает остаток на ключе.");
        }

        // Не отказ ключа, а свойство провайдера: спрашивать остаток не у кого.
        throw new BalanceNotReportedException($"{spec.Name} не сообщает остаток на ключе.");
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
        if (!ProviderSpec.Of(credential).HasUsageHistory)
        {
            // Не поломка, а свойство провайдера: журнала списаний у него нет вовсе. Тем же
            // исключением, что и отказ Venice без админ-ключа, — вызывающий на оба отвечает
            // одинаково, переходом на собственный журнал программы.
            throw new VeniceAdminKeyRequiredException(
                $"{ProviderSpec.Of(credential).Name} не отдаёт журнал списаний.");
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
                    $"Venice API error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}")
                {
                    StatusCode = (int)response.StatusCode
                };
            }

            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
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

    /// <remarks>
    /// Через <see cref="ProviderErrorText.Detail"/>: вместо JSON приходит обычно страница защиты
    /// сайта, и её разметка в сообщении ничего не объясняет.
    /// </remarks>
    private static string Preview(string body) =>
        string.IsNullOrWhiteSpace(body) ? "(empty body)" : ProviderErrorText.Detail(body);
}
