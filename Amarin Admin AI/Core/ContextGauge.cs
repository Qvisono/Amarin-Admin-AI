namespace Amarin.Core;

/// <summary>
/// How full the model's context window is right now.
/// </summary>
/// <param name="Used">Tokens the next request would carry.</param>
/// <param name="Max">What the model accepts; zero when the catalogue has not said yet.</param>
/// <param name="IsEstimate">True when any part of <paramref name="Used"/> was guessed locally.</param>
public readonly record struct ContextUsage(int Used, int Max, bool IsEstimate)
{
    public static ContextUsage Unknown => new(0, 0, true);

    /// <summary>Zero when the ceiling is unknown — a ring with no scale must stay empty, not full.</summary>
    public double Fraction => Max <= 0 ? 0 : Math.Clamp(Used / (double)Max, 0, 1);

    /// <summary>Nothing to draw until both halves of the ratio are real.</summary>
    public bool HasScale => Max > 0 && Used > 0;
}

/// <summary>
/// Measures the context window against what the chat is about to send.
/// </summary>
/// <remarks>
/// Venice reports <c>prompt_tokens</c> only after it answers, so between turns the honest number
/// is always one request behind. Rather than show a stale figure, the gauge anchors on the last
/// reported count and estimates only the messages appended since — which is why
/// <see cref="ChatSession.LastPromptTokensApiIndex"/> is stored next to the count itself.
/// </remarks>
internal static class ContextGauge
{
    /// <summary>
    /// Characters per token. Deliberately crude: the estimate exists to keep the ring moving
    /// between answers, and a tokeniser here would have to match whichever model is selected.
    /// </summary>
    private const double CharsPerToken = 4;

    public static ContextUsage Measure(ChatSession? session, string? systemPrompt, VeniceModelInfo? model)
    {
        var max = model?.ModelSpec?.AvailableContextTokens ?? model?.ContextLength ?? 0;
        if (session is null)
        {
            return new ContextUsage(0, Math.Max(max, 0), true);
        }

        var anchor = session.LastPromptTokens;
        if (anchor <= 0)
        {
            // Nothing measured yet: the whole conversation is a guess, system prompt included.
            var estimated = EstimateTokens(session.ApiMessages, 0) + EstimateTokens(systemPrompt);
            return new ContextUsage(estimated, Math.Max(max, 0), true);
        }

        // A reopened chat can have fewer messages than when the count was taken (messages get
        // deleted, turns get rolled back); clamping keeps the tail estimate from reading backwards.
        var from = Math.Clamp(session.LastPromptTokensApiIndex, 0, session.ApiMessages.Count);
        var tail = EstimateTokens(session.ApiMessages, from);
        return new ContextUsage(anchor + tail, Math.Max(max, 0), tail > 0);
    }

    private static int EstimateTokens(IReadOnlyList<ChatMessage> messages, int fromIndex)
    {
        var chars = 0L;
        for (var i = Math.Max(fromIndex, 0); i < messages.Count; i++)
        {
            var message = messages[i];
            chars += ChatContent.ReadText(message.Content)?.Length ?? 0;

            // Tool calls are billed like any other text, and an agent turn is mostly this.
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
