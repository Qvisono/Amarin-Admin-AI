using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Файл, приложенный к релизу.</summary>
/// <param name="Sha256">
/// Контрольная сумма от GitHub шестнадцатеричной строкой; <c>null</c>, если API её не дал.
/// </param>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>Релиз на GitHub: то немногое из ответа API, что нужно приложению.</summary>
public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string PageUrl,
    string? Title,
    DateTimeOffset? Published,
    IReadOnlyList<ReleaseAsset> Assets)
{
    /// <summary>
    /// Готовая сборка для Windows. Релиз этой программы — один самодостаточный exe, поэтому
    /// берётся первый подходящий: сначала помеченный архитектурой, потом любой .exe.
    /// </summary>
    public ReleaseAsset? WindowsBuild =>
        Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase))
        ?? Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Итог проверки. Ошибка сети — это не исключение, а обычный ответ с текстом.</summary>
public sealed record UpdateCheckResult
{
    public ReleaseInfo? Latest { get; init; }

    /// <summary>Человеческое сообщение об ошибке; <c>null</c>, когда проверка удалась.</summary>
    public string? Error { get; init; }

    public bool Ok => Error is null;

    /// <summary>Версия в релизе строго больше установленной.</summary>
    public bool UpdateAvailable { get; init; }

    public static UpdateCheckResult Failed(string error) => new() { Error = error };
}

/// <summary>
/// Проверка обновлений по странице релизов проекта на GitHub.
/// </summary>
/// <remarks>
/// Проверка только узнаёт номер последней версии и адрес её сборки. Скачиванием и подменой
/// файла занимается <see cref="UpdateInstaller"/>, и начинает это человек кнопкой, подтвердив
/// отдельным окном: автоматическая установка exe из сети без спроса — ровно то, от чего
/// защищает белый список загрузок в этом же приложении.
/// </remarks>
public static class UpdateChecker
{
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/Qvisono/Amarin-Admin-AI/releases/latest";

    public const string ReleasesPageUrl =
        "https://github.com/Qvisono/Amarin-Admin-AI/releases/latest";

    /// <summary>Страница репозитория: сюда ведёт ссылка «Github» в настройках.</summary>
    /// <remarks>
    /// Живёт рядом с адресами обновлений намеренно: owner и repo здесь одни и те же, и при
    /// переезде репозитория чинить надо одно место, а не два разошедшихся.
    /// </remarks>
    public const string RepositoryUrl = "https://github.com/Qvisono/Amarin-Admin-AI";

    /// <summary>Чем программа представляется GitHub. Без этого заголовка он отвечает 403.</summary>
    private const string ProductToken = "Amarin-Admin-AI";

    /// <summary>Как часто автопроверка ходит в сеть.</summary>
    /// <remarks>
    /// Значение живёт в <see cref="UpdateSchedule"/> вместе с остальным расписанием: два
    /// источника истины для одного срока молча разъехались бы.
    /// </remarks>
    public static TimeSpan AutoCheckInterval => UpdateSchedule.Interval;

    /// <summary>
    /// Свой клиент, а не общий с Venice.
    /// </summary>
    /// <remarks>
    /// У клиента Venice в заголовках по умолчанию стоит <c>Authorization: Bearer</c> с ключом
    /// API. HttpClient подмешивает заголовки по умолчанию в каждый запрос, куда бы он ни шёл, —
    /// GitHub на чужой Bearer отвечает 401, а ключ при этом уезжает на посторонний сервер.
    /// Поэтому обновления ходят своим клиентом, у которого никакой авторизации нет.
    /// </remarks>
    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        var client = HttpClients.Create(TimeSpan.FromSeconds(30));

