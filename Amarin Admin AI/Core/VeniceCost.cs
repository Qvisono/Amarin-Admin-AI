using System.Text.Json.Serialization;

namespace Amarin.Core;

public sealed class VeniceCost
{
    public static VeniceCost Zero { get; } = new();

    public decimal Usd { get; set; }
    public decimal Diem { get; set; }

    public bool HasData { get; set; }

    public VeniceCost Add(VeniceCost other) => new()
    {
        Usd = Usd + other.Usd,
        Diem = Diem + other.Diem,
        HasData = HasData || other.HasData
    };

    /// <summary>
    /// Takes a part out of a total — used to recover what the conversation itself cost once the
    /// tool charges booked against the same client are removed. Clamped at zero: the parts are
    /// reported separately by Venice and rounding could otherwise leave a negative remainder,
    /// which would read as the model paying the user.
    /// </summary>
    public VeniceCost Subtract(VeniceCost other) => new()
    {
        Usd = Math.Max(0m, Usd - other.Usd),
        Diem = Math.Max(0m, Diem - other.Diem),
        HasData = HasData
    };

    public string Format()
    {
        var parts = new List<string>();

        if (HasData || Usd > 0)
        {
            parts.Add(Usd < 0.0001m && Usd > 0
                ? "< $0.0001 USD"
                : $"${Usd:0.####} USD");
        }

        if (Diem > 0)
        {
            parts.Add($"{Diem:0.####} DIEM");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "нет данных";
    }
}

public sealed class VeniceCostResponse
{
    [JsonPropertyName("usd")]
    public decimal? Usd { get; init; }

    [JsonPropertyName("diem")]
    public decimal? Diem { get; init; }

    public VeniceCost ToCost() => new()
    {
        Usd = Usd ?? 0,
        Diem = Diem ?? 0,
        HasData = Usd is not null || Diem is not null
    };
}