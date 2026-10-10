namespace Amarin.Core;

/// <summary>Итог загрузки каталога: список моделей либо понятная строка, почему его нет.</summary>
internal readonly record struct CatalogLoad(IReadOnlyList<VeniceModelInfo>? Models, string? Error);

/// <summary>
/// Списки моделей — по одному на провайдера.
/// </summary>
/// <remarks>
/// До версии 1.23.0 список был один: активный ключ задавал провайдера всем слотам сразу, и
/// показывать было нечего, кроме моделей этого ключа. Теперь слот выбирается из моделей любого
/// провайдера, у которого есть ключ, и плашка держит перед глазами оба списка сразу — значит
/// и кэшей нужно столько же.
/// <para>
/// Провайдера без ключа не опрашиваем вовсе: запрос без <c>Authorization</c> вернулся бы
/// четырёхсоткой, и в плашке вместо понятного «добавьте ключ» стояла бы ошибка сервера.
/// </para>
/// <para>
/// Запоминается только удача. До 1.34.0 отказ ложился в кэш пустым списком, и дальше и кэш,
/// и окно считали провайдера загруженным: человеку, которому OpenRouter ответил «Access Denied»
/// из-за страны, список не возвращался до перезапуска программы, сколько бы он ни открывал
/// плашку. Теперь каждая следующая просьба после отказа снова идёт в сеть.
/// </para>
/// </remarks>
internal sealed class VeniceModelListCache
{
    /// <summary>
    /// Сколько ждать список. Свой срок, а не общий у клиента: у того пять минут под долгие
    /// ответы моделей, а плашка всё это время стояла бы на «Загрузка…».
    /// </summary>
    internal static readonly TimeSpan DefaultLoadTimeout = TimeSpan.FromSeconds(30);

    private readonly VeniceClient _venice;
    private readonly ApiKeyProvider? _keys;
    private readonly TimeSpan _loadTimeout;
    private readonly Dictionary<LlmProvider, Entry> _entries = [];
    private readonly Lock _entriesGate = new();

    /// <param name="loadTimeout">Срок загрузки — параметром, чтобы тест не ждал полминуты.</param>
    public VeniceModelListCache(VeniceClient venice, ApiKeyProvider? keys = null, TimeSpan? loadTimeout = null)
    {
        _venice = venice;
        _keys = keys;
        _loadTimeout = loadTimeout ?? DefaultLoadTimeout;
    }

    /// <summary>Провайдер, чей список показывают, когда никто не спросил конкретного.</summary>
    /// <remarks>
    /// Это провайдер выбранного ключа: у человека с одним ключом плашка открывается на его
    /// моделях, как и раньше.
    /// </remarks>
    private LlmProvider Default => _keys?.CurrentProvider ?? LlmProvider.Venice;

    /// <summary>Список провайдера выбранного ключа, если он уже загружен.</summary>
    public IReadOnlyList<VeniceModelInfo>? Cached => CachedFor(Default);

    /// <summary>Загруженный список провайдера. <c>null</c> — удачной загрузки ещё не было.</summary>
    public IReadOnlyList<VeniceModelInfo>? CachedFor(LlmProvider provider) => Get(provider).Models;

    /// <summary>Почему не загрузилась последняя попытка. <c>null</c> — не было отказа.</summary>
    public string? ErrorFor(LlmProvider provider) => Get(provider).Error;

    /// <summary>
    /// Ищет модель по идентификатору во всех загруженных списках.
    /// </summary>
    /// <remarks>
    /// По всем, а не только в списке выбранного ключа: идентификатор несёт приставку провайдера,
    /// и заголовок чата у одного провайдера обязан находиться, пока разговор идёт у другого.
    /// </remarks>
    public VeniceModelInfo? Find(string? id)
    {
        var provider = ModelRef.Of(id, Default);
        if (ReasoningPolicy.Find(CachedFor(provider), id) is { } hit)
        {
            return hit;
        }

        foreach (var spec in ProviderSpec.All)
        {
            if (spec.Provider != provider &&
                ReasoningPolicy.Find(CachedFor(spec.Provider), id) is { } other)
            {
                return other;
            }
        }

        return null;
    }

