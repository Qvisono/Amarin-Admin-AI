using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Цены и возможности моделей прямых провайдеров — по открытому каталогу OpenRouter.
/// </summary>
/// <remarks>
/// OpenAI, Anthropic, Gemini и прочие в своём списке моделей цен не называют, а OpenAI — и
/// возможностей тоже. Без цены трата ответа неизвестна, график пустеет, а лимиты трат молчат.
/// Каталог OpenRouter открыт без ключа и называет цены производителей: ими программа и
/// оценивает ответ (<see cref="VeniceCost.Estimated"/>). Оценка, а не счёт: скидки за кэш и
/// ступени цены за длинный запрос здесь не учтены.
/// <para>
/// Каталог хранится на машине (в архив данных не едет — он общий для всех и восстанавливается
/// сам) и перечитывается раз в сутки. OpenRouter недоступен (например, не работает из страны
/// человека) — цен нет, а токены всё равно считаются.
/// </para>
/// </remarks>
internal sealed class ModelPriceBook
{
    private const string CatalogUrl = "https://openrouter.ai/api/v1/models";

    /// <summary>Файл книги — у машины, рядом с профилем компиляции: он общий для всех профилей.</summary>
    public static string DefaultCachePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AmarinAdminAI", "prices.json");
    private static readonly TimeSpan Fresh = TimeSpan.FromHours(24);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly string? _cachePath;
    private readonly Lock _gate = new();
    private Dictionary<string, VeniceModelInfo> _byKey = new(StringComparer.Ordinal);
    private DateTime _loadedUtc = DateTime.MinValue;
    private DateTime _attemptUtc = DateTime.MinValue;
    private bool _cacheRead;
    private Task? _loading;

    /// <param name="http">Свой клиент без ключа: каталог открыт, и ключу туда незачем.</param>
    /// <param name="cachePath">Файл каталога на машине. <c>null</c> — только в памяти (тесты).</param>
    public ModelPriceBook(HttpClient http, string? cachePath)
    {
        _http = http;
        _cachePath = cachePath;
    }

    /// <summary>Производители в каталоге OpenRouter у каждого прямого провайдера.</summary>
    private static readonly Dictionary<LlmProvider, string[]> Vendors = new()
    {
        [LlmProvider.OpenAI] = ["openai"],
        [LlmProvider.Anthropic] = ["anthropic"],
        [LlmProvider.Google] = ["google"],
        [LlmProvider.XAi] = ["x-ai"],
        [LlmProvider.DeepSeek] = ["deepseek"],
        [LlmProvider.Moonshot] = ["moonshotai"],
        [LlmProvider.Mistral] = ["mistralai"],
        [LlmProvider.ZAi] = ["z-ai"],
        [LlmProvider.Qwen] = ["qwen"]
    };

    /// <summary>Есть ли у провайдера цены в этой книге.</summary>
    public static bool Covers(LlmProvider provider) => Vendors.ContainsKey(provider);

    /// <summary>
    /// Загружает каталог, если его ещё нет или он устарел. Никогда не бросает: без цен
    /// программа работает, просто не знает, сколько потрачено.
    /// </summary>
    /// <remarks>
    /// Токена нет намеренно: книга одна на процесс, и закрытая плашка одного провайдера не должна
    /// обрывать загрузку, которую ждёт каталог другого. Срок у загрузки свой.
    /// </remarks>
    public Task EnsureLoadedAsync()
    {
        lock (_gate)
        {
            if (_loading is { IsCompleted: false })
            {
                return _loading;
            }

            var now = DateTime.UtcNow;
            if (_cacheRead && (now - _loadedUtc < Fresh || now - _attemptUtc < RetryAfter))
            {
                return Task.CompletedTask;
            }

            _attemptUtc = now;
            _loading = Task.Run(LoadAsync, CancellationToken.None);
            return _loading;
        }
    }

    /// <summary>
    /// Дополняет модели провайдера ценой и возможностями, которых его собственный список не назвал.
    /// </summary>
    public IReadOnlyList<VeniceModelInfo> Enrich(LlmProvider provider, IReadOnlyList<VeniceModelInfo> models)
    {
        if (!Vendors.TryGetValue(provider, out var vendors))
        {
            return models;
        }

        Dictionary<string, VeniceModelInfo> book;
        lock (_gate)
        {
            book = _byKey;
        }

        if (book.Count == 0)
        {
            return models;
        }

        var result = new List<VeniceModelInfo>(models.Count);
        foreach (var model in models)
        {
            var name = ModelFamily.Normalize(ModelRef.Bare(model.Id));
            var known = vendors
                .Select(vendor => book.GetValueOrDefault(vendor + "/" + name))
                .FirstOrDefault(entry => entry is not null);
            result.Add(known is null ? model : Merge(model, known));
        }

        return result;
    }

