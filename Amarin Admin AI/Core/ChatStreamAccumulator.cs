using System.Text;
using System.Text.Json;

namespace Amarin.Core;

internal sealed class ChatStreamAccumulator
{
    private readonly StringBuilder _text = new();
    private readonly Dictionary<int, ToolCallBuilder> _toolCalls = [];

    public string Text => _text.ToString();

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
            _text.Append(piece);
            addedText = true;
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
