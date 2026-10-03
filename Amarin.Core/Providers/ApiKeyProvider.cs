namespace Amarin.Core;

/// <summary>Ключ из списка: чем он платит и кому его предъявляют.</summary>
/// <remarks>
/// Секрет, а не ссылка на <c>keys.json</c>: ключ разрешается на каждом запросе, в том числе из
/// потоков хода чата и прогона агента, и лезть за ним в файл значило бы читать диск под ходом.
/// </remarks>
/// <param name="Id">Идентификатор строки в <c>keys.json</c> либо ключа из окружения.</param>
public readonly record struct KeyHandle(string Id, LlmProvider Provider, string Secret)
{
    public ApiCredential Credential => new(Provider, Secret);
}

/// <summary>
/// Все ключи программы разом: которым платят по умолчанию и какой достать по имени.
/// Одна штука на весь процесс.
/// </summary>
/// <remarks>
/// Отдельный объект, а не поле в <see cref="AgentOptions"/>, потому что настройки раздаются
/// копиями: агент, маршрутизатор, заголовок чата и сводка собирают себе свои
/// <see cref="AgentOptions"/> и разъехались бы на ключе, с которым программа запустилась.
/// <para>
/// С версии 1.23.0 ключ выбирается не один на программу, а на каждый слот моделей: провайдера
/// задаёт приставка идентификатора модели, а <see cref="CredentialFor(ModelBinding)"/> достаёт
/// под неё ключ. Поэтому держатель знает весь список, а не только выбранный ключ, — иначе слот
/// с моделью OpenRouter при выбранном ключе Venice ушёл бы с чужим ключом на чужой сервер.
/// </para>
/// <para>
/// Ключи, выбранный ключ и провайдер меняются одной операцией и лежат одним полем: между
/// «поставили ключ» и «поставили провайдера» соседний поток успел бы отправить ключ Venice
/// в OpenRouter, то есть вынести секрет на посторонний сервер. Поле <c>volatile</c>, а не под
/// замком: читают его из разных потоков — ход чата, прогон агента и страница настроек живут
/// параллельно, — присваивание ссылки и так атомарно, а видимость даёт именно <c>volatile</c>.
/// </para>
/// </remarks>
public sealed class ApiKeyProvider
{
    private volatile Holder _current;

    public ApiKeyProvider(string initial = "", LlmProvider provider = LlmProvider.Venice)
    {
        var secret = initial ?? string.Empty;
        _current = new Holder(
            provider,
            secret,
            new ApiCredential(
                LlmProvider.Venice,
                provider == LlmProvider.Venice ? secret : string.Empty),
            []);
    }

    /// <summary>Ключ, которым платят, если слот не выбрал свой. Пустая строка — ключа нет вовсе.</summary>
    public string Current => _current.Secret;

    /// <summary>Чьему серверу этот ключ предъявляют.</summary>
    public LlmProvider CurrentProvider => _current.Provider;

    /// <summary>
    /// Ключ Venice, каким бы ни был выбранный. Пустой — такого ключа в программе нет.
    /// </summary>
    /// <remarks>
    /// Рисование картинок и чтение страниц живут только у Venice, а выбранным может быть ключ
    /// OpenRouter. Живёт здесь, рядом с выбранным ключом: этот объект и так один на программу
    /// и уже роздан всем копиям <see cref="AgentOptions"/> — второму такому же держателю
    /// пришлось бы ходить по тем же местам.
    /// </remarks>
    public ApiCredential VeniceCredential => _current.Venice;

    /// <summary>Выбранный ключ вместе с провайдером, снятые одним чтением.</summary>
    public ApiCredential Credential
    {
        get
        {
            var holder = _current;
            return new ApiCredential(holder.Provider, holder.Secret);
        }
    }

    /// <summary>Все ключи программы одним снимком.</summary>
    public IReadOnlyList<KeyHandle> Keys => _current.All;

    /// <summary>
    /// Поднимается только при настоящей смене — ключа, провайдера, списка или всего сразу. На
    /// него подписаны вещи, которые после смены врут: остаток на плашке и список моделей —
    /// у другого ключа другой тариф, а у другого провайдера и вовсе другие модели.
    /// </summary>
    public event Action<ApiCredential>? Changed;

