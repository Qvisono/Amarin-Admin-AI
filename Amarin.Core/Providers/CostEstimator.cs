namespace Amarin.Core;

/// <summary>Цена модели: доллары за миллион токенов входа и выхода.</summary>
internal readonly record struct ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

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
        model?.ModelSpec?.Pricing is { Input.Usd: { } input, Output.Usd: { } output } && input >= 0m && output >= 0m
            ? new ModelPrice(input, output)
            : null;
}
