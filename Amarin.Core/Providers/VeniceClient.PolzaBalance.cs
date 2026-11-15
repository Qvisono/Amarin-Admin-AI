using System.Text.Json;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    private async Task<VeniceRateLimitsData> GetPolzaBalanceAsync(
        ApiCredential credential,
        CancellationToken cancellationToken)
    {
        var rate = _options.RublesPerUsd?.Invoke();
        if (rate is not > 0m)
        {
            return new VeniceRateLimitsData { AccessPermitted = true };
        }

        var json = await GetJsonAsync("../v2/balance", credential, trackBalance: false, cancellationToken)
            .ConfigureAwait(false);
        // Ответ старого курса не должен стать новой оценкой в общей книге остатков.
        if (_options.RublesPerUsd?.Invoke() != rate)
        {
            throw new VeniceApiException(Loc.Get("S.Key.PolzaBalanceError"));
        }

        try
        {
            var balance = ProviderBalances.FromPolza(json, rate);
            return balance.Balances is not null ? balance
                : throw new VeniceApiException(Loc.Get("S.Key.PolzaBalanceError"));
        }
        catch (JsonException)
        {
            throw new VeniceApiException(Loc.Get("S.Key.PolzaBalanceError"));
        }
    }
}
