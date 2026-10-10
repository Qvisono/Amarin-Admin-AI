using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

/// <summary>
/// Поток событий Messages API — в кусочки <see cref="ChatCompletionChunk"/>, какие ждёт
/// <see cref="ChatStreamAccumulator"/>.
/// </summary>
/// <remarks>
/// Накопитель потока один на всех провайдеров, и всё, что ниже его (вызовы инструментов,
/// размышление в ленте, кольцо контекста), о протоколе Anthropic не знает. Номер блока Anthropic
/// служит номером вызова инструмента: он уникален внутри ответа, а накопитель упорядочивает
/// вызовы по первому появлению.
/// <para>
/// Блоки размышления собираются здесь же вместе с подписью — их нужно вернуть серверу без
/// изменений в следующем запросе раунда (<see cref="ReplayBlocks"/>).
/// </para>
/// </remarks>
internal sealed class AnthropicStream
{
    private readonly Dictionary<int, (StringBuilder Thinking, StringBuilder Signature)> _thinking = [];
    private readonly JsonArray _replay = [];
    private int _promptTokens;

    /// <summary>Блоки размышления ответа, готовые к возврату серверу. <c>null</c> — их не было.</summary>
    public JsonElement? ReplayBlocks => _replay.Count > 0 ? AnthropicMessages.ToElement(_replay) : null;

    /// <summary>
    /// Кусочек для накопителя из одной строки <c>data:</c>. <c>null</c> — событие служебное.
    /// </summary>
    /// <exception cref="VeniceApiException">Сервер прислал ошибку или отказ классификатора.</exception>
    public ChatCompletionChunk? Translate(string data)
    {
        using var document = JsonDocument.Parse(data);
        var root = document.RootElement;

        switch (AnthropicMessages.Text(root, "type"))
        {
            case "message_start" when root.TryGetProperty("message", out var message):
                _promptTokens = AnthropicMessages.Tokens(message).Prompt;
                return new ChatCompletionChunk { Usage = new VeniceUsage { PromptTokens = _promptTokens } };

            case "content_block_start":
                return Start(Index(root), root.TryGetProperty("content_block", out var block) ? block : default);

            case "content_block_delta":
                return Delta(Index(root), root.TryGetProperty("delta", out var delta) ? delta : default);

            case "content_block_stop":
                Finish(Index(root));
                return null;

            case "message_delta":
                return End(root);

            case "error":
                throw new VeniceApiException(AnthropicMessages.ErrorText(root));

            default:
                return null;
        }
    }

    private ChatCompletionChunk? Start(int index, JsonElement block)
    {
        switch (AnthropicMessages.Text(block, "type"))
        {
            case "text" when AnthropicMessages.Text(block, "text") is { Length: > 0 } text:
                return Content(text);

            case "tool_use":
                return Tool(new ToolCallDelta
                {
                    Index = index,
                    Id = AnthropicMessages.Text(block, "id"),
                    Type = "function",
                    Function = new FunctionCallDelta { Name = AnthropicMessages.Text(block, "name"), Arguments = "" }
                });

            case "thinking":
                _thinking[index] = (
                    new StringBuilder(AnthropicMessages.Text(block, "thinking") ?? ""),
                    new StringBuilder(AnthropicMessages.Text(block, "signature") ?? ""));
                return null;

            case "redacted_thinking":
                _replay.Add(JsonNode.Parse(block.GetRawText()));
                return null;

            default:
                return null;
        }
    }

    private ChatCompletionChunk? Delta(int index, JsonElement delta)
    {
        switch (AnthropicMessages.Text(delta, "type"))
        {
            case "text_delta" when AnthropicMessages.Text(delta, "text") is { Length: > 0 } text:
                return Content(text);

            case "input_json_delta" when AnthropicMessages.Text(delta, "partial_json") is { Length: > 0 } json:
                return Tool(new ToolCallDelta { Index = index, Function = new FunctionCallDelta { Arguments = json } });

            case "thinking_delta" when AnthropicMessages.Text(delta, "thinking") is { Length: > 0 } thought:
                if (_thinking.TryGetValue(index, out var building))
                {
                    building.Thinking.Append(thought);
                }

                return new ChatCompletionChunk
                {
                    Choices = [new ChatChunkChoice { Delta = new ChatMessageDelta { ReasoningContent = ChatContent.Text(thought) } }]
                };

            case "signature_delta" when AnthropicMessages.Text(delta, "signature") is { } signature:
                if (_thinking.TryGetValue(index, out var signed))
                {
                    signed.Signature.Append(signature);
                }

                return null;

            default:
                return null;
        }
    }

    private void Finish(int index)
    {
        if (!_thinking.Remove(index, out var block))
        {
            return;
        }

        _replay.Add(new JsonObject
        {
            ["type"] = "thinking",
            ["thinking"] = block.Thinking.ToString(),
            ["signature"] = block.Signature.ToString()
        });
    }

    private ChatCompletionChunk End(JsonElement root)
    {
        var stop = root.TryGetProperty("delta", out var delta) ? AnthropicMessages.Text(delta, "stop_reason") : null;
        var output = AnthropicMessages.Tokens(root).Completion;

        return new ChatCompletionChunk
        {
            Choices = stop is null ? [] : [new ChatChunkChoice { FinishReason = AnthropicMessages.FinishReason(stop) }],
            Usage = new VeniceUsage
            {
                PromptTokens = _promptTokens,
                CompletionTokens = output,
                TotalTokens = _promptTokens + output
            }
        };
    }

    private static ChatCompletionChunk Content(string text) =>
        new()
        {
            Choices = [new ChatChunkChoice { Delta = new ChatMessageDelta { Content = ChatContent.Text(text) } }]
        };

    private static ChatCompletionChunk Tool(ToolCallDelta call) =>
        new()
        {
            Choices = [new ChatChunkChoice { Delta = new ChatMessageDelta { ToolCalls = [call] } }]
        };

    private static int Index(JsonElement root) =>
        root.TryGetProperty("index", out var index) && index.TryGetInt32(out var value) ? value : 0;
}
