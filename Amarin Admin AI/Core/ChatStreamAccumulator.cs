using System.Diagnostics;
using System.Text;

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
    private readonly List<ToolCallBuilder> _finishedToolCalls = [];
    private int _toolCallOrder;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>
    /// Длина <see cref="_text"/> на момент последнего разбора. Пока она не изменилась,
    /// <see cref="_splitAnswer"/> и <see cref="_splitReasoning"/> действительны.
    /// </summary>
    private int _splitAt = -1;
    private string _splitAnswer = "";
    private string _splitReasoning = "";

    /// <summary>
    /// Встречался ли в ответе хоть один символ, с которого начинается маркер размышления.
    /// </summary>
    /// <remarks>
    /// Признак копится по приходящим кускам и только растёт. Пока его нет, ответ и накопленный
    /// текст — одно и то же, и разбор не нужен вовсе.
    /// </remarks>
    private bool _mayHaveMarkers;

    /// <summary>The answer alone: any chain of thought the model inlined is stripped out.</summary>
    public string Text => Parsed().Answer;

    /// <summary>
    /// Chain of thought the model wrote into <c>content</c> inside <c>&lt;think&gt;</c>-style
    /// tags, as GLM does. Empty for models that use the <c>reasoning_content</c> channel.
    /// </summary>
    public string InlineReasoning => Parsed().Reasoning;

    /// <summary>
    /// Разбор накопленного текста на размышление и ответ, посчитанный один раз на состояние.
    /// </summary>
    /// <remarks>
    /// Оба свойства читают по нескольку раз на каждый чанк потока, а стоил каждый вызов полной
    /// копии накопленного ответа плюс прохода по ней. На ответе в десятки килобайт это давало
    /// сотни мегабайт мусора и квадратичное время — отсюда и то, что подтормаживание к концу
    /// длинного ответа было сильнее, чем в начале.
    /// </remarks>
    private (string Reasoning, string Answer) Parsed()
    {
        if (_splitAt == _text.Length)
        {
            return (_splitReasoning, _splitAnswer);
        }

        var text = _text.ToString();
        (_splitReasoning, _splitAnswer) = _mayHaveMarkers
            ? ReasoningSplit.Split(text)
            : ("", text);
        _splitAt = _text.Length;
        return (_splitReasoning, _splitAnswer);
    }

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

    /// <summary>
    /// Context the model read, straight from the stream's trailing usage chunk. Zero when Venice
    /// sent no usage at all.
    /// </summary>
    public int PromptTokens { get; private set; }

    public int TotalTokens { get; private set; }

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

        // Before the choice is picked, not after: the usage chunk carries an empty "choices" and
        // the early return below would drop it on the floor.
        if (chunk.Usage?.PromptTokens > 0)
        {
            PromptTokens = chunk.Usage.PromptTokens.Value;
        }

        if (chunk.Usage?.TotalTokens > 0)
        {
            TotalTokens = chunk.Usage.TotalTokens.Value;
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
            _mayHaveMarkers |= ReasoningSplit.MayContainMarker(piece);
            if (_mayHaveMarkers)
            {
                // Compared against the answer as it stood before this chunk, not against the
                // chunk itself: a model writing inside <think> is producing content that must
                // not repaint the bubble, and only the split can tell the two apart.
                var before = Text.Length;
                _text.Append(piece);
                addedText = Text.Length > before;
            }
            else
            {
                // Ни одного символа, с которого маркер начинается, в ответе ещё не было —
                // значит весь текст и есть ответ, и сравнивать длины незачем. Замер стоил бы
                // полной копии накопленного на каждый чанк.
                _text.Append(piece);
                addedText = true;
            }
        }

        var reasoning = ChatContent.ReadText(delta.ReasoningContent);
        if (!string.IsNullOrEmpty(reasoning))
        {
            _reasoning.Append(reasoning);
        }

        // `_mayHaveMarkers` первым: без маркеров InlineReasoning заведомо пуст, а обращение
        // к нему собрало бы накопленный ответ в строку — на каждый чанк до конца ответа.
        if (addedText && ThinkingElapsed == TimeSpan.Zero &&
            (_reasoning.Length > 0 || (_mayHaveMarkers && InlineReasoning.Length > 0)))
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
                    builder = new ToolCallBuilder { Order = _toolCallOrder++ };
                    _toolCalls[toolDelta.Index] = builder;
                }

                // Часть моделей нумерует все вызовы нулём и различает их только по id.
                // Без этой проверки аргументы двух вызовов склеивались в одну строку —
                // получалось «{...}{...}», и разбор падал на первой же закрывающей скобке.
                if (!string.IsNullOrWhiteSpace(toolDelta.Id) &&
                    !string.IsNullOrWhiteSpace(builder.Id) &&
                    !string.Equals(builder.Id, toolDelta.Id, StringComparison.Ordinal))
                {
                    _finishedToolCalls.Add(builder);
                    builder = new ToolCallBuilder { Order = _toolCallOrder++ };
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
                    if (!string.IsNullOrWhiteSpace(toolDelta.Function.Name) &&
                        !string.Equals(builder.Name, toolDelta.Function.Name, StringComparison.Ordinal))
                    {
                        // Имя приходит либо кусками, либо целиком в каждом чанке. Второй случай
                        // без этой проверки давал «init_agentinit_agent».
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
        return _finishedToolCalls
            .Concat(_toolCalls.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            .OrderBy(builder => builder.Order)
            .Select(builder => builder.Build())
            .ToList();
    }

    private sealed class ToolCallBuilder
    {
        /// <summary>Очерёдность появления: слот индекса может переиспользоваться.</summary>
        public int Order { get; init; }

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
