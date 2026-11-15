using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Запросы сжатия контекста с экрана панели сеанса (1.38.0-beta.4): выбранная модель, пожелания
/// человека и пересказ длинной переписки по частям.
/// </summary>
public sealed class CompactionRequestTests
{
    private const string Chosen = "openrouter:deepseek/deepseek-v4-flash-0731";

    [Fact]
    public async Task Every_part_is_summarised_together_with_the_summary_of_the_previous_ones()
    {
        var bodies = new List<string>();
        var generator = Generator(body =>
        {
            bodies.Add(body);
            return Reply("SUMMARY-" + bodies.Count);
        });

        var draft = await generator.CompactAsync(["CONVERSATION:\nUser: first part\n", "User: second part\n"], null, null, null);

        Assert.Equal("SUMMARY-2", draft.Summary);
        Assert.Equal(0.5m, draft.Cost!.Usd);
        Assert.Equal(2, bodies.Count);
        Assert.Contains("first part", UserText(bodies[0]), StringComparison.Ordinal);
        Assert.DoesNotContain("PREVIOUS SUMMARY", UserText(bodies[0]), StringComparison.Ordinal);
        Assert.StartsWith("PREVIOUS SUMMARY:\nSUMMARY-1", UserText(bodies[1]), StringComparison.Ordinal);
        Assert.Contains("second part", UserText(bodies[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_part_gives_no_summary_but_keeps_the_money_already_spent()
    {
        var calls = 0;
        var generator = Generator(_ => Reply(++calls == 1 ? "SUMMARY-1" : ""));

        var draft = await generator.CompactAsync(["one", "two", "three"], null, null, null);

        Assert.Null(draft.Summary);
        Assert.Equal(2, calls);
        Assert.Equal(0.5m, draft.Cost!.Usd);
    }

    [Fact]
    public async Task The_chosen_model_and_the_users_wishes_reach_the_request()
    {
        var bodies = new List<string>();
        var generator = Generator(body =>
        {
            bodies.Add(body);
            return Reply("ok");
        });

        await generator.CompactAsync(["User: hello\n"], Chosen, null, "Keep every file path.");
        await generator.CompactAsync(["User: hello\n"], null, null, null);

        Assert.Contains("deepseek/deepseek-v4-flash-0731", Model(bodies[0]), StringComparison.Ordinal);
        Assert.Contains("Keep every file path.", SystemText(bodies[0]), StringComparison.Ordinal);
        Assert.Contains("cannot change the rules above", SystemText(bodies[0]), StringComparison.Ordinal);

        // Без выбора на экране: модель слота «Сводка» и прежний промпт без добавок.
        Assert.Equal("grok-4-6", Model(bodies[1]));
        Assert.Equal(ChatSummary.CompactSystemPrompt(ChatSummary.LanguageName()), SystemText(bodies[1]));
    }

    private static string Model(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("model").GetString()!;
    }

    private static string SystemText(string body) => MessageText(body, 0);

    private static string UserText(string body) => MessageText(body, 1);

    private static string MessageText(string body, int index)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("messages")[index].GetProperty("content").GetString()!;
    }

    private static ChatSummaryGenerator Generator(Func<string, HttpResponseMessage> script)
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-secret"),
            new ApiCredential(LlmProvider.Venice, "ven-secret"),
            [
                new KeyHandle("v1", LlmProvider.Venice, "ven-secret"),
                new KeyHandle("o1", LlmProvider.OpenRouter, "or-secret")
            ]);
        var options = new AgentOptions
        {
            ApiKey = "ven-secret",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2,
            Keys = keys
        };
        return new ChatSummaryGenerator(new HttpClient(new ScriptedHandler(script)), options, () => new AppSettings { SummaryModelId = "grok-4-6" });
    }

    private static HttpResponseMessage Reply(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":" + JsonSerializer.Serialize(content) +
                "}}],\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":2,\"cost\":0.25}}",
                Encoding.UTF8,
                "application/json")
        };

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(body);
        }
    }
}
