using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

internal sealed class ChatStreamAccumulator
{
    /// <summary>
    /// Venice appends the encrypted copy of the chain of thought to the last reasoning chunk.
    /// Everything from this marker on is an opaque base64 blob and must never reach the user.
    /// </summary>
    private const string EncryptedReasoningMarker = "__ENCRYPTED_REASONING__";

    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<int, ToolCallBuilder> _toolCalls = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>The answer alone: any chain of thought the model inlined is stripped out.</summary>
    public string Text => ReasoningSplit.Split(_text.ToString()).Answer;

    /// <summary>
    /// Chain of thought the model wrote into <c>content</c> inside <c>&lt;think&gt;</c>-style
    /// tags, as GLM does. Empty for models that use the <c>reasoning_content</c> channel.
    /// </summary>
    public string InlineReasoning => ReasoningSplit.Split(_text.ToString()).Reasoning;

    /// <summary>
    /// Chain of thought from <c>reasoning_content</c>, with the encrypted tail stripped.
    /// Surfaced as the collapsed block above the answer, and used as the answer itself only
    /// when the model produced no content at all.
    /// </summary>
    public string ReasoningText => Sanitize(_reasoning.ToString());

    /// <summary>
    /// How long the model spent before the first word of the answer appeared. Zero when it
    /// started answering straight away — there was nothing to wait through.
    /// </summary>
    public TimeSpan ThinkingElapsed { get; private set; }

    public string? FinishReason { get; private set; }

    public VeniceCost Cost { get; private set; } = VeniceCost.Zero;

    public bool Apply(ChatCompletionChunk chunk)
    {
        if (chunk.Error is not null)
        {
            throw new VeniceApiException(chunk.Error.Message ?? "Unknown Venice API error.");
        }

        if (chunk.Cost is not null)
        {
            Cost = Cost.Add(chunk.Cost.ToCost());
        }

        var choice = chunk.Choices.FirstOrDefault();
        if (choice is null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(choice.FinishReason))
        {
            FinishReason = choice.FinishReason;
        }

        var delta = choice.Delta;
        if (delta is null)
        {
            return false;
        }

        var addedText = false;
        var piece = ChatContent.ReadText(delta.Content);
        if (!string.IsNullOrEmpty(piece))
        {
            // Compared against the answer as it stood before this chunk, not against the chunk
            // itself: a model writing inside <think> is producing content that must not repaint
            // the bubble, and only the split can tell the two apart.
            var before = Text.Length;
            _text.Append(piece);
            addedText = Text.Length > before;
        }

        var reasoning = ChatContent.ReadText(delta.ReasoningContent);
        if (!string.IsNullOrEmpty(reasoning))
        {
            _reasoning.Append(reasoning);
        }

        if (addedText && ThinkingElapsed == TimeSpan.Zero &&
            (_reasoning.Length > 0 || InlineReasoning.Length > 0))
        {
            // The first visible word closes the thinking phase, and only a model that actually
            // thought gets a figure — otherwise this would report the network round trip of
            // every answer as deliberation. Reasoning between later chunks is inside the turn's
            // own duration and needs no second number.
            ThinkingElapsed = _clock.Elapsed;
        }

        if (delta.ToolCalls is { Count: > 0 })
        {
            foreach (var toolDelta in delta.ToolCalls)
            {
                if (!_toolCalls.TryGetValue(toolDelta.Index, out var builder))
                {
                    builder = new ToolCallBuilder();
                    _toolCalls[toolDelta.Index] = builder;
                }

                if (!string.IsNullOrWhiteSpace(toolDelta.Id))
                {
                    builder.Id = toolDelta.Id;
                }

                if (!string.IsNullOrWhiteSpace(toolDelta.Type))
                {
                    builder.Type = toolDelta.Type;
                }

                if (toolDelta.Function is not null)
                {
                    if (!string.IsNullOrWhiteSpace(toolDelta.Function.Name))
                    {
                        builder.Name += toolDelta.Function.Name;
                    }

                    if (!string.IsNullOrEmpty(toolDelta.Function.Arguments))
                    {
                        builder.Arguments.Append(toolDelta.Function.Arguments);
                    }
                }
            }
        }

        return addedText;
    }

    private static string Sanitize(string reasoning)
    {
        if (reasoning.Length == 0)
        {
            return "";
        }

        var cut = reasoning.IndexOf(EncryptedReasoningMarker, StringComparison.Ordinal);
        return (cut >= 0 ? reasoning[..cut] : reasoning).Trim();
    }

    public List<ToolCall> BuildToolCalls()
    {
        return _toolCalls
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.Build())
            .ToList();
    }

    private sealed class ToolCallBuilder
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "function";
        public string Name { get; set; } = "";
        public StringBuilder Arguments { get; } = new();

        public ToolCall Build() => new()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id,
            Type = string.IsNullOrWhiteSpace(Type) ? "function" : Type,
            Function = new FunctionCall
            {
                Name = Name,
                Arguments = Arguments.Length == 0 ? "{}" : Arguments.ToString()
            }
        };
    }
}