    /// <summary>
    /// Ключ под эту модель: сначала назначенный слоту, иначе — по умолчанию для её провайдера.
    /// </summary>
    /// <remarks>
    /// Назначенный ключ принимается, только если он того же провайдера, что и модель. Иначе
    /// вышло бы ровно то, от чего защищает <see cref="ApiCredential"/>: ключ одного сервера,
    /// отправленный другому. Чужой или исчезнувший ключ — не ошибка, а повод взять ключ по
    /// умолчанию: человек мог удалить ключ, не трогая слоты.
    /// </remarks>
    public ApiCredential CredentialFor(string? modelId, string? keyId)
    {
        var holder = _current;
        var provider = ModelRef.Of(modelId, holder.Provider);

        if (!string.IsNullOrWhiteSpace(keyId))
        {
            foreach (var key in holder.All)
            {
                if (key.Provider == provider &&
                    key.Id.Equals(keyId, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(key.Secret))
                {
                    return key.Credential;
                }
            }
        }

        return DefaultFor(provider, holder);
    }

    /// <summary>Ключ этой привязки.</summary>
    public ApiCredential CredentialFor(ModelBinding binding) =>
        CredentialFor(binding.ModelId, binding.KeyId);

    /// <summary>
    /// Ключ провайдера по умолчанию: выбранный, если он этого провайдера, иначе первый годный.
    /// </summary>
    /// <remarks>
    /// Выбранный идёт первым, чтобы у человека с одним ключом всё осталось как было. Пустые
    /// учётные данные — не отказ здесь, а отказ понятной строкой в <see cref="VeniceClient"/>:
    /// он один знает, что именно человек пытался сделать.
    /// </remarks>
    public ApiCredential DefaultFor(LlmProvider provider) => DefaultFor(provider, _current);

    /// <summary>Есть ли чем платить этому провайдеру.</summary>
    public bool HasKeyFor(LlmProvider provider) => !DefaultFor(provider).IsEmpty;

    private static ApiCredential DefaultFor(LlmProvider provider, Holder holder)
    {
        if (holder.Provider == provider && !string.IsNullOrWhiteSpace(holder.Secret))
        {
            return new ApiCredential(provider, holder.Secret);
        }

        foreach (var key in holder.All)
        {
            if (key.Provider == provider && !string.IsNullOrWhiteSpace(key.Secret))
            {
                return key.Credential;
            }
        }

        return new ApiCredential(provider, string.Empty);
    }

    public void Use(string? key, LlmProvider provider = LlmProvider.Venice) =>
        Use(new ApiCredential(provider, key ?? string.Empty));

    /// <param name="venice">
    /// Ключ Venice для того, что умеет только Venice. Пусто — берётся сам
    /// <paramref name="credential"/>, если он и есть ключ Venice.
    /// </param>
    public void Use(ApiCredential credential, ApiCredential? venice = null) =>
        Use(credential, venice, _current.All);

    /// <summary>
    /// Ставит выбранный ключ вместе со всем списком — одним присваиванием.
    /// </summary>
    /// <param name="all">
    /// Все ключи программы. Из них слоты достают свои: список обязан меняться в тот же миг, что
    /// и выбранный ключ, иначе между двумя присваиваниями слот нашёл бы уже удалённый ключ.
    /// </param>
    public void Use(ApiCredential credential, ApiCredential? venice, IReadOnlyList<KeyHandle> all)
    {
        var secret = credential.Secret ?? string.Empty;
        var fallback = credential.Provider == LlmProvider.Venice
            ? new ApiCredential(LlmProvider.Venice, secret)
            : new ApiCredential(LlmProvider.Venice, string.Empty);

        var keys = all ?? [];
        var next = new Holder(credential.Provider, secret, venice ?? fallback, keys);
        var previous = _current;
        if (previous.Provider == next.Provider &&
            string.Equals(previous.Secret, next.Secret, StringComparison.Ordinal) &&
            string.Equals(previous.Venice.Secret, next.Venice.Secret, StringComparison.Ordinal) &&
            previous.All.SequenceEqual(next.All))
        {
            return;
        }

        _current = next;
        Changed?.Invoke(new ApiCredential(next.Provider, next.Secret));
    }

    /// <summary>Всё разом под одной ссылкой: поменять её целиком — одно присваивание.</summary>
    private sealed record Holder(
        LlmProvider Provider,
        string Secret,
        ApiCredential Venice,
        IReadOnlyList<KeyHandle> All);
}
