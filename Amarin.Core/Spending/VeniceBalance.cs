namespace Amarin.Core;

/// <summary>Остаток на ключе Venice — столько, сколько отдают заголовки ответа.</summary>
/// <remarks>
/// <c>DiemEpochAllocation</c> заполняется только из старых файлов <c>balance.json</c>:
/// отдельный запрос за балансом больше не делается, а в заголовках этого поля нет.
/// </remarks>
public sealed class VeniceBalance
{
    public bool CanConsume { get; init; }
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