    /// <summary>Кладёт каталог из ответа OpenRouter. Открыт тестам.</summary>
    internal void Load(string json)
    {
        var response = JsonSerializer.Deserialize(json, VeniceJsonContext.Default.OpenRouterModelsResponse);
        if (response is null)
        {
            return;
        }

        var byKey = new Dictionary<string, VeniceModelInfo>(StringComparer.Ordinal);
        var raw = new Dictionary<string, OpenRouterModel>(StringComparer.Ordinal);
        foreach (var model in response.Data)
        {
            // «~vendor/…-latest» — псевдонимы, «…:batch» и прочие варианты — другие цены.
            if (string.IsNullOrWhiteSpace(model.Id) || model.Id.StartsWith('~') || model.Id.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            var slash = model.Id.IndexOf('/', StringComparison.Ordinal);
            if (slash <= 0)
            {
                continue;
            }

            var key = model.Id[..slash].ToLowerInvariant() + "/" + ModelFamily.Normalize(model.Id[(slash + 1)..]);
            raw.TryAdd(key, model);
        }

        foreach (var model in OpenRouterMapper.ToModels(new OpenRouterModelsResponse { Data = [.. raw.Values] }))
        {
            var bare = ModelRef.Bare(model.Id);
            var slash = bare.IndexOf('/', StringComparison.Ordinal);
            byKey.TryAdd(bare[..slash].ToLowerInvariant() + "/" + ModelFamily.Normalize(bare[(slash + 1)..]), model);
        }

        lock (_gate)
        {
            _byKey = byKey;
            _loadedUtc = DateTime.UtcNow;
        }
    }

    private static VeniceModelInfo Merge(VeniceModelInfo model, VeniceModelInfo known)
    {
        var spec = model.ModelSpec ?? new VeniceModelSpec();
        var caps = spec.Capabilities ?? new VeniceModelCapabilities();
        var knownSpec = known.ModelSpec;
        var knownCaps = knownSpec?.Capabilities;

        return new VeniceModelInfo
        {
            Id = model.Id,
            Object = model.Object,
            OwnedBy = model.OwnedBy,
            Type = model.Type,
            Created = model.Created ?? known.Created,
            ContextLength = model.ContextLength ?? known.ContextLength,
            ModelSpec = new VeniceModelSpec
            {
                Name = spec.Name ?? knownSpec?.Name,
                Description = spec.Description ?? knownSpec?.Description,
                AvailableContextTokens = spec.AvailableContextTokens ?? knownSpec?.AvailableContextTokens,
                MaxOutputTokens = spec.MaxOutputTokens,
                Offline = spec.Offline,
                Beta = spec.Beta,
                Traits = spec.Traits ?? knownSpec?.Traits,
                Pricing = spec.Pricing ?? knownSpec?.Pricing,
                Capabilities = new VeniceModelCapabilities
                {
                    OptimizedForCode = caps.OptimizedForCode || knownCaps?.OptimizedForCode == true,
                    SupportsFunctionCalling = caps.SupportsFunctionCalling,
                    SupportsReasoning = caps.SupportsReasoning || knownCaps?.SupportsReasoning == true,
                    SupportsReasoningEffort = caps.ReasoningEffortOptions is null && knownCaps is not null
                        ? knownCaps.SupportsReasoningEffort
                        : caps.SupportsReasoningEffort,
                    SupportsReasoningEffortWithTools = caps.SupportsReasoningEffortWithTools,
                    ReasoningEffortOptions = caps.ReasoningEffortOptions ?? knownCaps?.ReasoningEffortOptions,
                    DefaultReasoningEffort = caps.DefaultReasoningEffort ?? knownCaps?.DefaultReasoningEffort,
                    SupportsVision = caps.SupportsVision || knownCaps?.SupportsVision == true,
                    Quantization = caps.Quantization,
                    ThinkingModes = caps.ThinkingModes
                }
            }
        };
    }

    private async Task LoadAsync()
    {
        // Сначала файл: вчерашние цены лучше, чем никаких, а свежий файл избавляет от запроса.
        if (!_cacheRead)
        {
            ReadCache();
            _cacheRead = true;
            if (DateTime.UtcNow - _loadedUtc < Fresh)
            {
                return;
            }
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var json = await _http.GetStringAsync(new Uri(CatalogUrl), deadline.Token).ConfigureAwait(false);
            Load(json);
            WriteCache(json);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Нет цен — нет оценки трат; повтор через RetryAfter.
        }
    }

    private void ReadCache()
    {
        try
        {
            if (_cachePath is null || !File.Exists(_cachePath))
            {
                return;
            }

            Load(File.ReadAllText(_cachePath));
            lock (_gate)
            {
                _loadedUtc = File.GetLastWriteTimeUtc(_cachePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void WriteCache(string json)
    {
        if (_cachePath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath) ?? ".");
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
