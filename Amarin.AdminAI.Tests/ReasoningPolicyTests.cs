using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class ReasoningPolicyTests
{
    [Fact]
    public void Effort_options_come_from_the_catalogue_and_drop_none()
    {
        var model = Model(
            supportsEffort: true,
            options: ["none", "low", "medium", "high", "max"],
            defaultEffort: "medium");

        Assert.True(ReasoningPolicy.SupportsEffort(model));
        Assert.Equal(["low", "medium", "high", "max"], ReasoningPolicy.VisibleEffortOptions(model));
        Assert.True(ReasoningPolicy.HasNoneOption(model));
        Assert.Equal("medium", ReasoningPolicy.ClampEffort(null, model));
        Assert.Equal("max", ReasoningPolicy.ClampEffort("MAX", model));
        Assert.Equal("medium", ReasoningPolicy.ClampEffort("xhigh", model));
    }

    [Fact]
    public void Missing_options_array_falls_back_to_low_medium_high()
    {
        var model = Model(supportsEffort: true, options: null, defaultEffort: null);
        Assert.Equal(["low", "medium", "high"], ReasoningPolicy.VisibleEffortOptions(model));
        Assert.Equal("medium", ReasoningPolicy.ClampEffort("max", model));
    }

    [Fact]
    public void Grok_like_models_have_no_effort_chips()
    {
        var model = Model(supportsEffort: false, options: null, defaultEffort: null);
        Assert.False(ReasoningPolicy.SupportsEffort(model));
        Assert.Empty(ReasoningPolicy.VisibleEffortOptions(model));
        Assert.Null(ReasoningPolicy.ClampEffort("high", model));
    }

    [Fact]
    public void Disable_never_sends_effort()
    {
        var claude = Model(
            supportsEffort: true,
            options: ["low", "medium", "high"],
            defaultEffort: "medium");

        var wire = ReasoningPolicy.ToWire(claude, ReasoningChoice.Disabled);
        Assert.True(wire.DisableThinking);
        Assert.Null(wire.ReasoningEffort);
    }

    [Fact]
    public void Enabled_effort_is_clamped_and_does_not_disable()
    {
        var claude = Model(
            supportsEffort: true,
            options: ["low", "medium", "high"],
            defaultEffort: "medium");

        var wire = ReasoningPolicy.ToWire(claude, new ReasoningChoice(false, "xhigh"));
        Assert.Null(wire.DisableThinking);
        Assert.Equal("medium", wire.ReasoningEffort);
    }

    [Fact]
    public void Enabled_without_effort_support_omits_both()
    {
        var grok = Model(supportsEffort: false, options: null, defaultEffort: null);
        var wire = ReasoningPolicy.ToWire(grok, new ReasoningChoice(false, "high"));
        Assert.Null(wire.DisableThinking);
        Assert.Null(wire.ReasoningEffort);
    }

    [Fact]
    public void Button_text_matches_state()
    {
        var claude = Model(
            supportsEffort: true,
            options: ["low", "medium", "high"],
            defaultEffort: "medium");

        Assert.Equal("Выкл", ReasoningPolicy.ButtonText(ReasoningChoice.Disabled, claude, autoMode: false));
        Assert.Equal("High", ReasoningPolicy.ButtonText(new ReasoningChoice(false, "high"), claude, autoMode: false));
        Assert.Equal("Вкл", ReasoningPolicy.ButtonText(new ReasoningChoice(false, "high"), claude, autoMode: true));
        Assert.Equal("Вкл", ReasoningPolicy.ButtonText(new ReasoningChoice(false, null), model: null, autoMode: false));
    }

    [Theory]
    [InlineData("gpt-5.6-luna", true)]
    [InlineData("openai-gpt-56-luna", true)]
    [InlineData("gpt-5-6-terra", true)]
    [InlineData("gpt-54", true)]
    [InlineData("gpt-6", true)]
    [InlineData("openai-gpt-53-codex", false)]
    [InlineData("claude-sonnet-5", false)]
    [InlineData("grok-4-6", false)]
    public void Gpt56_family_cannot_combine_tools_with_effort(string id, bool blocked)
    {
        Assert.Equal(blocked, ReasoningPolicy.FamilyBlocksEffortWithTools(id));
        var model = Model(
            supportsEffort: true,
            options: ["none", "low", "medium", "max"],
            defaultEffort: "medium",
            id: id);
        Assert.Equal(!blocked, ReasoningPolicy.AllowsEffortWithTools(model, id));
        if (blocked)
        {
            Assert.Empty(ReasoningPolicy.VisibleEffortOptions(model, withTools: true));
            Assert.Equal(["low", "medium", "max"], ReasoningPolicy.VisibleEffortOptions(model, withTools: false));
        }
    }

    [Fact]
    public void Tools_on_gpt56_force_none_even_when_thinking_is_on()
    {
        var luna = Model(
            supportsEffort: true,
            options: ["none", "low", "medium", "max"],
            defaultEffort: "medium",
            id: "gpt-5.6-luna");

        var enabled = ReasoningPolicy.ToWire(luna, new ReasoningChoice(false, "max"), withTools: true);
        Assert.Equal("none", enabled.ReasoningEffort);
        Assert.Null(enabled.DisableThinking);

        var disabled = ReasoningPolicy.ToWire(luna, ReasoningChoice.Disabled, withTools: true);
        Assert.Equal("none", disabled.ReasoningEffort);
        Assert.True(disabled.DisableThinking);
    }

    [Fact]
    public void Catalogue_flag_false_hides_effort_with_tools()
    {
        var model = new VeniceModelInfo
        {
            Id = "mystery-reasoner",
            ModelSpec = new VeniceModelSpec
            {
                Capabilities = new VeniceModelCapabilities
                {
                    SupportsReasoning = true,
                    SupportsReasoningEffort = true,
                    SupportsReasoningEffortWithTools = false,
                    ReasoningEffortOptions = ["low", "medium", "high"],
                    DefaultReasoningEffort = "medium"
                }
            }
        };

        Assert.False(ReasoningPolicy.AllowsEffortWithTools(model));
        Assert.Empty(ReasoningPolicy.VisibleEffortOptions(model, withTools: true));
    }

    [Fact]
    public void Chat_completion_request_writes_effort_and_skips_disable()
    {
        var request = new ChatCompletionRequest
        {
            Model = "claude-sonnet-5",
            Messages = [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            ReasoningEffort = "high",
            VeniceParameters = new VeniceParameters { StripThinkingResponse = true }
        };

        var json = JsonSerializer.Serialize(request, VeniceJsonContext.Default.ChatCompletionRequest);
        Assert.Contains("\"reasoning_effort\":\"high\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("disable_thinking", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_completion_request_writes_venice_disable_thinking_only()
    {
        var request = new ChatCompletionRequest
        {
            Model = "qwen-3-7-plus",
            Messages = [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            VeniceParameters = new VeniceParameters
            {
                DisableThinking = true,
                StripThinkingResponse = true
            }
        };

        var json = JsonSerializer.Serialize(request, VeniceJsonContext.Default.ChatCompletionRequest);
        Assert.Contains("\"disable_thinking\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fallback_drops_effort_on_a_model_that_cannot_take_it()
    {
        var calls = 0;
        var bodies = new List<string>();
        var handler = new ScriptedHandler((_, body) =>
        {
            calls++;
            bodies.Add(body);
            if (calls == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("""{"error":{"message":"overload"}}""", Encoding.UTF8, "application/json")
                };
            }

            return Json("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var venice = new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "claude-sonnet-5"
        });
        venice.ResolveModelInfo = id => id.Equals("claude-sonnet-5", StringComparison.OrdinalIgnoreCase)
            ? Model(supportsEffort: true, options: ["low", "medium", "high"], defaultEffort: "medium")
            : Model(supportsEffort: false, options: null, defaultEffort: null, id: id);

        var response = await venice.CreateChatCompletionAsync(
            "claude-sonnet-5",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: null,
            toolChoice: null,
            new VeniceParameters { StripThinkingResponse = true },
            CancellationToken.None,
            new ReasoningChoice(false, "high"));

        Assert.Equal("ok", ChatContent.ReadText(response.Choices[0].Message.Content));
        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"reasoning_effort\":\"high\"", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", bodies[1], StringComparison.Ordinal);
        Assert.Contains("\"model\":\"grok-4-6\"", bodies[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tools_plus_effort_400_retries_with_none()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler((_, body) =>
        {
            bodies.Add(body);
            if (bodies.Count == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(
                        """{"error":{"message":"Function tools with reasoning_effort are not supported for cline-probe in /v1/chat/completions. To use function tools, use /v1/responses or set reasoning_effort to 'none'."}}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            return Json("""{"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""");
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var venice = new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "cline-probe"
        });

        var tool = new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = "system_info",
                Description = "info",
                Parameters = JsonSchema.Parse("""{"type":"object"}""")
            }
        };

        var response = await venice.CreateChatCompletionAsync(
            "cline-probe",
            [new ChatMessage { Role = "user", Content = ChatContent.Text("hi") }],
            tools: [tool],
            toolChoice: "auto",
            new VeniceParameters { StripThinkingResponse = true },
            CancellationToken.None,
            new ReasoningChoice(false, "high"));

        Assert.Equal("ok", ChatContent.ReadText(response.Choices[0].Message.Content));
        Assert.Equal(2, bodies.Count);
        Assert.Contains("\"reasoning_effort\":\"none\"", bodies[1], StringComparison.Ordinal);
        Assert.False(ReasoningPolicy.AllowsEffortWithTools(model: null, "cline-probe"));
    }

    private static VeniceModelInfo Model(
        bool supportsEffort,
        string[]? options,
        string? defaultEffort,
        string id = "claude-sonnet-5") =>
        new()
        {
            Id = id,
            ModelSpec = new VeniceModelSpec
            {
                Capabilities = new VeniceModelCapabilities
                {
                    SupportsReasoning = true,
                    SupportsReasoningEffort = supportsEffort,
                    ReasoningEffortOptions = options?.ToList(),
                    DefaultReasoningEffort = defaultEffort
                }
            }
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _script;

        public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script) => _script = script;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _script(request, body);
        }
    }
}
