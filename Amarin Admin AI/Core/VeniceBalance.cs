using System.Text.Json.Serialization;

namespace Amarin.Core;

public sealed class VeniceBalance
{
    public bool CanConsume { get; init; }
    public string? ConsumptionCurrency { get; init; }
    public decimal? Usd { get; init; }
    public decimal? Diem { get; init; }
    public decimal? DiemEpochAllocation { get; init; }

    public string Format()
    {
        var parts = new List<string>();

        if (Usd is not null)
        {
            parts.Add($"${Usd:0.##} USD");
        }

        if (Diem is not null)
        {
            parts.Add(DiemEpochAllocation is > 0
                ? $"{Diem:0.##}/{DiemEpochAllocation:0.##} DIEM"
                : $"{Diem:0.##} DIEM");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "нет данных";
    }
}

internal sealed class VeniceBalanceResponse
{
    [JsonPropertyName("canConsume")]
    public bool CanConsume { get; init; }

    [JsonPropertyName("consumptionCurrency")]
    public string? ConsumptionCurrency { get; init; }

    [JsonPropertyName("balances")]
    public VeniceBalancesResponse? Balances { get; init; }

    [JsonPropertyName("diemEpochAllocation")]
    public decimal? DiemEpochAllocation { get; init; }

    public VeniceBalance ToBalance() => new()
    {
        CanConsume = CanConsume,
        ConsumptionCurrency = ConsumptionCurrency,
        Usd = Balances?.Usd,
        Diem = Balances?.Diem,
        DiemEpochAllocation = DiemEpochAllocation
    };
}

internal sealed class VeniceBalancesResponse
{
    [JsonPropertyName("usd")]
    public decimal? Usd { get; init; }

    [JsonPropertyName("diem")]
    public decimal? Diem { get; init; }
}