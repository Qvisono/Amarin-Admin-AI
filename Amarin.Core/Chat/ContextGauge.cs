using System.Text.Json;

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

/// <summary>Приблизительное распределение общего счёта между частями запроса.</summary>
internal readonly record struct ContextCategoryUsage(
    ContextUsage Total, int Conversation, int Tools, int Files, int System, bool HasUnmeasuredAttachments);

/// <summary>Прикидка результата сжатия.</summary>
/// <param name="UsedAfter">Заполненность после сжатия, в токенах.</param>
/// <param name="CompactedMessages">Сколько сообщений ленты уйдёт под сводку; ноль — сопоставить с лентой не вышло.</param>
internal readonly record struct CompactionPreview(int UsedAfter, int CompactedMessages, int TotalMessages);

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

    /// <summary>
    /// Распределяет общий счёт пропорционально оценке частей. Провайдер не сообщает токены
    /// категорий, поэтому эти числа всегда приблизительны, даже если общий счёт известен.
    /// </summary>
    public static ContextCategoryUsage MeasureCategories(
        ChatSession? session, string? systemPrompt, IReadOnlyList<VeniceModelInfo?> candidates)
    {
        var total = Measure(session, systemPrompt, candidates);
        if (session is null)
        {
            return new ContextCategoryUsage(total, 0, 0, 0, 0, false);
        }

        List<ChatMessage> history;
        string? summary;
        lock (session.Gate)
        {
            history = ApiContextLimiter.Prepare(ContextCompaction.Tail(session));
            summary = ContextCompaction.IsActive(session) ? session.CompactSummary : null;
        }

        long conversation = 0, tools = 0, files = 0;
        var attachments = false;
        foreach (var message in history)
        {
            var chars = ChatContent.ReadText(message.Content)?.Length ?? 0;
            var hasAttachment = message.Content is { ValueKind: JsonValueKind.Array } parts &&
                parts.EnumerateArray().Any(part => part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                    type.GetString() is "file" or "image_url");
            if (hasAttachment)
            {
                attachments = true;
                files += chars;
            }
            else if (message.Role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            {
                tools += chars;
            }
            else
            {
                conversation += chars;
            }

            foreach (var call in message.ToolCalls ?? [])
            {
                tools += call.Function.Name.Length + call.Function.Arguments.Length;
            }
        }

        var system = (long)(systemPrompt?.Length ?? 0) +
                     (summary is null ? 0 : ContextCompaction.PromptBlock(summary, session.CompactedFrom > 0).Length);
        var weights = new[] { conversation, tools, files, system };
        var weightTotal = weights.Sum();
        var result = new int[weights.Length];
        if (weightTotal > 0 && total.Used > 0)
        {
            var assigned = 0;
            for (var i = 0; i < weights.Length; i++)
            {
                result[i] = (int)(total.Used * (double)weights[i] / weightTotal);
                assigned += result[i];
            }

            result[Array.IndexOf(weights, weights.Max())] += total.Used - assigned;
        }

        return new ContextCategoryUsage(total, result[0], result[1], result[2], result[3], attachments);
    }

    /// <summary>
    /// Что даст сжатие с такими настройками: прикидка для экрана сжатия, пока человек их меняет.
    /// Null — сжимать с ними нечего.
    /// </summary>
    /// <param name="used">Текущая заполненность (<see cref="ContextUsage.Used"/>).</param>
    public static CompactionPreview? PreviewCompaction(ChatSession session, CompactionOptions options, int used)
    {
        lock (session.Gate)
        {
            if (ContextCompaction.Plan(session, options) is not { } plan)
            {
                return null;
            }

            // Освобождается то, что сейчас уходит модели дословно и попадает под новую сводку.
            var api = session.ApiMessages;
            var freed = ContextCompaction.IsActive(session)
                ? EstimateTokens(api, plan.From, Math.Min(session.CompactedFrom, plan.Cut)) +
                  EstimateTokens(api, Math.Max(session.CompactedThrough, plan.From), plan.Cut)
                : EstimateTokens(api, plan.From, plan.Cut);
            var summary = ContextCompaction.IsActive(session) ? 0 : Math.Min(freed, TypicalSummaryTokens);

            var last = session.Messages.FindIndex(message => message.Id == plan.AnchorMessageId);
            var first = plan.FirstMessageId is null ? 0 : session.Messages.FindIndex(message => message.Id == plan.FirstMessageId);
            return new CompactionPreview(
                Math.Max(0, used - freed + summary),
                last < 0 || first < 0 ? 0 : last - first + 1,
                session.Messages.Count);
        }
    }

    /// <summary>Сколько обычно занимает сводка: её настоящий размер до сжатия неизвестен.</summary>
    private const int TypicalSummaryTokens = 1000;

    private static int EstimateTokens(IReadOnlyList<ChatMessage> messages, int fromIndex, int toIndex = int.MaxValue)
    {
        var chars = 0L;
        for (var i = Math.Max(fromIndex, 0); i < messages.Count && i < toIndex; i++)
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
