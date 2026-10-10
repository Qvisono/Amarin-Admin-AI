using System.Text.Json;
using System.Text.Json.Nodes;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Перевод запроса программы в Messages API Anthropic и ответа обратно.
/// </summary>
/// <remarks>
/// Вся программа говорит в форме <c>/chat/completions</c>; ошибка перевода не видна глазом —
/// Anthropic просто отвечает 400, и ход стоит.
/// </remarks>
public sealed class AnthropicMessagesTests
{
    private static ChatMessage System(string text) => new() { Role = "system", Content = ChatContent.Text(text) };

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static ChatMessage Assistant(string? text, params ToolCall[] calls) => new()
    {
        Role = "assistant",
        Content = text is null ? null : ChatContent.Text(text),
        ToolCalls = calls.Length > 0 ? [.. calls] : null
    };

    private static ChatMessage Tool(string id, string text) => new()
    {
        Role = "tool",
        ToolCallId = id,
        Content = ChatContent.Text(text)
    };

    private static ToolCall Call(string id, string name, string arguments) => new()
    {
        Id = id,
        Function = new FunctionCall { Name = name, Arguments = arguments }
    };

    private static ToolDefinition Definition(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = "reads a file",
            Parameters = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""").RootElement.Clone()
        }
    };

    private static VeniceModelInfo Model(List<string>? modes, List<string>? efforts, int? maxOutput = null) => new()
    {
        Id = "anthropic:claude-sonnet-5-5",
        ModelSpec = new VeniceModelSpec
        {
            MaxOutputTokens = maxOutput,
            Capabilities = new VeniceModelCapabilities
            {
                SupportsFunctionCalling = true,
                SupportsReasoning = true,
                SupportsReasoningEffort = efforts is not { Count: 0 },
                ReasoningEffortOptions = efforts,
                ThinkingModes = modes
            }
        }
    };

    private static JsonObject Body(
        List<ChatMessage> messages,
        ReasoningChoice? choice = null,
        VeniceModelInfo? info = null,
        List<ToolDefinition>? tools = null,
        bool official = true,
        bool relaxed = false) =>
        AnthropicMessages.Build(
            new ChatCompletionRequest
            {
                Model = "anthropic:claude-sonnet-5-5",
                Messages = messages,
                Tools = tools,
                ToolChoice = tools is null ? null : "auto",
                ReasoningChoice = choice
            },
            "claude-sonnet-5-5",
            info,
            stream: true,
            official,
            relaxed).Json;

    [Fact]
    public void System_messages_go_to_the_top_and_temperature_never_does()
    {
        var body = Body([System("rule one"), User("hi"), System("rule two")]);

        Assert.Equal("rule one\n\nrule two", (string?)body["system"]);
        Assert.Null(body["temperature"]);
        Assert.Equal("claude-sonnet-5-5", (string?)body["model"]);
        Assert.All(body["messages"]!.AsArray(), message => Assert.NotEqual("system", (string?)message!["role"]));
    }

    /// <summary>
    /// Ответы инструментов — блоками <c>tool_result</c> в сообщении пользователя, и картинки,
    /// пришедшие за ними, — в том же сообщении: Anthropic требует чередования ролей.
    /// </summary>
    [Fact]
    public void Tool_results_and_their_pictures_share_one_user_turn()
    {
        var picture = new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ToolImages("screenshot", "here", [new Amarin.Tools.ImageAttachment("QUJD", "image/png")])
        };

        var body = Body(
        [
            User("look"),
            Assistant("checking", Call("call_1", "screenshot", "{}"), Call("call_2", "read_file", "{\"path\":\"a\"}")),
            Tool("call_1", "done"),
            Tool("call_2", ""),
            picture
        ], tools: [Definition("screenshot"), Definition("read_file")]);

        var messages = body["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => (string?)m!["role"]));

        var assistant = messages[1]!["content"]!.AsArray();
        Assert.Equal("text", (string?)assistant[0]!["type"]);
        Assert.Equal("tool_use", (string?)assistant[1]!["type"]);
        Assert.Equal("a", (string?)assistant[2]!["input"]!["path"]);

        var results = messages[2]!["content"]!.AsArray();
        Assert.Equal("tool_result", (string?)results[0]!["type"]);
        Assert.Equal("call_1", (string?)results[0]!["tool_use_id"]);
        Assert.Equal("(no output)", (string?)results[1]!["content"]);
        Assert.Equal("image", (string?)results[3]!["type"]);
        Assert.Equal("base64", (string?)results[3]!["source"]!["type"]);
        Assert.Equal("image/png", (string?)results[3]!["source"]!["media_type"]);
    }

    /// <summary>
    /// Подписанное размышление возвращается только у ответов текущего раунда: подпись привязана
    /// к истории перед блоком, и старые блоки всё равно потеряли бы силу.
    /// </summary>
    [Fact]
    public void Only_the_current_round_returns_its_signed_thinking()
    {
        var thinking = JsonDocument.Parse("""[{"type":"thinking","thinking":"plan","signature":"sig"}]""").RootElement.Clone();
        var old = new ChatMessage { Role = "assistant", Content = ChatContent.Text("earlier"), ReplayBlocks = thinking };
        var current = new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [Call("call_1", "read_file", "{}")],
            ReplayBlocks = thinking
        };

        var body = Body(
            [User("first"), old, User("second"), current, Tool("call_1", "text")],
            choice: new ReasoningChoice(false, "low"),
            info: Model(["adaptive"], ["low", "medium", "high"]),
            tools: [Definition("read_file")]);

        var messages = body["messages"]!.AsArray();
        Assert.Equal("text", (string?)messages[1]!["content"]![0]!["type"]);
        Assert.Equal("thinking", (string?)messages[3]!["content"]![0]!["type"]);
        Assert.Equal("sig", (string?)messages[3]!["content"]![0]!["signature"]);

        // Самой Anthropic — с просьбой отбросить устаревший блок, а не отказывать всем запросом.
        Assert.Equal("drop_block", (string?)body["thinking"]!["block_binding"]!["prefix_mismatch_behavior"]);
    }

    [Fact]
    public void A_custom_server_gets_neither_beta_fields_nor_cache()
    {
        var thinking = JsonDocument.Parse("""[{"type":"thinking","thinking":"plan","signature":"sig"}]""").RootElement.Clone();
        var built = AnthropicMessages.Build(
            new ChatCompletionRequest
            {
                Model = "m",
                Messages = [User("q"), new ChatMessage { Role = "assistant", ToolCalls = [Call("c", "read_file", "{}")], ReplayBlocks = thinking }, Tool("c", "x")],
                Tools = [Definition("read_file")],
                ReasoningChoice = new ReasoningChoice(false, null)
            },
            "m",
            Model(["adaptive"], null),
            stream: true,
            official: false);

        Assert.False(built.NeedsBindingBeta);
        Assert.Null(built.Json["cache_control"]);
        Assert.Null(built.Json["thinking"]!["block_binding"]);
    }

    [Fact]
    public void Thinking_on_is_adaptive_with_a_readable_summary_and_the_level()
    {
        var body = Body([User("q")], new ReasoningChoice(false, "medium"), Model(["adaptive"], ["low", "medium", "high"]));

        Assert.Equal("adaptive", (string?)body["thinking"]!["type"]);
        Assert.Equal("summarized", (string?)body["thinking"]!["display"]);
        Assert.Equal("medium", (string?)body["output_config"]!["effort"]);
    }

    /// <summary>
    /// «Без размышления» — тем выключателем, который принимает модель: у моделей 5.5 без поля
    /// размышление включается само, а <c>disabled</c> Sonnet 5.5 отвергает.
    /// </summary>
    [Theory]
    [InlineData("adaptive,disabled", "disabled", null)]
    [InlineData("adaptive,between_tools", "between_tools", null)]
    [InlineData("adaptive", null, "low")]
    public void Thinking_off_uses_the_switch_the_model_accepts(string modes, string? type, string? effort)
    {
        var body = Body(
            [User("q")],
            ReasoningChoice.Disabled,
            Model([.. modes.Split(',')], ["low", "medium", "high"]));

        Assert.Equal(type, (string?)body["thinking"]?["type"]);
        Assert.Equal(effort, (string?)body["output_config"]?["effort"]);
    }

    [Fact]
    public void A_relaxed_retry_drops_everything_about_thinking()
    {
        var thinking = JsonDocument.Parse("""[{"type":"thinking","thinking":"plan","signature":"sig"}]""").RootElement.Clone();
        var body = Body(
            [User("q"), new ChatMessage { Role = "assistant", ToolCalls = [Call("c", "read_file", "{}")], ReplayBlocks = thinking }, Tool("c", "x")],
            new ReasoningChoice(false, "high"),
            Model(["adaptive"], ["high"]),
            tools: [Definition("read_file")],
            relaxed: true);

        Assert.Null(body["thinking"]);
        Assert.Null(body["output_config"]);
        Assert.Equal("tool_use", (string?)body["messages"]![1]!["content"]![0]!["type"]);
    }

    /// <summary>
    /// Итоговый ответ после раунда идёт без своих инструментов, а в истории вызовы есть:
    /// Anthropic такую историю без объявленных инструментов не принимает.
    /// </summary>
    [Fact]
    public void A_history_with_calls_but_no_tools_declares_them_as_not_callable()
    {
        var body = Body([User("q"), Assistant(null, Call("c", "read_file", "{}")), Tool("c", "x"), User("sum up")]);

        Assert.Equal("read_file", (string?)body["tools"]![0]!["name"]);
        Assert.Equal("none", (string?)body["tool_choice"]!["type"]);
    }

    [Fact]
    public void A_pdf_becomes_a_document_and_other_files_a_note()
    {
        var content = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "text", text = "read these" },
            new { type = "file", file = new { file_data = "data:application/pdf;base64,JVBERg==", filename = "a.pdf" } },
            new { type = "file", file = new { file_data = "data:application/zip;base64,UEsDBA==", filename = "b.zip" } }
        });

        var blocks = Body([new ChatMessage { Role = "user", Content = content }])["messages"]![0]!["content"]!.AsArray();

        Assert.Equal("document", (string?)blocks[1]!["type"]);
        Assert.Equal("application/pdf", (string?)blocks[1]!["source"]!["media_type"]);
        Assert.Equal("text", (string?)blocks[2]!["type"]);
        Assert.Contains("b.zip", (string?)blocks[2]!["text"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_answer_ceiling_follows_the_model()
    {
        Assert.Equal(8_000, (int)Body([User("q")], info: Model(null, null, maxOutput: 8_000))["max_tokens"]!);
        Assert.Equal(32_000, (int)Body([User("q")], info: Model(null, null, maxOutput: 128_000))["max_tokens"]!);
    }

    [Fact]
    public void A_whole_answer_reads_back_as_a_completion()
    {
        const string json = """
            {"type":"message","role":"assistant","stop_reason":"tool_use",
             "content":[
               {"type":"thinking","thinking":"need the file","signature":"s1"},
               {"type":"text","text":"Reading."},
               {"type":"tool_use","id":"toolu_1","name":"read_file","input":{"path":"a.txt"}}],
             "usage":{"input_tokens":100,"cache_read_input_tokens":20,"output_tokens":7}}
            """;

        var response = AnthropicMessages.ParseResponse(json);
        var message = response.Choices[0].Message;

        Assert.Equal("Reading.", ChatContent.ReadText(message.Content));
        Assert.Equal("need the file", ChatContent.ReadText(message.ReasoningContent));
        Assert.Equal("tool_calls", response.Choices[0].FinishReason);
        Assert.Equal("toolu_1", message.ToolCalls![0].Id);
        Assert.Equal("a.txt", JsonDocument.Parse(message.ToolCalls[0].Function.Arguments).RootElement.GetProperty("path").GetString());
        Assert.Equal("s1", message.ReplayBlocks!.Value[0].GetProperty("signature").GetString());
        Assert.Equal(120, response.Usage!.PromptTokens);
        Assert.Equal(7, response.Usage.CompletionTokens);
    }

    /// <summary>Отказ классификатора — отказ, а не пустой ответ, который ход повторил бы снова.</summary>
    [Fact]
    public void A_refusal_is_an_error_not_an_empty_answer()
    {
        var error = Assert.Throws<VeniceApiException>(() => AnthropicMessages.ParseResponse(
            """{"type":"message","stop_reason":"refusal","content":[],"usage":{"input_tokens":1,"output_tokens":0}}"""));

        Assert.Equal(Loc.Get("S.Provider.Refusal"), error.Message);
    }

    [Fact]
    public void The_stream_becomes_ordinary_chunks()
    {
        var events = new[]
        {
            """{"type":"message_start","message":{"usage":{"input_tokens":50,"cache_creation_input_tokens":5}}}""",
            """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"hmm"}}""",
            """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"abc"}}""",
            """{"type":"content_block_stop","index":0}""",
            """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hel"}}""",
            """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"lo"}}""",
            """{"type":"content_block_stop","index":1}""",
            """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_9","name":"read_file","input":{}}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"pa"}}""",
            """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"th\":\"x\"}"}}""",
            """{"type":"content_block_stop","index":2}""",
            """{"type":"ping"}""",
            """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":12}}""",
            """{"type":"message_stop"}"""
        };

        var translator = new AnthropicStream();
        var accumulator = new ChatStreamAccumulator();
        foreach (var data in events)
        {
            if (translator.Translate(data) is { } chunk)
            {
                accumulator.Apply(chunk);
            }
        }

        Assert.Equal("Hello", accumulator.Text);
        Assert.Equal("hmm", accumulator.ReasoningText);
        Assert.Equal("tool_calls", accumulator.FinishReason);
        Assert.Equal(55, accumulator.PromptTokens);
        Assert.Equal(12, accumulator.CompletionTokens);

        var call = Assert.Single(accumulator.BuildToolCalls());
        Assert.Equal("toolu_9", call.Id);
        Assert.Equal("read_file", call.Function.Name);
        Assert.Equal("x", JsonDocument.Parse(call.Function.Arguments).RootElement.GetProperty("path").GetString());

        var replay = translator.ReplayBlocks!.Value;
        Assert.Equal("hmm", replay[0].GetProperty("thinking").GetString());
        Assert.Equal("abc", replay[0].GetProperty("signature").GetString());
    }

    [Fact]
    public void A_stream_error_event_stops_the_turn()
    {
        var error = Assert.Throws<VeniceApiException>(() => new AnthropicStream().Translate(
            """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""));

        Assert.Equal("Overloaded", error.Message);
    }

    [Theory]
    [InlineData(400, "thinking.type: Input tag 'adaptive' found using 'type' does not match", true)]
    [InlineData(400, "messages.3.content.0: Invalid `signature` in `thinking` block", true)]
    [InlineData(400, "max_tokens: must be at most 8192", false)]
    [InlineData(500, "thinking failed", false)]
    public void Only_a_refused_thinking_field_is_retried_without_thinking(int status, string error, bool expected) =>
        Assert.Equal(expected, AnthropicMessages.MayRelax(status, error));
}