    /// <summary>
    /// Забывает списки, чтобы они загрузились заново.
    /// </summary>
    /// <remarks>
    /// Зовётся при смене ключа: тариф и доступ к моделям у ключей разные, и список, собранный
    /// прежним ключом, показывал бы модели, к которым новый не пускает. Провайдер указывают,
    /// чтобы добавленный ключ OpenRouter не заставлял перечитывать и каталог Venice —
    /// он от чужого ключа не меняется.
    /// </remarks>
    public void Invalidate(LlmProvider? provider = null)
    {
        lock (_entriesGate)
        {
            if (provider is { } one)
            {
                _entries.Remove(one);
                return;
            }

            _entries.Clear();
        }
    }

    /// <summary>
    /// Список провайдера: уже загруженный — сразу, иначе из сети.
    /// </summary>
    /// <remarks>
    /// Никогда не бросает, кроме отмены самим вызывающим: отказ сервера, обрыв сети и истёкший
    /// срок — обычный ответ с <see cref="CatalogLoad.Error"/>. Прежде таймаут клиента вылетал
    /// исключением мимо окна, и плашка навсегда оставалась на «Загрузка…».
    /// <para>
    /// Одна загрузка на провайдера: пришедший, пока она идёт, ждёт её же, а не шлёт вторую.
    /// Сама загрузка от токена вызывающего не зависит — закрытая плашка одного не должна
    /// обрывать список, которого ждёт другая.
    /// </para>
    /// </remarks>
    public Task<CatalogLoad> LoadAsync(LlmProvider provider, CancellationToken cancellationToken = default)
    {
        var entry = Get(provider);
        Task<CatalogLoad> loading;

        lock (entry.Gate)
        {
            if (entry.Models is { } ready)
            {
                return Task.FromResult(new CatalogLoad(ready, null));
            }

            // Законченная загрузка — значит, неудачная (удачная оставила бы список): её итог
            // не повторяем, а идём в сеть заново. Ради этого всё и затевалось.
            // В пуле, а не тут же: синхронная часть запроса иначе шла бы под замком записи и на
            // потоке вызывающего, а вызывает чаще всего окно.
            if (entry.Loading is not { IsCompleted: false })
            {
                entry.Loading = Task.Run(() => FetchAsync(entry, provider), CancellationToken.None);
            }

            loading = entry.Loading;
        }

        return loading.WaitAsync(cancellationToken);
    }

    private async Task<CatalogLoad> FetchAsync(Entry entry, LlmProvider provider)
    {
        using var deadline = new CancellationTokenSource(_loadTimeout);
        try
        {
            var credential = _keys?.DefaultFor(provider);
            var list = await _venice.ListTextModelsAsync(credential, deadline.Token).ConfigureAwait(false);
            var models = VeniceModelCatalog.FilterAgentic(list);

            lock (entry.Gate)
            {
                entry.Models = models;
                entry.Error = null;
            }

            return new CatalogLoad(models, null);
        }
        catch (Exception exception)
        {
            var error = ProviderErrorText.ForCatalog(exception, ProviderSpec.For(provider).Name);
            lock (entry.Gate)
            {
                entry.Error = error;
            }

            return new CatalogLoad(null, error);
        }
    }

    private Entry Get(LlmProvider provider)
    {
        lock (_entriesGate)
        {
            if (!_entries.TryGetValue(provider, out var entry))
            {
                entry = new Entry();
                _entries[provider] = entry;
            }

            return entry;
        }
    }

    /// <summary>Один список со своим замком: провайдеры грузятся независимо.</summary>
    private sealed class Entry
    {
        public Lock Gate { get; } = new();

        public IReadOnlyList<VeniceModelInfo>? Models { get; set; }

        public string? Error { get; set; }

        /// <summary>Идущая загрузка, к которой присоединяются, или законченная неудачная.</summary>
        public Task<CatalogLoad>? Loading { get; set; }
    }
}
