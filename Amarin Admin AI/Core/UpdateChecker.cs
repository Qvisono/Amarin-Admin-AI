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

/// <summary>
/// Версия выпуска целиком: три числа и необязательная пометка предварительной сборки
/// (<c>1.29.0-beta.1</c>).
/// </summary>
/// <remarks>
/// <see cref="Version"/> пометку не держит, а SDK её из версии сборки выбрасывает: у
/// <c>&lt;Version&gt;1.29.0-beta.1&lt;/Version&gt;</c> версия сборки — <c>1.29.0.0</c>, и только
/// <c>AssemblyInformationalVersion</c> сохраняет суффикс. Сравнивай программа одни числа, человек
/// на бете никогда не получил бы финальную 1.29.0 — она «та же самая», — а скачанная бета
/// сошла бы за готовую финальную. Сравнение — по правилам semver: без пометки старше, чем с ней;
/// пометки — по частям через точку, числа численно и младше слов.
/// </remarks>
public readonly record struct ReleaseVersion(Version Core, string Label) : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Label.Length > 0;

    /// <summary>
    /// Разбирает тег или версию: <c>v1.29.0-beta.1</c>, <c>1.28.0+abc</c> (сборочные метаданные
    /// после плюса отбрасываются), <c>1.28</c>. Нет чисел — <c>null</c>.
    /// </summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (UpdateChecker.ParseTag(text) is not { } core)
        {
            return null;
        }

        var raw = (text ?? "").Trim();
        var plus = raw.IndexOf('+');
        if (plus >= 0)
        {
            raw = raw[..plus];
        }

        var dash = raw.IndexOf('-');
        var label = dash >= 0 ? raw[(dash + 1)..].Trim() : "";
        return new ReleaseVersion(core, label);
    }

    public static implicit operator ReleaseVersion(Version version) => new(UpdateChecker.Normalize(version), "");

    public int CompareTo(ReleaseVersion other)
    {
        var core = UpdateChecker.Normalize(Core ?? new Version(0, 0, 0)).CompareTo(UpdateChecker.Normalize(other.Core ?? new Version(0, 0, 0)));
        if (core != 0)
        {
            return core;
        }

        var mine = Label ?? "";
        var theirs = other.Label ?? "";
        if (mine.Length == 0 || theirs.Length == 0)
        {
            return (mine.Length == 0 ? 1 : 0) - (theirs.Length == 0 ? 1 : 0);
        }

        var left = mine.Split('.');
        var right = theirs.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumber = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var a);
            var rightNumber = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var b);
            var step = (leftNumber, rightNumber) switch
            {
                (true, true) => a.CompareTo(b),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i])
            };

            if (step != 0)
            {
                return Math.Sign(step);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public bool Equals(ReleaseVersion other) => CompareTo(other) == 0;

    public override int GetHashCode() => HashCode.Combine(UpdateChecker.Normalize(Core ?? new Version(0, 0, 0)), Label ?? "");

    public override string ToString() =>
        UpdateChecker.Normalize(Core ?? new Version(0, 0, 0)).ToString(3) + (IsPrerelease ? "-" + Label : "");
}

