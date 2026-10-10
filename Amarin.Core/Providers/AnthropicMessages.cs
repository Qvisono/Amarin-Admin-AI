using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

/// <summary>Тело запроса к Messages API и то, нужен ли к нему заголовок бета-функции.</summary>
internal sealed record AnthropicBody(JsonObject Json, bool NeedsBindingBeta);

/// <summary>
/// Перевод между запросом программы (OpenAI-совместимым по форме) и Messages API Anthropic.
/// </summary>
/// <remarks>
/// Вся программа — история, инструменты, учёт трат, поток — говорит в форме
/// <c>/chat/completions</c>. Переводчик держит эту форму снаружи нетронутой: на входе тот же
/// <see cref="ChatCompletionRequest"/>, на выходе тот же <see cref="ChatCompletionResponse"/> и
/// те же <see cref="ChatCompletionChunk"/> для накопителя потока. Здесь нет ни сети, ни ключей —
/// перевод проверяется тестами напрямую.
/// <para>
/// Что легко сломать:
/// <list type="bullet">
/// <item>Системные сообщения — верхним полем <c>system</c>, а не в истории.</item>
/// <item>Подряд идущие сообщения одной роли склеиваются: Anthropic требует чередования, а у нас
/// за ответами инструментов (<c>tool</c>) идёт сообщение с их картинками (<c>user</c>).</item>
/// <item>Блоки размышления с подписью возвращаются без изменений и только у ответов текущего
/// раунда — после последнего хода человека. Подпись привязана ко всему, что стоит перед блоком;
/// снимать блоки с начала истории Anthropic разрешает, а старые всё равно потеряли бы силу.</item>
/// <item>«Без размышления» не значит «не слать поле»: у моделей 5.5 без поля размышление
/// включается само. Выключатель свой у каждой модели, и каталог его называет
/// (<see cref="VeniceModelCapabilities.ThinkingModes"/>).</item>
/// <item>Температура не уходит никогда: модели 5.5 отвечают на неё отказом.</item>
/// </list>
/// </para>
/// </remarks>
internal static class AnthropicMessages
{
    public const string ApiVersion = "2023-06-01";

    /// <summary>
    /// Бета, позволяющая попросить сервер отбросить устаревший блок размышления, а не отказывать
    /// всем запросом: историю программа правит и посреди раунда (заглушка устаревшего файла,
    /// снятые картинки).
    /// </summary>
    public const string BindingBeta = "thinking-binding-controls-2026-08-01";

    private const int StreamMaxTokens = 32_000;

    /// <summary>Без потока ответ ждут целиком, и слишком длинный упёрся бы в срок клиента.</summary>
    private const int PlainMaxTokens = 16_000;

    /// <param name="official">
    /// Запрос уходит самой Anthropic, а не своему серверу человека: только ей шлются бета-поля и
    /// кэш. Посредник мог бы их не пропустить, и отказ ему был бы непонятен.
    /// </param>
    /// <param name="relaxed">
    /// Повтор после отказа: без размышления, уровня и блоков с подписью — у модели не того
    /// поколения или на посреднике, которому они незнакомы.
    /// </param>
    public static AnthropicBody Build(
        ChatCompletionRequest request,
        string bareModel,
        VeniceModelInfo? info,
        bool stream,
        bool official,
        bool relaxed = false)
    {
        var messages = request.Messages;
        var lastTurn = LastTurnStart(messages);
        var system = new List<string>();
        var turns = new List<(string Role, JsonArray Blocks)>();
        var replayed = false;

        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            switch (message.Role.ToLowerInvariant())
            {
                case "system":
                    if (ChatContent.ReadText(message.Content) is { Length: > 0 } text)
                    {
                        system.Add(text);
                    }

                    break;

                case "tool":
                    Append(turns, "user", ToolResult(message));
                    break;

                case "assistant":
                    var blocks = new JsonArray();
                    if (!relaxed && i > lastTurn && message.ReplayBlocks is { ValueKind: JsonValueKind.Array } replay)
                    {
                        foreach (var block in replay.EnumerateArray())
                        {
                            blocks.Add(JsonNode.Parse(block.GetRawText()));
                            replayed = true;
                        }
                    }

                    AddAssistantContent(blocks, message);
                    if (blocks.Count > 0)
                    {
                        Append(turns, "assistant", blocks);
                    }

                    break;

                default:
                    Append(turns, "user", UserBlocks(message.Content));
                    break;
            }
        }

