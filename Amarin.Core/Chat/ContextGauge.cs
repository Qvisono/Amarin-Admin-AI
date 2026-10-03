namespace Amarin.Core;

/// <summary>
/// Насколько заполнено окно контекста модели сейчас.
/// </summary>
/// <param name="Used">Токенов в следующем запросе.</param>
/// <param name="Max">Сколько модель принимает; ноль — каталог ещё не сказал.</param>
/// <param name="IsEstimate">True — часть <paramref name="Used"/> прикинута своим подсчётом.</param>
/// <param name="IsFloor">
/// Потолок взят по худшему из нескольких возможных моделей — это случай «Авто».
/// </param>
public readonly record struct ContextUsage(int Used, int Max, bool IsEstimate, bool IsFloor = false)
{
    public static ContextUsage Unknown => new(0, 0, true);

    /// <summary>Ноль, если потолок неизвестен: кольцо без шкалы должно быть пустым, а не полным.</summary>
    public double Fraction => Max <= 0 ? 0 : Math.Clamp(Used / (double)Max, 0, 1);

    /// <summary>Рисовать нечего, пока обе части отношения не настоящие.</summary>
    public bool HasScale => Max > 0 && Used > 0;
}

/// <summary>
/// Сравнивает окно контекста с тем, что чат собирается отправить.
/// </summary>
/// <remarks>
/// <c>prompt_tokens</c> провайдер сообщает только после ответа, и честное число между ходами
/// всегда отстаёт на запрос. Вместо устаревшей цифры кольцо опирается на последний названный
/// счёт и прикидывает только дописанное после него — поэтому рядом со счётом хранится
/// <see cref="ChatSession.LastPromptTokensApiIndex"/>.
/// </remarks>
internal static class ContextGauge
{
    /// <summary>
    /// Знаков на токен. Грубо намеренно: прикидка лишь двигает кольцо между ответами, а
    /// токенизатор здесь пришлось бы подбирать под каждую выбранную модель.
    /// </summary>
    private const double CharsPerToken = 4;

    public static ContextUsage Measure(ChatSession? session, string? systemPrompt, VeniceModelInfo? model) =>
        Measure(session, systemPrompt, [model]);

    /// <summary>
    /// То же, но когда моделей может быть несколько. Так выглядит «Авто»: маршрутизатор выбирает
    /// между быстрой и тяжёлой моделью уже во время ответа, и до первого ответа честного одного
    /// числа не существует.
    /// </summary>
    /// <remarks>
    /// Берётся меньшее из окон, а не большее: кольцо существует, чтобы предупредить до того, как
    /// модель начнёт молча забывать начало разговора, и ошибаться оно должно в сторону
    /// осторожности. Раньше «Авто» просто не находилось в каталоге моделей — <c>Find("auto")</c>
    /// возвращает null, потому что это не модель, а просьба выбрать её, — и кольцо показывало
    /// прочерк в каждом чате, где выбрано «Авто».
    /// </remarks>
    public static ContextUsage Measure(
        ChatSession? session, string? systemPrompt, IReadOnlyList<VeniceModelInfo?> candidates)
    {
        var max = 0;
        var known = 0;
        foreach (var candidate in candidates ?? [])
        {
            var ceiling = candidate?.ModelSpec?.AvailableContextTokens ?? candidate?.ContextLength ?? 0;
            if (ceiling <= 0)
            {
                continue;
            }

            known++;
            max = max == 0 ? ceiling : Math.Min(max, ceiling);
        }

        var floor = known > 1;
        if (session is null)
        {
            return new ContextUsage(0, Math.Max(max, 0), true, floor);
        }

        var anchor = session.LastPromptTokens;
        if (anchor <= 0)
        {
            // Замеров ещё нет: вся переписка — прикидка, вместе с системным промптом. Сжатый чат
            // шлёт сводку вместо старой части (D10) — её и взвешиваем.
            var compacted = ContextCompaction.IsActive(session);
            var estimated = EstimateTokens(ContextCompaction.Tail(session), 0) + EstimateTokens(systemPrompt) +
                            (compacted ? EstimateTokens(session.CompactSummary) : 0);
            return new ContextUsage(estimated, Math.Max(max, 0), true, floor);
        }

        // В открытом заново чате сообщений может стать меньше, чем при замере (удалили, откатили
        // ход); ограничение не даёт прикидке хвоста уйти в минус.
        var from = Math.Clamp(session.LastPromptTokensApiIndex, 0, session.ApiMessages.Count);
        var tail = EstimateTokens(session.ApiMessages, from);
        return new ContextUsage(anchor + tail, Math.Max(max, 0), tail > 0, floor);
    }

    private static int EstimateTokens(IReadOnlyList<ChatMessage> messages, int fromIndex)
    {
        var chars = 0L;
        for (var i = Math.Max(fromIndex, 0); i < messages.Count; i++)
        {
            var message = messages[i];
            chars += ChatContent.ReadText(message.Content)?.Length ?? 0;

            // Вызовы инструментов считаются как любой текст, а ход агента — в основном они.
            if (message.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var call in calls)
                {
                    chars += call.Function.Name.Length + call.Function.Arguments.Length;
                }
            }
        }

        return ToTokens(chars);
    }

    private static int EstimateTokens(string? text) => ToTokens(text?.Length ?? 0);

    private static int ToTokens(long chars) =>
        chars <= 0 ? 0 : (int)Math.Min(int.MaxValue, Math.Ceiling(chars / CharsPerToken));
}
