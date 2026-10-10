using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Откуда ключ взялся.</summary>
public enum ApiKeySource
{
    /// <summary>Человек ввёл его на странице «Key &amp; Info».</summary>
    Stored,

    /// <summary>Переменная окружения провайдера или user-secrets.</summary>
    Environment
}

/// <summary>Запись из <c>keys.json</c>. Сам ключ в ней лежит только зашифрованным.</summary>
public sealed class ApiKeyRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Label { get; set; } = "";

    /// <summary>Блоб DPAPI в base64. Открытым текстом ключ на диск не попадает никогда.</summary>
    public string Protected { get; set; } = "";

    /// <summary>
    /// Чей это ключ. Значение по умолчанию — <see cref="LlmProvider.Venice"/>, поэтому
    /// <c>keys.json</c>, написанный версией до 1.23.0 и не знающий этого поля, читается как
    /// список ключей Venice, каким он и был.
    /// </summary>
    public LlmProvider Provider { get; set; } = LlmProvider.Venice;

    public DateTime AddedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Сколько токенов выдано на ключ, если его продали пакетом токенов. <c>null</c> — ключ
    /// денежный, и остаток у него в долларах.
    /// </summary>
    /// <remarks>
    /// Задаёт человек: перепродавец показывает пакет у себя на сайте, а запроса, по которому его
    /// узнать, обычно не даёт. Прежние версии поле пропускают — оно необязательное.
    /// </remarks>
    public long? TokenLimit { get; set; }
}

/// <summary>
/// Содержимое <c>provider-keys.json</c>: ключи провайдеров, которых не знают версии до 1.34.0,
/// и описания своих серверов.
/// </summary>
/// <remarks>
/// Отдельный файл, а не новое поле <c>keys.json</c>: 1.33, на которую человек может откатиться
/// кнопкой «Вернуть v…», читает провайдера ключа перечислением и, встретив незнакомое имя,
/// сочла бы весь <c>keys.json</c> повреждённым — ключи Venice и OpenRouter пропали бы вместе с
/// новыми. Новое поле в том же файле не спасло бы иначе: 1.33 при первой же записи выбросила бы
/// незнакомое ей поле. Этот файл прежняя версия не открывает вовсе.
/// </remarks>
public sealed class ProviderKeysFile
{
    public List<ApiKeyRecord> Keys { get; set; } = [];

    public List<CustomEndpoint> Endpoints { get; set; } = [];
}

/// <summary>Содержимое <c>keys.json</c>.</summary>
public sealed class ApiKeyFile
{
    public string? ActiveKeyId { get; set; }

    public List<ApiKeyRecord> Keys { get; set; } = [];

    /// <summary>
    /// Названия, которые человек дал строкам ключей из окружения. Ключ словаря —
    /// синтетический идентификатор строки (<see cref="ApiKeyStore.EnvironmentId"/> и соседний).
    /// </summary>
    /// <remarks>
    /// По месту, а не по отпечатку секрета: название человек даёт переменной — «рабочая», —
    /// и после смены её значения оно должно остаться. Скрытие устроено наоборот, по значению:
    /// см. <see cref="HiddenEnvironmentKeys"/>.
    /// </remarks>
    public Dictionary<string, string> EnvironmentLabels { get; set; } = [];

    /// <summary>Отпечатки ключей окружения, которые человек убрал из программы.</summary>
    /// <remarks>
    /// Отпечатками, а не идентификаторами строк: убирают ключ, а не место. Запрет, записанный
    /// по месту, пережил бы смену значения переменной, и новый ключ программа молча не увидела
    /// бы — «я же поменял VENICE_API_KEY, почему он не работает». Сам секрет в файл при этом не
    /// попадает: <see cref="ApiKeyStore.Fingerprint"/> — это срез SHA-256.
    /// </remarks>
    public List<string> HiddenEnvironmentKeys { get; set; } = [];
}

