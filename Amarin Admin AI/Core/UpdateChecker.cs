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
/// Приложение ничего не скачивает и не запускает само: найденное обновление — это ссылка,
/// которую пользователь открывает в браузере. Автоматическая установка exe из сети без
/// подтверждения — ровно то, от чего защищает белый список загрузок в этом же приложении.
/// </remarks>
public static class UpdateChecker
{
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/Qvisono/Amarin-Admin-AI/releases/latest";

    public const string ReleasesPageUrl =
        "https://github.com/Qvisono/Amarin-Admin-AI/releases/latest";

    /// <summary>Как часто автопроверка ходит в сеть.</summary>
    public static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// Свой клиент, а не общий с Venice.
    /// </summary>
    /// <remarks>
    /// У клиента Venice в заголовках по умолчанию стоит <c>Authorization: Bearer</c> с ключом
    /// API. HttpClient подмешивает заголовки по умолчанию в каждый запрос, куда бы он ни шёл, —
    /// GitHub на чужой Bearer отвечает 401, а ключ при этом уезжает на посторонний сервер.
    /// Поэтому обновления ходят своим клиентом, у которого никакой авторизации нет.
    /// </remarks>
    private static readonly Lazy<HttpClient> Client =
        new(() => HttpClients.Create(TimeSpan.FromSeconds(30)));

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
                return UpdateCheckResult.Failed("Неожиданный ответ GitHub.");
            }

            var tag = root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
                ? tagProp.GetString() ?? ""
                : "";

            var version = ParseTag(tag);
            if (version is null)
            {
                return UpdateCheckResult.Failed("В релизе нет распознаваемой версии.");
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
            return UpdateCheckResult.Failed("Не удалось разобрать ответ GitHub.");
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
        if (http.DefaultRequestHeaders.Authorization is not null)
        {
            return UpdateCheckResult.Failed("Проверка обновлений не должна ходить с чужим ключом.");
        }

        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(current);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            // GitHub отвечает 403 на запрос без User-Agent — заголовок обязателен, а не вежлив.
            request.Headers.UserAgent.ParseAdd("Amarin-Admin-AI/" + Normalize(current));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed($"GitHub ответил {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadRelease(json, current);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("Проверка заняла слишком много времени.");
        }
        catch (HttpRequestException ex)
        {
            return UpdateCheckResult.Failed("Нет связи с GitHub: " + ex.Message);
        }
    }
}
