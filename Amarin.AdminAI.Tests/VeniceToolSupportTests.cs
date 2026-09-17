using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Модели, которые заявляют поддержку инструментов, но ломаются, получив их.
/// </summary>
/// <remarks>
/// Inkling отвечал на каждый ход четырёхсоткой с куском грамматики на пол-экрана:
/// <c>Failed to compile structural_tag grammar: at 6(8): unknown name: "read_file"</c>.
/// Venice подставляет имя инструмента в lark-грамматику без кавычек, а там строчный
/// идентификатор — ссылка на правило. Проверено запросами: падает на любом имени и даже на
/// одном инструменте без параметров, то есть наши схемы ни при чём.
/// </remarks>
public sealed class VeniceToolSupportTests
{
    private const string RealError =
        "Failed to compile structural_tag grammar: at 6(8): unknown name: \"read_file\"\n" +
        "   4 | TAG_TEXT: /(.|\\n)*/\n" +
        "   6 | tag_0: TAG_TEXT <|message_model|>read_file<|content_invoke_tool_json|>";

    [Fact]
    public void The_grammar_failure_is_recognised()
    {
        Assert.True(VeniceToolSupport.IsToolGrammarFailure(RealError));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Venice API error (429): rate limit exceeded")]
    [InlineData("Model does not support function tools with reasoning_effort")]
    [InlineData("Insufficient USD balance")]
    public void Other_failures_are_left_alone(string? message)
    {
        // Ошибка узнаётся по двум признакам сразу, чтобы не проглотить чужие отказы:
        // подмена их текста на «выберите другую модель» увела бы человека не туда.
        Assert.False(VeniceToolSupport.IsToolGrammarFailure(message));
    }

    [Fact]
    public void A_remembered_model_drops_out_of_the_agentic_list()
    {
        var models = new[] { Agentic("inkling-probe-1"), Agentic("inkling-probe-2") };

        Assert.Equal(2, VeniceModelCatalog.FilterAgentic(models).Count);

        VeniceToolSupport.Remember("inkling-probe-1");
        var left = VeniceModelCatalog.FilterAgentic(models);

        Assert.Single(left);
        Assert.Equal("inkling-probe-2", left[0].Id);
    }

    [Fact]
    public void Inkling_is_not_offered_at_all()
    {
        // Инструменты уходят в каждом ходе чата и агента, так что рабочего сценария у этой
        // модели сейчас нет ни одного — в списке она была бы кнопкой с гарантированной ошибкой.
        Assert.Empty(VeniceModelCatalog.FilterAgentic([Agentic("inkling")]));
    }

    [Fact]
    public void The_message_shown_to_the_person_names_the_model_and_not_the_grammar()
    {
        var text = VeniceToolSupport.FailureText("inkling");

        Assert.Contains("inkling", text, StringComparison.Ordinal);
        Assert.DoesNotContain("structural_tag", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TAG_TEXT", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S.Venice", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_failure_is_caught_when_it_arrives_over_the_stream()
    {
        // Venice отдаёт этот отказ с кодом 200 и кладёт его чанком в поток — проверено
        // запросом. Ветка про неуспешный HTTP его не видит, и в чат ехал кусок грамматики.
        using var http = new HttpClient(new ScriptedHandler(_ => SseError(RealError)))
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var client = new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "inkling-stream-probe"
        });

        var failure = await Assert.ThrowsAsync<VeniceApiException>(
            () => Stream(client, "inkling-stream-probe"));

        Assert.Contains("inkling-stream-probe", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("structural_tag", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TAG_TEXT", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_stream_error_still_reaches_the_person_as_it_was()
    {
        // Подменять любой отказ на «выберите другую модель» нельзя: про кончившиеся деньги
        // или упавшую сеть человек должен прочитать правду.
        using var http = new HttpClient(new ScriptedHandler(_ => SseError("Insufficient USD balance")))
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var client = new VeniceClient(http, new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        });

        var failure = await Assert.ThrowsAsync<VeniceApiException>(() => Stream(client, "grok-4-6"));

        Assert.Contains("Insufficient USD balance", failure.Message, StringComparison.Ordinal);
    }

    private static Task<StreamedChatCompletion> Stream(VeniceClient client, string model) =>
        client.StreamChatCompletionAsync(
            model,
            model,
            [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("привет") }],
            [Tool()],
            toolChoice: null,
            new VeniceParameters(),
            onText: null,
            CancellationToken.None);

    private static ToolDefinition Tool() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "read_file",
            Description = "Read a file",
            Parameters = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone()
        }
    };

    private static HttpResponseMessage SseError(string message) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"error\":{\"message\":" + JsonSerializer.Serialize(message) + "}}\n" +
                "data: [DONE]\n",
                Encoding.UTF8,
                "text/event-stream")
        };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> script)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(script(request));
    }

    private static VeniceModelInfo Agentic(string id) => new()
    {
        Id = id,
        ModelSpec = new VeniceModelSpec
        {
            Offline = false,
            Capabilities = new VeniceModelCapabilities
            {
                SupportsFunctionCalling = true,
                SupportsReasoning = true
            }
        }
    };
}