/// <summary>Ключ вместе с тем, чьему серверу его предъявлять.</summary>
/// <remarks>
/// Пара, а не две строки рядом: ключ Venice, отправленный в OpenRouter, — это утечка секрета
/// на посторонний сервер, и такую ошибку должен ловить тип, а не внимательность.
/// </remarks>
/// <param name="Endpoint">
/// Адрес своего сервера. Только у ключей своих серверов: адрес встроенного провайдера знает
/// его описание (<see cref="ProviderSpec"/>), а у своего сервера он живёт рядом с ключом.
/// </param>
public readonly record struct ApiCredential(LlmProvider Provider, string Secret, ProviderEndpoint? Endpoint = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Secret);
}

/// <summary>
/// Строка списка ключей — то, что видит страница настроек.
/// </summary>
/// <param name="Id">
/// У ключа из окружения — <see cref="ApiKeyStore.EnvironmentId"/> либо
/// <see cref="ApiKeyStore.OpenRouterEnvironmentId"/>: в файле таких нет.
/// </param>
/// <param name="Secret">
/// Ключ открытым текстом либо <c>null</c>, если блоб не расшифровался. Живёт только в памяти.
/// </param>
/// <param name="Provider">
/// Последним параметром со значением по умолчанию — чтобы строки, собранные одним только
/// именем и ключом, продолжали собираться.
/// </param>
/// <param name="Endpoint">Адрес своего сервера — у ключа своего сервера.</param>
/// <param name="TokenLimit">Пакет токенов, выданный на ключ, если его задал человек.</param>
public sealed record ApiKeyEntry(
    string Id,
    string Label,
    string? Secret,
    ApiKeySource Source,
    bool IsActive,
    LlmProvider Provider = default,
    ProviderEndpoint? Endpoint = null,
    long? TokenLimit = null)
{
    /// <summary>Название провайдера для человека: у своего сервера — его собственное.</summary>
    public string ProviderName => Endpoint?.Name ?? ProviderSpec.For(Provider).Name;

    /// <summary>Блоб зашифрован не этой учётной записью Windows — показать нечего.</summary>
    public bool IsBroken => Secret is null;

    /// <summary>Замаскированный вид для списка: <c>vk-•••••••••ab12</c>.</summary>
    public string Masked => ApiKeyStore.Mask(Secret);

    /// <summary>
    /// Удаление этой строки ключ не уничтожает, а лишь прячет от программы.
    /// </summary>
    /// <remarks>
    /// Переменной окружения программа не распоряжается: стереть её значило бы лезть в
    /// настройки Windows за спиной человека. Поэтому удаление такой строки — это отказ брать
    /// её ключ, и говорить об этом надо прямо, в том же вопросе, где спрашивают подтверждение.
    /// </remarks>
    public bool RemovalOnlyHides => Source == ApiKeySource.Environment;

    /// <summary>Учётные данные этой строки. Пустые, если блоб не расшифровался.</summary>
    public ApiCredential Credential => new(Provider, Secret ?? "", Endpoint);
}

