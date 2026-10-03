namespace Amarin.Core;

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
/// </remarks>
internal sealed class VeniceModelListCache
{
    private readonly VeniceClient _venice;
    private readonly ApiKeyProvider? _keys;
    private readonly Dictionary<LlmProvider, Entry> _entries = [];
    private readonly Lock _entriesGate = new();

    public VeniceModelListCache(VeniceClient venice, ApiKeyProvider? keys = null)
    {
        _venice = venice;
        _keys = keys;
    }

    /// <summary>Провайдер, чей список показывают, когда никто не спросил конкретного.</summary>
    /// <remarks>
    /// Это провайдер выбранного ключа: у человека с одним ключом плашка открывается на его
    /// моделях, как и раньше.
    /// </remarks>
    private LlmProvider Default => _keys?.CurrentProvider ?? LlmProvider.Venice;

    /// <summary>Список провайдера выбранного ключа, если он уже загружен.</summary>
    public IReadOnlyList<VeniceModelInfo>? Cached => CachedFor(Default);

    public string? Error => ErrorFor(Default);

    public IReadOnlyList<VeniceModelInfo>? CachedFor(LlmProvider provider) => Get(provider).Models;

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

    public Task<IReadOnlyList<VeniceModelInfo>> GetAgenticAsync(
        CancellationToken cancellationToken = default) =>
        GetAgenticAsync(Default, cancellationToken);

    public async Task<IReadOnlyList<VeniceModelInfo>> GetAgenticAsync(
        LlmProvider provider,
        CancellationToken cancellationToken = default)
    {
        var entry = Get(provider);
        if (entry.Models is not null)
        {
            return entry.Models;
        }

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Models is not null)
            {
                return entry.Models;
            }

            var credential = _keys?.DefaultFor(provider);
            var list = await _venice.ListTextModelsAsync(credential, cancellationToken)
                .ConfigureAwait(false);
            entry.Models = VeniceModelCatalog.FilterAgentic(list);
            entry.Error = null;
            return entry.Models;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            entry.Error = ex.Message;
            entry.Models = [];
            return entry.Models;
        }
        finally
        {
            entry.Gate.Release();
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

    /// <summary>Один список со своим замком: два провайдера грузятся независимо.</summary>
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyList<VeniceModelInfo>? Models { get; set; }

        public string? Error { get; set; }
    }
}
