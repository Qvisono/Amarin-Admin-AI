namespace Amarin.Core;

internal static class VeniceModelFallback
{
    public static readonly string[] FallbackModels =
    [
        "claude-sonnet-5",
        "grok-4-5",
        "openai-gpt-53-codex",
        "kimi-k2-7-code"
    ];

    public static IReadOnlyList<string> BuildChain(string primaryModel)
    {
        var chain = new List<string> { primaryModel };

        foreach (var fallback in FallbackModels)
        {
            if (!chain.Contains(fallback, StringComparer.OrdinalIgnoreCase))
            {
                chain.Add(fallback);
            }
        }

        return chain;
    }

    public static IEnumerable<string> GetModelsFrom(string currentModel, string primaryModel)
    {
        var chain = BuildChain(primaryModel);
        var startIndex = 0;

        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i].Equals(currentModel, StringComparison.OrdinalIgnoreCase))
            {
                startIndex = i;
                break;
            }
        }

        return chain.Skip(startIndex);
    }

    public static bool IsModelOverloaded(VeniceApiException exception)
    {
        var message = exception.Message;

        return message.Contains("overload", StringComparison.OrdinalIgnoreCase)
            || message.Contains("перегруж", StringComparison.OrdinalIgnoreCase)
            || message.Contains("(503)", StringComparison.OrdinalIgnoreCase)
            || message.Contains("(429)", StringComparison.OrdinalIgnoreCase)
            || message.Contains("capacity", StringComparison.OrdinalIgnoreCase)
            || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
    }
}