/// <summary>Релиз на GitHub: то немногое из ответа API, что нужно приложению.</summary>
/// <param name="Notes">Заметки к релизу (Markdown из поля <c>body</c>); показываются до установки.</param>
/// <param name="Prerelease">Помечен на GitHub как предварительный — такие видит только бета-канал.</param>
public sealed record ReleaseInfo(
    string Tag,
    Version Version,
    string PageUrl,
    string? Title,
    DateTimeOffset? Published,
    IReadOnlyList<ReleaseAsset> Assets,
    string? Notes = null,
    bool Prerelease = false)
{
    /// <summary>Версия вместе с пометкой из тега — по ней и сравниваются выпуски.</summary>
    public ReleaseVersion Release => ReleaseVersion.Parse(Tag) is { } parsed && parsed.Core == Version
        ? parsed
        : Version;

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

    /// <summary>
    /// Список выпусков для бета-канала. <c>/releases/latest</c> предварительные выпуски не
    /// отдаёт никогда, поэтому бета читает первую страницу списка и выбирает сама.
    /// </summary>
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/Qvisono/Amarin-Admin-AI/releases?per_page=20";

    /// <summary>
    /// Больше этого заметки к релизу не показываются: окно подтверждения — не место для книги,
    /// а полный текст открывается ссылкой на страницу релиза.
    /// </summary>
    public const int NotesLimit = 20_000;

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

    /// <summary>
    /// Версия сборки, как её выпустили: три числа и пометка беты (<c>1.29.0-beta.1</c>), без хеша
    /// коммита. Из <c>AssemblyInformationalVersion</c> — SDK выбрасывает пометку из версии сборки.
    /// Разошлись числа (информационную версию задали руками) — верим версии сборки.
    /// </summary>
    internal static string VersionOf(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var numbers = assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion;
        return ReleaseVersion.Parse(informational) is { } parsed && parsed.Core.ToString(3) == numbers
            ? parsed.ToString()
            : numbers;
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
        return ReadRelease(json, (ReleaseVersion)current);
    }

    /// <inheritdoc cref="ReadRelease(string, Version)"/>
    public static UpdateCheckResult ReadRelease(string json, ReleaseVersion current)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return UpdateCheckResult.Failed(Loc.Get("S.Updates.BadAnswer"));
            }

            return ReadReleaseElement(root) is { } release
                ? new UpdateCheckResult { Latest = release, UpdateAvailable = release.Release > current }
                : UpdateCheckResult.Failed(Loc.Get("S.Updates.NoVersionInRelease"));
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(Loc.Get("S.Updates.ParseFailed"));
        }
    }

    /// <summary>
    /// Разбор списка выпусков для бета-канала: самый новый из не-черновиков, предварительные —
    /// только если <paramref name="includePrerelease"/>.
    /// </summary>
    /// <remarks>
    /// Выбирается по версии, а не по порядку в списке: GitHub сортирует по дате создания, и
    /// заплатка к прошлой ветке, выпущенная позже, стояла бы первой.
    /// </remarks>
    public static UpdateCheckResult ReadReleases(string json, ReleaseVersion current, bool includePrerelease)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
            {
                return UpdateCheckResult.Failed(Loc.Get("S.Updates.BadAnswer"));
            }

            ReleaseInfo? best = null;
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) ||
                    ReadReleaseElement(item) is not { } release ||
                    (release.Prerelease && !includePrerelease))
                {
                    continue;
                }

                if (best is null || release.Release > best.Release)
                {
                    best = release;
                }
            }

            return best is null
                ? UpdateCheckResult.Failed(Loc.Get("S.Updates.NoVersionInRelease"))
                : new UpdateCheckResult { Latest = best, UpdateAvailable = best.Release > current };
        }
        catch (JsonException)
        {
            return UpdateCheckResult.Failed(Loc.Get("S.Updates.ParseFailed"));
        }
    }

    private static ReleaseInfo? ReadReleaseElement(JsonElement root)
    {
        var tag = root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String
            ? tagProp.GetString() ?? ""
            : "";

        var version = ParseTag(tag);
        if (version is null)
        {
            return null;
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

        var notes = root.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
            ? TrimNotes(bodyProp.GetString())
            : null;

        var prerelease = root.TryGetProperty("prerelease", out var preProp) && preProp.ValueKind == JsonValueKind.True;

        return new ReleaseInfo(tag, version, page, title, published, ReadAssets(root), notes, prerelease);
    }

    /// <summary>Пустые заметки — <c>null</c>; длинные обрезаются по границе строки.</summary>
    internal static string? TrimNotes(string? body)
    {
        var text = (body ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length <= NotesLimit)
        {
            return text;
        }

        var cut = text.LastIndexOf('\n', NotesLimit);
        return text[..(cut > NotesLimit / 2 ? cut : NotesLimit)].TrimEnd() + "\n\n…";
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

    /// <summary>Имя файла со списком сумм в релизе — его выкладывает <c>release.yml</c>.</summary>
    public const string SumsAssetName = "SHA256SUMS";

    /// <summary>
    /// Сумма сборки из файла <c>SHA256SUMS</c>, если GitHub не прислал её в <c>digest</c>.
    /// </summary>
    /// <remarks>
    /// Поле <c>digest</c> GitHub заполняет не у всех релизов, а без суммы программа сама
    /// обновляться не станет. Файл сумм лежит в том же релизе и скачивается с того же проверенного
    /// домена; ошибка его чтения — не повод проваливать проверку: сборка просто останется
    /// «без суммы», и решать о ней будет человек.
    /// </remarks>
    private static async Task<ReleaseInfo> WithSumsFileAsync(
        HttpClient http,
        ReleaseInfo release,
        Version current,
        CancellationToken cancellationToken)
    {
        if (release.WindowsBuild is not { Sha256: null } build ||
            release.Assets.FirstOrDefault(asset =>
                    asset.Name.Equals(SumsAssetName, StringComparison.OrdinalIgnoreCase) ||
                    asset.Name.Equals(SumsAssetName + ".txt", StringComparison.OrdinalIgnoreCase))
                is not { } sums ||
            !UpdateInstaller.IsTrustedUrl(sums.Url) ||
            sums.Size > 64 * 1024)
        {
            return release;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, sums.Url);
            request.Headers.UserAgent.ParseAdd(ProductToken + "/" + Normalize(current));
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return release;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ReadSha256Sums(text, build.Name) is { } hash
                ? release with { Assets = [.. release.Assets.Select(asset => ReferenceEquals(asset, build) ? asset with { Sha256 = hash } : asset)] }
                : release;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return release;
        }
    }

    /// <summary>
    /// Сумма файла из текста в формате <c>sha256sum</c>: «сумма, пробел(ы), необязательная
    /// звёздочка, имя». Строки с чужими именами и мусор пропускаются.
    /// </summary>
    internal static string? ReadSha256Sums(string? text, string fileName)
    {
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOfAny([' ', '\t']);
            if (space != 64)
            {
                continue;
            }

            var hex = line[..64];
            var name = line[64..].TrimStart(' ', '\t').TrimStart('*').Trim();
            if (hex.All(Uri.IsHexDigit) && name.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                return hex.ToLowerInvariant();
            }
        }

        return null;
    }

    public static Task<UpdateCheckResult> CheckAsync(
        Version current,
        CancellationToken cancellationToken = default) =>
        CheckAsync(Client.Value, current, cancellationToken);

    /// <param name="beta">Бета-канал: смотреть и предварительные выпуски.</param>
    public static Task<UpdateCheckResult> CheckAsync(
        ReleaseVersion current,
        bool beta,
        CancellationToken cancellationToken = default) =>
        CheckAsync(Client.Value, current, beta, cancellationToken);

    internal static Task<UpdateCheckResult> CheckAsync(
        HttpClient http,
        Version current,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        return CheckAsync(http, (ReleaseVersion)current, beta: false, cancellationToken);
    }

    /// <param name="http">
    /// Клиент без заголовков авторизации. Общий клиент Venice сюда передавать нельзя —
    /// см. <see cref="Client"/>.
    /// </param>
    internal static async Task<UpdateCheckResult> CheckAsync(
        HttpClient http,
        ReleaseVersion current,
        bool beta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (http.DefaultRequestHeaders.Authorization is not null)
        {
            return UpdateCheckResult.Failed(Loc.Get("S.Updates.ForeignKey"));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, beta ? ReleasesApiUrl : LatestReleaseApiUrl);
            // GitHub отвечает 403 на запрос без User-Agent — заголовок обязателен, а не вежлив.
            request.Headers.UserAgent.ParseAdd(ProductToken + "/" + Normalize(current.Core));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var result = beta ? ReadReleases(json, current, includePrerelease: true) : ReadRelease(json, current);
                return result.Latest is { } latest
                    ? result with { Latest = await WithSumsFileAsync(http, latest, current.Core, cancellationToken).ConfigureAwait(false) }
                    : result;
            }

            if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
            {
                return UpdateCheckResult.Failed(Loc.Format("S.Updates.Status", (int)response.StatusCode));
            }

            var refusal = DescribeRefusal(response.Headers);
            // Запасной путь знает только последний финальный выпуск — для беты это лучше, чем
            // ничего: о предварительных она узнает на следующей удачной проверке.
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
        ReleaseVersion current,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesPageUrl);
            request.Headers.UserAgent.ParseAdd(ProductToken + "/" + Normalize(current.Core));

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
        return ReadTaggedUrl(url, (ReleaseVersion)current);
    }

    internal static UpdateCheckResult? ReadTaggedUrl(string? url, ReleaseVersion current)
    {

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

        var latest = new ReleaseInfo(tag, version, text, null, null, []);
        return new UpdateCheckResult
        {
            Latest = latest,
            UpdateAvailable = latest.Release > current
        };
    }
}
