using System.Globalization;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Остаток на ключе у провайдеров, которые его называют, — в том же виде, что у Venice.
/// </summary>
/// <remarks>
/// Отдельно от клиента: здесь нет ни сети, ни ключей, и разбор чужих ответов проверяется
/// тестами напрямую. Учитываются только доллары: книга остатков (<see cref="BalanceBook"/>)
/// складывает ключи в одну сумму, и юани в ней дали бы бессмысленное число.
/// </remarks>
internal static class ProviderBalances
{
    /// <summary>
    /// DeepSeek: <c>GET /user/balance</c> → <c>balance_infos[]</c> по валютам, суммы строками.
    /// </summary>
    public static VeniceRateLimitsData FromDeepSeek(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        decimal? usd = null;

        if (root.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == JsonValueKind.Array)
        {
            foreach (var info in infos.EnumerateArray())
            {
                if (string.Equals(Text(info, "currency"), "USD", StringComparison.OrdinalIgnoreCase))
                {
                    usd = Number(info, "total_balance");
                }
            }
        }

        return Data(usd, available: !root.TryGetProperty("is_available", out var ok) || ok.ValueKind != JsonValueKind.False);
    }

    /// <summary>Moonshot (Kimi): <c>GET /users/me/balance</c> → <c>data.available_balance</c>, доллары.</summary>
    public static VeniceRateLimitsData FromMoonshot(string json)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement.TryGetProperty("data", out var node) ? node : default;
        return Data(data.ValueKind == JsonValueKind.Object ? Number(data, "available_balance") : null, available: true);
    }

    /// <summary>
    /// Перепродавцы на шлюзах семейства one-api: потолок из <c>dashboard/billing/subscription</c>
    /// минус потраченное из <c>dashboard/billing/usage</c> (оно в центах, как у старого API OpenAI).
    /// </summary>
    /// <returns><c>null</c> — шлюз ответил, но остатка в ответе нет.</returns>
    public static VeniceRateLimitsData? FromResellerBilling(string subscriptionJson, string? usageJson)
    {
        using var subscription = JsonDocument.Parse(subscriptionJson);
        var root = subscription.RootElement;
        var limit = Number(root, "hard_limit_usd") ?? Number(root, "system_hard_limit_usd");
        if (limit is null)
        {
            return null;
        }

        decimal? spent = null;
        if (usageJson is not null)
        {
            using var usage = JsonDocument.Parse(usageJson);
            if (Number(usage.RootElement, "total_usage") is { } cents)
            {
                spent = cents / 100m;
            }
        }

        var data = Data(limit - (spent ?? 0m), available: true);
        return new VeniceRateLimitsData
        {
            AccessPermitted = data.AccessPermitted,
            Balances = data.Balances,
            ApiTier = data.ApiTier,
            SpentUsd = spent
        };
    }

    private static VeniceRateLimitsData Data(decimal? usd, bool available) =>
        new()
        {
            AccessPermitted = available,
            Balances = new VeniceKeyBalances { Usd = usd },
            ApiTier = new VeniceApiTier { Id = "paid", IsCharged = true }
        };

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? Number(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }
}