/// <summary>
/// Ключи провайдеров: несколько на выбор, зашифрованные на диске.
/// </summary>
/// <remarks>
/// До версии 1.22.0 ключ был один и брался только из переменной окружения — поля для него
/// в интерфейсе не было намеренно, чтобы не хранить секрет. Теперь ключей может быть
/// несколько, и хранятся они зашифрованными средствами Windows
/// (см. <see cref="DataProtector"/>): расшифровать их сможет только та учётная запись, под
/// которой их вводили.
/// <para>
/// С версии 1.23.0 у ключа есть провайдер. Активный ключ по-прежнему один, и он же решает,
/// чьему серверу программа платит: два активных ключа означали бы вопрос «с какого счёта
/// списывать» на каждый ход.
/// </para>
/// <para>
/// Ключи из окружения остаются и всегда стоят в списке первыми. В файл они не переписываются:
/// человек сознательно держал их снаружи программы, и копировать их на диск за него никто не
/// вправе. Если своих ключей нет, работают они — у обновившегося пользователя ничего
/// не меняется.
/// </para>
/// <para>
/// Переименовать и убрать из списка можно любую строку, в том числе такую. Переименование
/// для неё хранится по месту (<see cref="ApiKeyFile.EnvironmentLabels"/>), удаление — по
/// значению (<see cref="ApiKeyFile.HiddenEnvironmentKeys"/>), и саму переменную окружения
/// программа не трогает: она остаётся в Windows, а программа просто перестаёт брать её ключ.
/// Убранный ключ виден отдельной строкой под списком и возвращается <see cref="Restore"/> —
/// иначе единственным способом вернуть его была бы правка переменных среды.
/// </para>
/// <para>
/// Файл лежит в папке профиля, как <c>settings.json</c>: профили разделены паролем, и чужой
/// ключ вместе с его тратами в соседнем профиле видеть незачем.
/// </para>
/// </remarks>
internal sealed class ApiKeyStore
{
    /// <summary>Идентификатор строки ключа Venice из окружения. В файле такого никогда нет.</summary>
    /// <remarks>
    /// Значение менять нельзя: на него ссылается <c>activeKeyId</c> в уже записанных
    /// <c>keys.json</c>.
    /// </remarks>
    public const string EnvironmentId = "environment";

    /// <summary>То же для ключа OpenRouter из окружения.</summary>
    public const string OpenRouterEnvironmentId = "environment-openrouter";

    /// <summary>Имя файла ключей новых провайдеров и своих серверов.</summary>
    public const string ProviderKeysFileName = "provider-keys.json";

    private readonly string _path;
    private readonly string _morePath;
    private readonly Dictionary<LlmProvider, string> _environment = [];
    private ApiKeyFile _file = new();
    private ProviderKeysFile _more = new();

    public ApiKeyStore(
        string? root = null,
        string environmentKey = "",
        string openRouterEnvironmentKey = "")
    {
        _path = root is null ? AppPaths.KeysFile : Path.Combine(root, "keys.json");
        _morePath = Path.Combine(Path.GetDirectoryName(_path) ?? "", ProviderKeysFileName);
        Remember(LlmProvider.Venice, environmentKey);
        Remember(LlmProvider.OpenRouter, openRouterEnvironmentKey);
    }

    /// <summary>Все свои записи ключей: прежнего файла и нового.</summary>
    private IEnumerable<ApiKeyRecord> Records => _file.Keys.Concat(_more.Keys);

    private ApiKeyRecord? FindRecord(string? id) =>
        Records.FirstOrDefault(record => string.Equals(record.Id, id, StringComparison.Ordinal));

    /// <summary>Адрес своего сервера этого ключа. <c>null</c> — ключ встроенного провайдера.</summary>
    private ProviderEndpoint? EndpointOf(LlmProvider provider) =>
        provider.IsCustom
            ? _more.Endpoints.FirstOrDefault(endpoint => endpoint.Provider == provider)?.ToEndpoint()
            : null;

    /// <summary>Свои серверы, к которым есть ключи.</summary>
    public IReadOnlyList<CustomEndpoint> Endpoints => _more.Endpoints;