        // User-Agent и на клиенте, а не только на самом запросе: GitHub отвечает 403 на любой
        // запрос без него, а запросов теперь два — к API и к странице релиза по редиректу.
        // Версию к нему приписывает уже сам запрос, здесь важно только, чтобы заголовок был.
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ProductToken);
        return client;
    });

    /// <summary>
    /// Тег релиза в версию: <c>v1.14.2</c>, <c>1.14</c>, <c>v2.0.0-beta.1</c>. Недостающие
    /// разряды добиваются нулями, чтобы 1.14 и 1.14.0 сравнивались как одно и то же.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        var text = (tag ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] is 'v' or 'V')
        {
            text = text[1..];
        }

        var cut = text.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0)
        {
            text = text[..cut];
        }

        if (text.Length == 0 || !Version.TryParse(text.Count(c => c == '.') == 0 ? text + ".0" : text, out var parsed))
        {
            return null;
        }

        return Normalize(parsed);
    }

    /// <summary>Обрезает версию до трёх разрядов и заменяет −1 на 0 — иначе сравнение врёт.</summary>
    public static Version Normalize(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(
            Math.Max(version.Major, 0),
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    /// <summary>Разбор ответа GitHub. Отделён от сети, чтобы проверяться тестами.</summary>
    public static UpdateCheckResult ReadRelease(string json, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateCheckResult.Failed(Loc.Get("S.Updates.BadAnswer"));
            }

            var tag = root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
                ? tagProp.GetString() ?? ""
                : "";

            var version = ParseTag(tag);
            if (version is null)
            {
                return UpdateCheckResult.Failed(Loc.Get("S.Updates.NoVersionInRelease"));
            }

            var page = root.TryGetProperty("html_url", out var urlProp) && urlProp.ValueKind == JsonValueKind.String
                ? urlProp.GetString() ?? ReleasesPageUrl
                : ReleasesPageUrl;

            var title = root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                ? nameProp.GetString()
                : null;

            DateTimeOffset? published = root.TryGetProperty("published_at", out var dateProp) &&
                                        dateProp.ValueKind == JsonValueKind.String &&
                                        DateTimeOffset.TryParse(dateProp.GetString(), out var parsedDate)
                ? parsedDate
                : null;

            return new UpdateCheckResult
            {
                Latest = new ReleaseInfo(tag, version, page, title, published, ReadAssets(root)),
                UpdateAvailable = version > Normalize(current)
            };
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(Loc.Get("S.Updates.ParseFailed"));
        }
    }

    private static IReadOnlyList<ReleaseAsset> ReadAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ReleaseAsset>();
        foreach (var item in assets.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var size = item.TryGetProperty("size", out var sizeProperty) &&
                       sizeProperty.TryGetInt64(out var bytes)
                ? bytes
                : 0;

            list.Add(new ReleaseAsset(
                name.GetString() ?? "",
                url.GetString() ?? "",
                size,
                ReadDigest(item)));
        }

        return list;
    }

    /// <summary>Поле <c>digest</c> приходит как <c>sha256:…</c>; берём из него только сумму.</summary>
    private static string? ReadDigest(JsonElement asset)
    {
        if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = (digest.GetString() ?? "").Trim();
        const string prefix = "sha256:";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = text[prefix.Length..];
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
    }

    public static Task<UpdateCheckResult> CheckAsync(
        Version current,
        CancellationToken cancellationToken = default) =>
        CheckAsync(Client.Value, current, cancellationToken);

    /// <param name="http">
    /// Клиент без заголовков авторизации. Общий клиент Venice сюда передавать нельзя —
    /// см. <see cref="Client"/>.
    /// </param>
    internal static async Task<UpdateCheckResult> CheckAsync(
        HttpClient http,
        Version current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(current);

        if (http.DefaultRequestHeaders.Authorization is not null)
        {
            return UpdateCheckResult.Failed(Loc.Get("S.Updates.ForeignKey"));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            // GitHub отвечает 403 на запрос без User-Agent — заголовок обязателен, а не вежлив.
            request.Headers.UserAgent.ParseAdd(ProductToken + "/" + Normalize(current));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ReadRelease(json, current);
            }

            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            {
                return UpdateCheckResult.Failed(Loc.Format("S.Updates.Status", (int)response.StatusCode));
            }

            var refusal = DescribeRefusal(response.Headers);
            return await FallBackToReleasePageAsync(http, current, cancellationToken).ConfigureAwait(false)
                   ?? UpdateCheckResult.Failed(refusal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed(Loc.Get("S.Updates.Timeout"));
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed(Loc.Format("S.Updates.NoConnection", ex.Message));
        }
    }

    /// <summary>
    /// Что сказать человеку про отказ. 403 от <c>api.github.com</c> — это почти всегда
    /// исчерпанный лимит анонимных запросов: шестьдесят в час на адрес, и за общим NAT или
    /// VPN его выбирают чужие запросы. «GitHub ответил 403» такому человеку не говорит ничего.
    /// </summary>
    internal static string DescribeRefusal(HttpHeaders headers) =>
        TryReadRateLimitReset(headers, out var reset)
            ? Loc.Format("S.Updates.RateLimited", reset.ToLocalTime().ToString("t", CultureInfo.CurrentCulture))
            : Loc.Get("S.Updates.Forbidden");

    /// <summary>
    /// Когда GitHub снова начнёт отвечать. Читается из <c>x-ratelimit-*</c>; отделено от сети,
    /// чтобы проверяться тестами.
    /// </summary>
    internal static bool TryReadRateLimitReset(HttpHeaders headers, out DateTimeOffset reset)
    {
        reset = default;
        if (headers is null)
        {
            return false;
        }

        // Лимитом считается только явный ноль остатка: 403 бывает и по другим причинам, и
        // называть время в них было бы прямой ложью.
        if (!TryReadHeader(headers, "x-ratelimit-remaining", out var remaining) || remaining != 0)
        {
            return false;
        }

        if (!TryReadHeader(headers, "x-ratelimit-reset", out var seconds) || seconds <= 0)
        {
            return false;
        }

        reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }

    private static bool TryReadHeader(HttpHeaders headers, string name, out long value)
    {
        value = 0;
        return headers.TryGetValues(name, out var found) &&
               long.TryParse(
                   found.FirstOrDefault(),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    /// <summary>
    /// Узнаёт версию без API — по тому, куда перенаправляет <c>/releases/latest</c>.
    /// </summary>
    /// <remarks>
    /// Эта страница не считается лимитом API, поэтому для человека, упёршегося в лимит, она и
    /// есть починка, а не сообщение о поломке. Ссылок на файлы сборки оттуда нет, так что
    /// <see cref="ReleaseInfo.Assets"/> остаётся пустым: обновиться одной кнопкой не выйдет,
    /// но «доступна версия такая-то» и «открыть релиз» работают.
    /// </remarks>
    private static async Task<UpdateCheckResult?> FallBackToReleasePageAsync(
        HttpClient http,
        Version current,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesPageUrl);
            request.Headers.UserAgent.ParseAdd(ProductToken + "/" + Normalize(current));

            // Тело страницы не нужно — нужен только адрес, на котором осел редирект.
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? ReadTaggedUrl(response.RequestMessage?.RequestUri?.ToString(), current)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Разбирает адрес вида <c>.../releases/tag/v1.17.0</c>. Отделено от сети ради тестов.
    /// </summary>
    internal static UpdateCheckResult? ReadTaggedUrl(string? url, Version current)
    {
        ArgumentNullException.ThrowIfNull(current);

        const string marker = "/releases/tag/";
        var text = url ?? "";
        var cut = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (cut < 0)
        {
            return null;
        }

        var tag = text[(cut + marker.Length)..].Split(['?', '#'])[0].Trim('/');
        var version = ParseTag(tag);
        if (version is null)
        {
            return null;
        }

        return new UpdateCheckResult
        {
            Latest = new ReleaseInfo(tag, version, text, null, null, []),
            UpdateAvailable = version > Normalize(current)
        };
    }
}
