using System.Globalization;

namespace Amarin.Core;

/// <summary>Цена модели: доллары за миллион токенов входа и выхода.</summary>
internal readonly record struct ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>
/// Оценка цены следующего ответа: от самой дешёвой модели-кандидата до самой дорогой.
/// </summary>
/// <param name="InputTokens">Сколько токенов уйдёт на вход — история, промпт, набранное.</param>
/// <param name="OutputTokens">Сколько, по прикидке, займёт ответ.</param>
internal readonly record struct CostEstimate(decimal Low, decimal High, int InputTokens, int OutputTokens)
{
    public bool IsRange => High != Low;
}

/// <summary>
/// Оценка цены до отправки (E2). Чистые функции: каталог, чат и набранный текст приходят
/// параметрами, и тесты проверяют их без окна.
/// </summary>
/// <remarks>
/// Оценка — только ответа модели чата. Работа агента и платные инструменты (поиск, картинки)
/// заранее непредсказуемы, и подсказка говорит об этом прямо, а не прибавляет выдуманное число.
/// </remarks>
internal static class CostEstimator
{
    /// <summary>Длина ответа, когда в чате ещё не с чем сравнить.</summary>
    public const int DefaultOutputTokens = 500;

    /// <summary>Сколько последних ответов усредняется: разговор меняется, давние ответы не показатель.</summary>
    private const int RecentAnswers = 10;

    /// <summary>Тот же грубый делитель, что у кольца контекста: оценка обязана с ним сходиться.</summary>
    private const double CharsPerToken = 4;

    /// <summary>Цена из каталога. Null — каталог её не назвал, и оценки не будет.</summary>
    public static ModelPrice? PriceOf(VeniceModelInfo? model) =>
        model?.ModelSpec?.Pricing is { Input.Usd: { } input, Output.Usd: { } output } && input >= 0m && output >= 0m
            ? new ModelPrice(input, output)
            : null;

    /// <summary>
    /// Типичная длина ответа в этом чате: среднее по последним ответам с текстом. Считается по
    /// знакам — числа токенов ответа провайдер отдельно не сообщает.
    /// </summary>
    public static int TypicalOutputTokens(ChatSession? session)
    {
        if (session is null)
        {
            return DefaultOutputTokens;
        }

        long chars = 0;
        var count = 0;
        for (var i = session.Messages.Count - 1; i >= 0 && count < RecentAnswers; i--)
        {
            var message = session.Messages[i];
            if (message.Role == "assistant" && !string.IsNullOrWhiteSpace(message.Text))
            {
                chars += message.Text.Length;
                count++;
            }
        }

        return count == 0
            ? DefaultOutputTokens
            : Math.Max(1, (int)Math.Ceiling(chars / (double)count / CharsPerToken));
    }

    /// <summary>Токены набранного текста.</summary>
    public static int DraftTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    /// <summary>
    /// Оценка по всем кандидатам с известной ценой. Null — ни одной цены нет: «≈ $0» на экране
    /// значило бы «бесплатно», а это неправда.
    /// </summary>
    public static CostEstimate? Estimate(int inputTokens, int outputTokens, IEnumerable<ModelPrice?> prices)
    {
        decimal? low = null, high = null;
        foreach (var price in prices)
        {
            if (price is not { } known)
            {
                continue;
            }

            var cost = (inputTokens * known.InputPerMillion + outputTokens * known.OutputPerMillion) / 1_000_000m;
            low = low is null ? cost : Math.Min(low.Value, cost);
            high = high is null ? cost : Math.Max(high.Value, cost);
        }

        return low is null ? null : new CostEstimate(low.Value, high!.Value, inputTokens, outputTokens);
    }

    /// <summary>Подпись у кольца: «≈ $0.03», «≈ $0.01–0.09», «&lt; $0.01».</summary>
    public static string Format(CostEstimate estimate)
    {
        if (estimate.High < 0.01m)
        {
            return "< $0.01";
        }

        return estimate.IsRange
            ? "≈ $" + Money(estimate.Low) + "–" + Money(estimate.High)
            : "≈ $" + Money(estimate.High);
    }

    /// <summary>Два знака, а у сумм меньше цента — один значащий: «0.004».</summary>
    private static string Money(decimal value) =>
        value >= 0.01m || value == 0m
            ? value.ToString("0.00", CultureInfo.InvariantCulture)
            : value.ToString("0.####", CultureInfo.InvariantCulture);
}