    private void Remember(LlmProvider provider, string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _environment[provider] = key;
        }
    }

    /// <summary>Есть ли хоть один ключ в окружении — от этого зависит вид пустого списка.</summary>
    public bool HasEnvironmentKey =>
        ProviderSpec.All.Any(spec => EnvironmentSecret(spec.Provider).Length > 0);

    /// <summary>
    /// Ключ окружения этого провайдера — или пустая строка, если его нет либо человек его убрал.
    /// </summary>
    /// <remarks>
    /// Через этот метод проходит всё, что ключами платит и выбирает. Прямое чтение
    /// <c>_environment</c> осталось только у <see cref="AllSecrets"/>: там нужны все секреты,
    /// какие программа знает, включая убранные.
    /// </remarks>
    private string EnvironmentSecret(LlmProvider provider)
    {
        var secret = _environment.GetValueOrDefault(provider, "");
        return secret.Length > 0 && !IsHidden(secret) ? secret : "";
    }

    private bool IsHidden(string secret) =>
        _file.HiddenEnvironmentKeys.Contains(Fingerprint(secret), StringComparer.Ordinal);

    /// <summary>Название строки окружения: данное человеком либо имя самой переменной.</summary>
    private string LabelOfEnvironment(string id, ProviderSpec spec) =>
        _file.EnvironmentLabels.TryGetValue(id, out var custom) && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : spec.EnvironmentVariable;

    /// <summary>Синтетический идентификатор строки окружения этого провайдера.</summary>
    public static string EnvironmentIdFor(LlmProvider provider) =>
        provider == LlmProvider.OpenRouter ? OpenRouterEnvironmentId : EnvironmentId;

    private static LlmProvider? ProviderOfEnvironmentId(string? id) =>
        string.Equals(id, EnvironmentId, StringComparison.Ordinal) ? LlmProvider.Venice
        : string.Equals(id, OpenRouterEnvironmentId, StringComparison.Ordinal) ? LlmProvider.OpenRouter
        : null;

    /// <summary>Никогда не бросает: повреждённый или отсутствующий файл — это пустой список.</summary>
    public void Load()
    {
        try
        {
            // Проверяется только то, что файл разбирается: секреты под DPAPI здесь не
            // расшифровываются, и копия .bak остаётся такой же зашифрованной, как сам файл.
            GuardedJsonFile.Read(_path, IsReadable, out var text);
            _file = text is null
                ? new ApiKeyFile()
                : JsonSerializer.Deserialize<ApiKeyFile>(text, AppJson.Options) ?? new ApiKeyFile();
            _file.Keys ??= [];
            _file.EnvironmentLabels ??= [];
            _file.HiddenEnvironmentKeys ??= [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _file = new ApiKeyFile();
        }

        try
        {
            GuardedJsonFile.Read(_morePath, IsReadableMore, out var text);
            _more = text is null
                ? new ProviderKeysFile()
                : JsonSerializer.Deserialize<ProviderKeysFile>(text, AppJson.Options) ?? new ProviderKeysFile();
            _more.Keys ??= [];
            _more.Endpoints ??= [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _more = new ProviderKeysFile();
        }
    }

    /// <summary>
    /// Best-effort, как и у остальных файлов рядом: не записавшийся список — потеря одной
    /// строки, а не повод показывать окно аварии посреди настроек.
    /// </summary>
    /// <remarks>
    /// Новый файл пишется, только когда в нём есть что хранить или он уже есть: у человека,
    /// который новыми провайдерами не пользуется, в папке профиля ничего не прибавляется.
    /// </remarks>
    public void Save()
    {
        try
        {
            GuardedJsonFile.Write(_path, JsonSerializer.Serialize(_file, AppJson.Options));
            if (_more.Keys.Count > 0 || _more.Endpoints.Count > 0 || File.Exists(_morePath))
            {
                GuardedJsonFile.Write(_morePath, JsonSerializer.Serialize(_more, AppJson.Options));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    internal static bool IsReadable(string text) =>
        JsonSerializer.Deserialize<ApiKeyFile>(text, AppJson.Options) is not null;

    internal static bool IsReadableMore(string text) =>
        JsonSerializer.Deserialize<ProviderKeysFile>(text, AppJson.Options) is not null;

    /// <summary>Строки для списка: ключи из окружения первыми, остальные в порядке добавления.</summary>
    public IReadOnlyList<ApiKeyEntry> List()
    {
        var activeId = ResolveActiveId();
        var entries = new List<ApiKeyEntry>();

        foreach (var spec in ProviderSpec.All)
        {
            var key = EnvironmentSecret(spec.Provider);
            if (key.Length == 0)
            {
                continue;
            }

            var id = EnvironmentIdFor(spec.Provider);
            entries.Add(new ApiKeyEntry(
                id,
                LabelOfEnvironment(id, spec),
                key,
                ApiKeySource.Environment,
                string.Equals(activeId, id, StringComparison.Ordinal),
                spec.Provider));
        }

        foreach (var record in Records)
        {
            // Ключ своего сервера, чью запись адреса потеряли (правка файла руками), платить не
            // может — адреса нет, — но в списке остаётся: его можно увидеть и убрать.
            entries.Add(new ApiKeyEntry(
                record.Id,
                record.Label,
                DataProtector.Unprotect(record.Protected),
                ApiKeySource.Stored,
                string.Equals(activeId, record.Id, StringComparison.Ordinal),
                record.Provider,
                EndpointOf(record.Provider),
                record.TokenLimit));
        }

        return entries;
    }

    /// <summary>Снимок всех годных ключей для <see cref="ApiKeyProvider"/>.</summary>
    /// <remarks>
    /// Отсюда слоты моделей достают назначенные им ключи. Строки, чей блоб не расшифровался,
    /// сюда не попадают: платить ими всё равно нечем, а в списке на странице настроек они
    /// остаются — человеку надо видеть, что ключ есть, но прочесть его не вышло.
    /// </remarks>
    public IReadOnlyList<KeyHandle> Handles()
    {
        var handles = new List<KeyHandle>();

        foreach (var entry in List())
        {
            // Свой сервер без записи адреса платить не может: адреса, куда предъявить ключ, нет.
            if (!string.IsNullOrWhiteSpace(entry.Secret) && (!entry.Provider.IsCustom || entry.Endpoint is not null))
            {
                handles.Add(new KeyHandle(entry.Id, entry.Provider, entry.Secret, entry.Endpoint));
            }
        }

        return handles;
    }

    /// <summary>Ключ, которым платить, когда слот не выбрал свой. Пусто — платить нечем.</summary>
    public string ActiveSecret() => ActiveCredential().Secret;

    /// <summary>Провайдер выбранного ключа. Модели слотов он с версии 1.23.0 не задаёт.</summary>
    public LlmProvider ActiveProvider() => ActiveCredential().Provider;

    /// <summary>Активный ключ вместе с его провайдером.</summary>
    public ApiCredential ActiveCredential()
    {
        var activeId = ResolveActiveId();

        if (ProviderOfEnvironmentId(activeId) is { } fromEnvironment)
        {
            return new ApiCredential(fromEnvironment, EnvironmentSecret(fromEnvironment));
        }

        var record = FindRecord(activeId);
        if (record is null)
        {
            return FirstEnvironmentCredential();
        }

        // Блоб мог не расшифроваться — ключ вводили под другой учётной записью Windows. Тогда
        // работает ключ из окружения, но обязательно того же провайдера: подставить ключ
        // соседнего значило бы отправить секрет на сервер, которому он не предназначен.
        return new ApiCredential(
            record.Provider,
            DataProtector.Unprotect(record.Protected) ?? EnvironmentSecret(record.Provider),
            EndpointOf(record.Provider));
    }

    /// <summary>
    /// Ключ Venice, каким бы ни был активный.
    /// </summary>
    /// <remarks>
    /// Рисование картинок и чтение страниц живут только у Venice, а активным может быть ключ
    /// OpenRouter. Активный ключ Venice идёт вперёд сохранённых, сохранённые — вперёд окружения.
    /// </remarks>
    public ApiCredential VeniceCredential()
    {
        var active = ActiveCredential();
        if (active.Provider == LlmProvider.Venice && !active.IsEmpty)
        {
            return active;
        }

        foreach (var record in _file.Keys)
        {
            if (record.Provider == LlmProvider.Venice &&
                DataProtector.Unprotect(record.Protected) is { Length: > 0 } secret)
            {
                return new ApiCredential(LlmProvider.Venice, secret);
            }
        }

        return new ApiCredential(LlmProvider.Venice, EnvironmentSecret(LlmProvider.Venice));
    }

    /// <summary>
    /// Все ключи, какие есть, — для вырезания из отчёта об аварии. Именно все, а не только
    /// активный: в стек попадает тот, которым отправляли запрос, а он мог быть и прежним.
    /// </summary>
    public IReadOnlyList<string?> AllSecrets()
    {
        var secrets = new List<string?>();
        foreach (var spec in ProviderSpec.All)
        {
            if (_environment.TryGetValue(spec.Provider, out var key))
            {
                secrets.Add(key);
            }
        }

        secrets.AddRange(Records.Select(record => DataProtector.Unprotect(record.Protected)));
        return secrets;
    }

    /// <summary>
    /// Добавляет ключ встроенного провайдера и делает его активным. <c>null</c> — Windows
    /// отказалась шифровать, такой ключ уже есть, либо это свой сервер (для него —
    /// <see cref="AddCustom"/>).
    /// </summary>
    public ApiKeyRecord? Add(string label, string secret, LlmProvider provider = default, long? tokenLimit = null)
    {
        if (provider.IsCustom)
        {
            return null;
        }

        return Store(label, secret, provider, tokenLimit);
    }

    /// <summary>
    /// Добавляет ключ своего сервера. Сервер с тем же адресом уже есть — ключ ложится к нему,
    /// и в плашке выбора это один провайдер с двумя ключами, а не два одинаковых.
    /// </summary>
    public ApiKeyRecord? AddCustom(string label, string secret, ProviderEndpoint endpoint, long? tokenLimit = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var server = _more.Endpoints.FirstOrDefault(known =>
            string.Equals(known.BaseUrl, endpoint.BaseUrl, StringComparison.OrdinalIgnoreCase));

        if (server is null)
        {
            server = new CustomEndpoint
            {
                Name = string.IsNullOrWhiteSpace(endpoint.Name) ? EndpointAddress.HostOf(endpoint.BaseUrl) : endpoint.Name.Trim(),
                BaseUrl = endpoint.BaseUrl,
                OpenAiChat = endpoint.OpenAiChat,
                AnthropicMessages = endpoint.AnthropicMessages
            };
            _more.Endpoints.Add(server);
        }
        else
        {
            // Проба протоколов при новом ключе свежее прежней: сервер мог научиться новому.
            server.OpenAiChat = endpoint.OpenAiChat;
            server.AnthropicMessages = endpoint.AnthropicMessages;
        }

        var record = Store(label, secret, server.Provider, tokenLimit);
        if (record is null && !Records.Any(key => key.Provider == server.Provider))
        {
            _more.Endpoints.Remove(server);
        }

        return record;
    }

    private ApiKeyRecord? Store(string label, string secret, LlmProvider provider, long? tokenLimit)
    {
        secret = (secret ?? "").Trim();
        if (secret.Length == 0 || Contains(secret))
        {
            return null;
        }

        var blob = DataProtector.Protect(secret);
        if (blob is null)
        {
            return null;
        }

        var record = new ApiKeyRecord
        {
            Label = string.IsNullOrWhiteSpace(label) ? Mask(secret) : label.Trim(),
            Protected = blob,
            Provider = provider,
            TokenLimit = tokenLimit is > 0 ? tokenLimit : null
        };

        // Ключи провайдеров, которых знает 1.33, — в её файл; остальные — в новый, которого
        // она не открывает (см. ProviderKeysFile).
        (ProviderSpec.For(provider).IsLegacy ? _file.Keys : _more.Keys).Add(record);
        _file.ActiveKeyId = record.Id;
        Save();
        return record;
    }

    /// <summary>
    /// Задаёт ключу пакет токенов. Пусто или ноль — ключ денежный.
    /// </summary>
    public bool SetTokenLimit(string id, long? limit)
    {
        if (FindRecord(id) is not { } record)
        {
            return false;
        }

        record.TokenLimit = limit is > 0 ? limit : null;
        Save();
        return true;
    }

    /// <summary>
    /// Это тот ключ окружения, который человек убрал? Тогда его возвращают, а не заводят заново.
    /// </summary>
    public bool IsRemovedEnvironmentKey(string? secret) =>
        !string.IsNullOrWhiteSpace(secret) &&
        _environment.Values.Any(key => string.Equals(key, secret, StringComparison.Ordinal)) &&
        IsHidden(secret);

    /// <summary>
    /// Уже есть такой ключ? Сравнение по значению, не по подписи.
    /// </summary>
    /// <remarks>
    /// Убранный ключ окружения тоже считается своим, хотя его и нет в списке: иначе человек,
    /// вставив то же значение заново, положил бы копию переменной окружения на диск — ровно то,
    /// чего программа не делает никогда.
    /// </remarks>
    public bool Contains(string secret) =>
        _environment.Values.Any(key => string.Equals(key, secret, StringComparison.Ordinal)) ||
        Records.Any(record => string.Equals(
            DataProtector.Unprotect(record.Protected), secret, StringComparison.Ordinal));

    /// <summary>
    /// Убирает ключ из программы.
    /// </summary>
    /// <remarks>
    /// Свой ключ удаляется из файла насовсем. Ключ из окружения удалить нельзя — переменная
    /// принадлежит Windows, а не программе, — поэтому запоминается его отпечаток, и брать этот
    /// ключ программа перестаёт. Вернуть его можно <see cref="Restore"/>: строка об убранном
    /// ключе остаётся под списком, иначе человеку пришлось бы лезть в переменные среды.
    /// </remarks>
    public bool Remove(string id)
    {
        if (ProviderOfEnvironmentId(id) is { } provider)
        {
            var secret = EnvironmentSecret(provider);
            if (secret.Length == 0)
            {
                return false;
            }

            _file.HiddenEnvironmentKeys.Add(Fingerprint(secret));
            ForgetChoice(id);
            Save();
            return true;
        }

        var removed = FindRecord(id);
        if (removed is null)
        {
            return false;
        }

        _ = _file.Keys.Remove(removed) || _more.Keys.Remove(removed);

        // Последний ключ своего сервера уносит и его запись: сервер без ключей в плашке выбора
        // был бы столбцом, в котором ничего нельзя выбрать.
        if (removed.Provider.IsCustom && !Records.Any(record => record.Provider == removed.Provider))
        {
            _more.Endpoints.RemoveAll(endpoint => endpoint.Provider == removed.Provider);
        }

        ForgetChoice(id);
        Save();
        return true;
    }

    /// <summary>Возвращает убранный ключ окружения. Данное ему название сохраняется.</summary>
    public bool Restore(string id)
    {
        if (ProviderOfEnvironmentId(id) is not { } provider)
        {
            return false;
        }

        var secret = _environment.GetValueOrDefault(provider, "");
        if (secret.Length == 0 || !IsHidden(secret))
        {
            return false;
        }

        var fingerprint = Fingerprint(secret);
        _file.HiddenEnvironmentKeys.RemoveAll(
            item => string.Equals(item, fingerprint, StringComparison.Ordinal));
        Save();
        return true;
    }

    /// <summary>
    /// Ключи окружения, которые человек убрал, — строки под списком с предложением вернуть.
    /// </summary>
    /// <remarks>
    /// Подписаны именем переменной, а не данным человеком названием: убранный ключ ищут в
    /// Windows, и там у него есть только имя переменной.
    /// </remarks>
    public IReadOnlyList<ApiKeyEntry> HiddenEnvironment()
    {
        var entries = new List<ApiKeyEntry>();

        foreach (var spec in ProviderSpec.All)
        {
            var secret = _environment.GetValueOrDefault(spec.Provider, "");
            if (secret.Length > 0 && IsHidden(secret))
            {
                entries.Add(new ApiKeyEntry(
                    EnvironmentIdFor(spec.Provider),
                    spec.EnvironmentVariable,
                    secret,
                    ApiKeySource.Environment,
                    IsActive: false,
                    spec.Provider));
            }
        }

        return entries;
    }

    /// <summary>
    /// Переименовывает строку списка. Пустое название возвращает то, что стояло по умолчанию.
    /// </summary>
    /// <remarks>
    /// Название — единственное, чем ключи различаются на экране: маска у всех одинаковая, а
    /// «vk-•••••••••ab12» человек между двумя рабочими ключами не выберет. Строке окружения
    /// название хранится отдельно: сама переменная — не наша, переписывать её нельзя.
    /// </remarks>
    public bool Rename(string id, string? label)
    {
        var name = (label ?? "").Trim();

        if (ProviderOfEnvironmentId(id) is { } provider)
        {
            if (EnvironmentSecret(provider).Length == 0)
            {
                return false;
            }

            if (name.Length == 0)
            {
                _file.EnvironmentLabels.Remove(id);
            }
            else
            {
                _file.EnvironmentLabels[id] = name;
            }

            Save();
            return true;
        }

        var record = FindRecord(id);
        if (record is null)
        {
            return false;
        }

        // Пустое название у своего ключа — это маска, как и у только что добавленного: строка
        // без подписи вовсе не читается.
        record.Label = name.Length > 0 ? name : Mask(DataProtector.Unprotect(record.Protected));
        Save();
        return true;
    }

    /// <summary>Снимает выбор, если он указывал на эту строку: её больше нет в списке.</summary>
    private void ForgetChoice(string id)
    {
        if (string.Equals(_file.ActiveKeyId, id, StringComparison.Ordinal))
        {
            _file.ActiveKeyId = null;
        }
    }

    public bool SetActive(string id)
    {
        if (ProviderOfEnvironmentId(id) is { } provider)
        {
            if (EnvironmentSecret(provider).Length == 0)
            {
                return false;
            }
        }
        else if (FindRecord(id) is null)
        {
            return false;
        }

        _file.ActiveKeyId = id;
        Save();
        return true;
    }

    /// <summary>
    /// Кто активен на самом деле. Записанный выбор может указывать в пустоту — ключ удалили
    /// в другом запуске программы, — и тогда работает первый годный.
    /// </summary>
    private string ResolveActiveId()
    {
        var chosen = _file.ActiveKeyId;
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            if (ProviderOfEnvironmentId(chosen) is { } provider &&
                EnvironmentSecret(provider).Length > 0)
            {
                return chosen;
            }

            if (FindRecord(chosen) is not null)
            {
                return chosen;
            }
        }

        // Свой ключ вперёд переменной окружения: человек добавил его сознательно и позже.
        return Records.FirstOrDefault()?.Id ?? FirstEnvironmentId();
    }

    private string FirstEnvironmentId()
    {
        foreach (var spec in ProviderSpec.All)
        {
            if (EnvironmentSecret(spec.Provider).Length > 0)
            {
                return EnvironmentIdFor(spec.Provider);
            }
        }

        return EnvironmentId;
    }

    private ApiCredential FirstEnvironmentCredential()
    {
        foreach (var spec in ProviderSpec.All)
        {
            if (EnvironmentSecret(spec.Provider) is { Length: > 0 } key)
            {
                return new ApiCredential(spec.Provider, key);
            }
        }

        return new ApiCredential(LlmProvider.Venice, "");
    }

    /// <summary>
    /// Ключ для списка: начало, хвост и ровно столько точек, сколько их в маске.
    /// </summary>
    /// <remarks>
    /// Число точек постоянное, а не по длине ключа: длина — тоже сведение о секрете, и
    /// показывать её незачем.
    /// </remarks>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "-";
        }

        const string Dots = "•••••••••";
        return secret.Length <= 8
            ? Dots
            : secret[..3] + Dots + secret[^4..];
    }

    /// <summary>
    /// Короткая подпись ключа — имя файла с его тратами. Сам ключ в имя файла попасть не должен.
    /// </summary>
    /// <remarks>
    /// Алгоритм менять нельзя ни на символ: по этой строке названы файлы в <c>usage/</c>, и
    /// другая подпись означала бы, что у человека обнулилась история трат.
    /// </remarks>
    public static string Fingerprint(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return "none";
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
