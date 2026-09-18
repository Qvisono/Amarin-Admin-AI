using System.Text.Json.Serialization;

namespace Amarin.Core;

// ───────────────────────── billing/usage-history ─────────────────────────

/// <summary>
/// Страница журнала трат Venice.
/// </summary>
/// <remarks>
/// Записи идут по возрастанию времени. <see cref="NextCursor"/> пуст на последней странице;
/// вместе с курсором Venice запрещает слать фильтры, поэтому границы периода задаются только
/// на первом запросе.
/// </remarks>
public sealed class VeniceUsagePage
{
    [JsonPropertyName("data")]
    public List<VeniceUsageRecord> Data { get; init; } = [];

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>Одно списание: столько-то такой-то валюты за такой-то товар.</summary>
public sealed class VeniceUsageRecord
{
    /// <summary>
    /// У списания величина отрицательная. Пополнения и возвраты приходят положительными —
    /// в тратах им места нет.
    /// </summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    /// <summary><c>USD</c>, <c>DIEM</c> или <c>BUNDLED_CREDITS</c>.</summary>
    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    /// <summary>Товар: <c>&lt;модель&gt;-llm-output-mtoken</c>, <c>web-search-request</c> и прочее.</summary>
    [JsonPropertyName("sku")]
    public string? Sku { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("units")]
    public decimal? Units { get; init; }

    [JsonPropertyName("pricePerUnitUsd")]
    public decimal? PricePerUnitUsd { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("inferenceDetails")]
    public VeniceInferenceDetails? InferenceDetails { get; init; }
}

public sealed class VeniceInferenceDetails
{
    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; init; }

    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; init; }

    [JsonPropertyName("inferenceExecutionTime")]
    public double? InferenceExecutionTime { get; init; }
}

// ───────────────────────── api_keys/rate_limits ─────────────────────────

/// <summary>Остаток и лимиты ключа. Работает с обычным ключом, без прав администратора.</summary>
public sealed class VeniceRateLimitsResponse
{
    [JsonPropertyName("data")]
    public VeniceRateLimitsData? Data { get; init; }
}

public sealed class VeniceRateLimitsData
{
    /// <summary>Пускает ли ключ к моделям вообще. Ложь — деньги есть, а доступа нет.</summary>
    [JsonPropertyName("accessPermitted")]
    public bool AccessPermitted { get; init; }

    [JsonPropertyName("balances")]
    public VeniceKeyBalances? Balances { get; init; }

    [JsonPropertyName("apiTier")]
    public VeniceApiTier? ApiTier { get; init; }

    [JsonPropertyName("keyExpiration")]
    public DateTimeOffset? KeyExpiration { get; init; }

    [JsonPropertyName("nextEpochBegins")]
    public DateTimeOffset? NextEpochBegins { get; init; }
}

/// <summary>
/// Три кошелька разом.
/// </summary>
/// <remarks>
/// <c>BUNDLED_CREDITS</c> считаются в долларах — это остаток, включённый в тариф. <c>DIEM</c> —
/// отдельная величина со своим курсом, которого программа не знает, поэтому в доллары он
/// нигде не переводится.
/// </remarks>
public sealed class VeniceKeyBalances
{
    [JsonPropertyName("USD")]
    public decimal? Usd { get; init; }

    [JsonPropertyName("DIEM")]
    public decimal? Diem { get; init; }

    [JsonPropertyName("BUNDLED_CREDITS")]
    public decimal? BundledCredits { get; init; }
}

public sealed class VeniceApiTier
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("isCharged")]
    public bool IsCharged { get; init; }
}
