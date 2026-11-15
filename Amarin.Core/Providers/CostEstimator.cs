namespace Amarin.Core;

/// <summary>Цена модели: доллары за миллион токенов входа и выхода, чтения из кэша и записи в него.</summary>
internal readonly record struct ModelPrice(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null,
    decimal? CacheWritePerMillion = null)
{
    /// <summary>Цена ответа в долларах.</summary>
    /// <param name="input">Весь вход, вместе с прочитанным из кэша и записанным в него.</param>
    /// <remarks>Цены кэша каталог не назвал — такие токены считаются обычным входом.</remarks>
    public decimal Usd(long input, long output, long cacheRead, long cacheWrite)
    {
        var fresh = Math.Max(0, input - cacheRead - cacheWrite);
        return (fresh * InputPerMillion +
                cacheRead * (CacheReadPerMillion ?? InputPerMillion) +
                cacheWrite * (CacheWritePerMillion ?? InputPerMillion) +
                output * OutputPerMillion) / 1_000_000m;
    }
}

/// <summary>
/// Цена модели из каталога — для оценки ответа провайдера, который цену не называет.
/// </summary>
/// <remarks>
/// До 1.37.0-beta.7 здесь же жила оценка цены следующего ответа у кольца контекста; плашку убрали
/// по решению человека, а цена из каталога осталась нужна учёту трат (<c>VeniceClient.Costs</c>).
/// </remarks>
internal static class CostEstimator
{
    /// <summary>Цена из каталога. Null — каталог её не назвал.</summary>
    public static ModelPrice? PriceOf(VeniceModelInfo? model) =>
        model?.ModelSpec?.Pricing is { Input.Usd: { } input, Output.Usd: { } output } pricing && input >= 0m && output >= 0m
            ? new ModelPrice(input, output, pricing.CacheInput?.Usd, pricing.CacheWrite?.Usd)
            : null;
}