        var maxTokens = MaxTokens(info, stream);
        var body = new JsonObject
        {
            ["model"] = bareModel,
            ["max_tokens"] = maxTokens
        };

        if (system.Count > 0)
        {
            body["system"] = string.Join("\n\n", system);
        }

        var list = new JsonArray();
        foreach (var (role, blocks) in turns)
        {
            list.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
        }

        body["messages"] = list;
        AddTools(body, request, turns);

        var thinksAdaptively = !relaxed && AddThinking(body, request.ReasoningChoice, info, maxTokens);

        if (stream)
        {
            body["stream"] = true;
        }

        var needsBeta = false;
        if (official)
        {
            // Кэш продолжения разговора: без него прямой Anthropic выходил бы дороже той же
            // модели через Venice, где кэш ставит сам посредник.
            body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

            if (thinksAdaptively && replayed && body["thinking"] is JsonObject thinking)
            {
                thinking["block_binding"] = new JsonObject { ["prefix_mismatch_behavior"] = "drop_block" };
                needsBeta = true;
            }
        }

        return new AnthropicBody(body, needsBeta);
    }

    /// <summary>
    /// Стоит ли повторить запрос без размышления после такого отказа.
    /// </summary>
    /// <remarks>
    /// Поля размышления у моделей разных поколений приняты по-разному (уровень, адаптивное,
    /// бюджет, подпись блока), и все различия заранее не перечислить. Отказ, называющий одно из
    /// них, обходится повтором без этих полей: ответ без размышления лучше, чем никакого.
    /// </remarks>
    public static bool MayRelax(int status, string error) =>
        status == 400 &&
        (error.Contains("thinking", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("effort", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("output_config", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("budget_tokens", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("block_binding", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("cache_control", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ответ без потока в форме <c>/chat/completions</c>.</summary>
    public static ChatCompletionResponse ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (Text(root, "type") == "error")
        {
            throw new VeniceApiException(ErrorText(root));
        }

        var text = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        var calls = new List<ToolCall>();
        var replay = new JsonArray();

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                switch (Text(block, "type"))
                {
                    case "text":
                        text.Append(Text(block, "text"));
                        break;
                    case "thinking":
                        reasoning.Append(Text(block, "thinking"));
                        replay.Add(JsonNode.Parse(block.GetRawText()));
                        break;
                    case "redacted_thinking":
                        replay.Add(JsonNode.Parse(block.GetRawText()));
                        break;
                    case "tool_use":
                        calls.Add(new ToolCall
                        {
                            Id = Text(block, "id") ?? "",
                            Function = new FunctionCall
                            {
                                Name = Text(block, "name") ?? "",
                                Arguments = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"
                            }
                        });
                        break;
                }
            }
        }

        var finish = FinishReason(Text(root, "stop_reason"));
        var (prompt, completion) = Tokens(root);

        return new ChatCompletionResponse
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new ChatMessage
                    {
                        Role = "assistant",
                        Content = ChatContent.Text(text.ToString()),
                        ReasoningContent = reasoning.Length > 0 ? ChatContent.Text(reasoning.ToString()) : null,
                        ToolCalls = calls.Count > 0 ? calls : null,
                        ReplayBlocks = replay.Count > 0 ? ToElement(replay) : null
                    },
                    FinishReason = finish
                }
            ],
            Usage = new VeniceUsage
            {
                PromptTokens = prompt,
                CompletionTokens = completion,
                TotalTokens = prompt + completion
            }
        };
    }

    internal static string FinishReason(string? stopReason) => stopReason switch
    {
        "tool_use" => "tool_calls",
        "max_tokens" => "length",

        // Отказ классификатора приходит кодом 200 с пустым ответом. Пустой ответ ход повторил бы
        // ещё раз на ту же модель, поэтому это отказ, и человек видит, почему.
        "refusal" => throw new VeniceApiException(Loc.Get("S.Provider.Refusal")),
        _ => "stop"
    };

    internal static string ErrorText(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "message") ?? "Unknown Anthropic error."
            : "Unknown Anthropic error.";

    internal static (int Prompt, int Completion) Tokens(JsonElement holder)
    {
        if (!holder.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (0, 0);
        }

        // Прочитанное моделью — всё, что ушло в запрос: и свежее, и записанное в кэш, и
        // прочитанное из кэша. Так считает и кольцо контекста, и перепродавец токенов.
        var prompt = Number(usage, "input_tokens") +
                     Number(usage, "cache_creation_input_tokens") +
                     Number(usage, "cache_read_input_tokens");
        return (prompt, Number(usage, "output_tokens"));
    }

    private static int LastTurnStart(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (ChatContent.IsTurnStart(messages[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static void Append(List<(string Role, JsonArray Blocks)> turns, string role, JsonArray blocks)
    {
        if (blocks.Count == 0)
        {
            return;
        }

        if (turns.Count > 0 && turns[^1].Role == role)
        {
            var target = turns[^1].Blocks;
            foreach (var block in blocks.ToList())
            {
                blocks.Remove(block);
                target.Add(block);
            }

            return;
        }

        turns.Add((role, blocks));
    }

    private static JsonArray ToolResult(ChatMessage message) =>
    [
        new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = message.ToolCallId ?? "",

            // Пустой результат Anthropic не берёт, а инструмент вправе ничего не вывести.
            ["content"] = ChatContent.ReadText(message.Content) is { Length: > 0 } text ? text : "(no output)"
        }
    ];

    private static void AddAssistantContent(JsonArray blocks, ChatMessage message)
    {
        if (ChatContent.ReadText(message.Content) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        foreach (var call in message.ToolCalls ?? [])
        {
            blocks.Add(new JsonObject
            {
                ["type"] = "tool_use",
                ["id"] = call.Id,
                ["name"] = call.Function.Name,
                ["input"] = Arguments(call.Function.Arguments)
            });
        }
    }

    /// <summary>Аргументы вызова объектом: Anthropic не примет ни строку, ни массив.</summary>
    private static JsonObject Arguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        try
        {
            // Тем же разбором, что и перед исполнением: аргументы пишет модель, и лишняя
            // скобка или обёртка в ```json здесь обычное дело.
            return JsonNode.Parse(ToolArguments.Parse(arguments).GetRawText()) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static JsonArray UserBlocks(JsonElement? content)
    {
        var blocks = new JsonArray();
        if (content is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return blocks;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { Length: > 0 } text)
            {
                blocks.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }

            return blocks;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return blocks;
        }

        foreach (var part in value.EnumerateArray())
        {
            if (Block(part) is { } block)
            {
                blocks.Add(block);
            }
        }

        return blocks;
    }

    /// <summary>Часть сообщения OpenAI — блок Anthropic: текст, картинка, PDF.</summary>
    private static JsonObject? Block(JsonElement part)
    {
        switch (Text(part, "type"))
        {
            case "text" when Text(part, "text") is { Length: > 0 } text:
                return new JsonObject { ["type"] = "text", ["text"] = text };

            case "image_url" when part.TryGetProperty("image_url", out var image) && Text(image, "url") is { } url:
                return DataUrl(url) is { } data
                    ? new JsonObject { ["type"] = "image", ["source"] = Base64(data.Mime, data.Data) }
                    : new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "url", ["url"] = url }
                    };

            case "file" when part.TryGetProperty("file", out var file) && Text(file, "file_data") is { } fileData:
                if (DataUrl(fileData) is { Mime: "application/pdf" } pdf)
                {
                    return new JsonObject { ["type"] = "document", ["source"] = Base64(pdf.Mime, pdf.Data) };
                }

                // Других файлов Messages API не берёт; модель хотя бы узнает, что файл был.
                return new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"[file {Text(file, "filename") ?? "attachment"} is not readable here]"
                };

            default:
                return null;
        }
    }

    private static JsonObject Base64(string mime, string data) =>
        new() { ["type"] = "base64", ["media_type"] = mime, ["data"] = data };

    private static (string Mime, string Data)? DataUrl(string url)
    {
        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var comma = url.IndexOf(',', StringComparison.Ordinal);
        var semicolon = url.IndexOf(';', StringComparison.Ordinal);
        if (comma < 0 || semicolon < 0 || semicolon > comma)
        {
            return null;
        }

        return (url[5..semicolon].ToLowerInvariant(), url[(comma + 1)..]);
    }

    /// <summary>
    /// Инструменты. Без своих инструментов у запроса, в истории которого вызовы уже есть
    /// (итоговый ответ после раунда), объявляются те же имена с запретом вызывать: Anthropic не
    /// принимает историю с вызовами без объявленных инструментов.
    /// </summary>
    private static void AddTools(JsonObject body, ChatCompletionRequest request, List<(string Role, JsonArray Blocks)> turns)
    {
        var tools = new JsonArray();
        if (request.Tools is { Count: > 0 } declared)
        {
            foreach (var tool in declared)
            {
                tools.Add(new JsonObject
                {
                    ["name"] = tool.Function.Name,
                    ["description"] = tool.Function.Description,
                    ["input_schema"] = JsonNode.Parse(tool.Function.Parameters.GetRawText())
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = new JsonObject
            {
                ["type"] = string.Equals(request.ToolChoice, "none", StringComparison.OrdinalIgnoreCase) ? "none" : "auto"
            };
            return;
        }

        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (_, blocks) in turns)
        {
            foreach (var block in blocks)
            {
                if (block is JsonObject node && (string?)node["type"] == "tool_use" && (string?)node["name"] is { } name)
                {
                    used.Add(name);
                }
            }
        }

        if (used.Count == 0)
        {
            return;
        }

        foreach (var name in used)
        {
            tools.Add(new JsonObject
            {
                ["name"] = name,
                ["description"] = "",
                ["input_schema"] = new JsonObject { ["type"] = "object" }
            });
        }

        body["tools"] = tools;
        body["tool_choice"] = new JsonObject { ["type"] = "none" };
    }

    /// <summary>
    /// Размышление и его уровень. Возвращает, включено ли адаптивное размышление: только к нему
    /// прикладывается просьба отбрасывать устаревшие блоки.
    /// </summary>
    private static bool AddThinking(JsonObject body, ReasoningChoice? choice, VeniceModelInfo? info, int maxTokens)
    {
        if (choice is not { } wanted)
        {
            return false;
        }

        var modes = info?.ModelSpec?.Capabilities?.ThinkingModes;
        var efforts = info?.ModelSpec?.Capabilities?.ReasoningEffortOptions;

        if (!wanted.DisableThinking)
        {
            var adaptive = modes is null || modes.Contains("adaptive");
            if (adaptive)
            {
                // Без «summarized» текст размышления у моделей 4.7+ приходит пустым, и в ленте
                // вместо хода мыслей стояла бы долгая пауза.
                body["thinking"] = new JsonObject { ["type"] = "adaptive", ["display"] = "summarized" };
            }
            else if (modes is not null && modes.Contains("enabled"))
            {
                // Бюджет обязан быть меньше потолка ответа и не меньше 1024 — иначе отказ.
                body["thinking"] = new JsonObject
                {
                    ["type"] = "enabled",
                    ["budget_tokens"] = Math.Clamp(maxTokens / 2, 1024, Math.Max(1024, maxTokens - 1))
                };
            }

            var effort = ReasoningPolicy.ClampEffort(wanted.Effort, info, withTools: true) ??
                         (efforts is null ? ReasoningPolicy.CommonEfforts.FirstOrDefault(
                             level => level.Equals(wanted.Effort, StringComparison.OrdinalIgnoreCase)) : null);
            if (effort is not null)
            {
                body["output_config"] = new JsonObject { ["effort"] = effort };
            }

            return adaptive;
        }

        if (modes is null)
        {
            return false;
        }

        if (modes.Contains("disabled"))
        {
            body["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if (modes.Contains("between_tools"))
        {
            body["thinking"] = new JsonObject { ["type"] = "between_tools" };
        }
        else if (efforts is not null && efforts.Contains("low"))
        {
            // Выключить нельзя вовсе (Opus 5.5) — самое меньшее, что можно попросить.
            body["output_config"] = new JsonObject { ["effort"] = "low" };
        }

        return false;
    }

    private static int MaxTokens(VeniceModelInfo? info, bool stream)
    {
        var ceiling = stream ? StreamMaxTokens : PlainMaxTokens;
        return info?.ModelSpec?.MaxOutputTokens is { } known && known > 0 ? Math.Min(known, ceiling) : ceiling;
    }

    internal static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number)
            ? number
            : 0;
}
