using System.Diagnostics;
using System.Text;

namespace Amarin.Core;

internal sealed class ChatStreamAccumulator
{
    /// <summary>
    /// Venice дописывает к последнему кусочку размышления его зашифрованную копию. Всё от этой
    /// метки — непрозрачный base64, человеку его показывать нельзя.
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

    /// <summary>Только ответ: размышление, вписанное моделью в текст, вырезано.</summary>
    public string Text => Parsed().Answer;

    /// <summary>
    /// Размышление, которое модель (как GLM) пишет в <c>content</c> тегами вида
    /// <c>&lt;think&gt;</c>. Пусто у моделей с отдельным каналом <c>reasoning_content</c>.
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
    /// Размышление из <c>reasoning_content</c> без зашифрованного хвоста. Показывается свёрнутым
    /// блоком над ответом, а ответом служит, только если модель не дала текста вовсе.
    /// </summary>
    public string ReasoningText => Sanitize(_reasoning.ToString());

    /// <summary>
    /// Сколько модель думала до первого слова ответа. Ноль — начала отвечать сразу.
    /// </summary>
    public TimeSpan ThinkingElapsed { get; private set; }

    public string? FinishReason { get; private set; }

    public VeniceCost Cost { get; private set; } = VeniceCost.Zero;

    /// <summary>
    /// Прочитанный моделью контекст — из последнего кусочка потока с расходом. Ноль — расход не
    /// пришёл.
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

        // До выбора варианта, а не после: у кусочка с расходом пустой «choices», и ранний выход
        // ниже его потерял бы.
        if (chunk.Usage?.PromptTokens > 0)
        {
            PromptTokens = chunk.Usage.PromptTokens.Value;
        }

        if (chunk.Usage?.TotalTokens > 0)
        {
            TotalTokens = chunk.Usage.TotalTokens.Value;
        }

        // Цена от OpenRouter приходит внутри usage и считает весь ответ целиком, а не этот
        // кусок: поэтому присваивание, а не сложение. Venice кладёт свою цену отдельным полем
        // выше — если ответили оба, верхнее поле старше, и записанное им не перетирается.
        if (chunk.Usage?.Cost is { } usd and > 0m && !Cost.HasData)
        {
            Cost = new VeniceCost { Usd = usd, HasData = true };
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
                // Сравниваем с ответом до этого кусочка, а не с самим кусочком: модель, пишущая
                // внутри <think>, даёт текст, который не должен перерисовывать пузырь, и различает
                // их только разбор.
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

        // Одно и то же под двумя именами: reasoning_content у Venice, reasoning у OpenRouter.
        // Связать оба имени с одним свойством генератор кода не умеет, поэтому склейка здесь.
        var reasoning = ChatContent.ReadText(delta.ReasoningContent ?? delta.Reasoning);
        if (!string.IsNullOrEmpty(reasoning))
        {
            _reasoning.Append(reasoning);
        }

        // `_mayHaveMarkers` первым: без маркеров InlineReasoning заведомо пуст, а обращение
        // к нему собрало бы накопленный ответ в строку — на каждый чанк до конца ответа.
        if (addedText && ThinkingElapsed == TimeSpan.Zero &&
            (_reasoning.Length > 0 || (_mayHaveMarkers && InlineReasoning.Length > 0)))
        {
            // Первое видимое слово закрывает фазу размышления, и число получает только модель,
            // которая и правда думала, — иначе сюда попадала бы сетевая задержка каждого ответа.
            // Размышление между поздними кусочками входит в длительность хода и второго числа не
            // требует.
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
