using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Копии настроек клиента собираются через <c>with</c> и наследуют каждое поле. До 1.30.0 их
/// перечисляли руками, и все копии — агента, заголовков, сводок и отчёта — теряли
/// <see cref="AgentOptions.BalanceSink"/>: остаток из их ответов до плашки не доходил.
/// </summary>
public sealed class AgentOptionsCopyTests
{
    private const string Answer =
        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Про диск D\"}}],\"cost\":{\"usd\":0.0004,\"diem\":0}}";

    [Fact]
    public void An_agent_copy_keeps_every_hook_of_the_program()
    {
        Action<string, VeniceCost, string> spend = (_, _, _) => { };
        Action<ApiCredential, VeniceBalance> balance = (_, _) => { };
        Func<ApiCredential, CancellationToken, Task> gate = (_, _) => Task.CompletedTask;
        var audit = new AuditLog(Path.GetTempPath());
        var source = new AgentOptions
        {
            ApiKey = "k",
            SpendSink = spend,
            BalanceSink = balance,
            SpendGate = gate,
            Audit = audit,
            MaxToolRounds = 7,
            Download = new DownloadOptions { MaxSizeBytes = 42 }
        };

        var copy = AgentHost.CloneOptions(source, "glm-5", new ReasoningSettings { DisableThinking = false, ReasoningEffort = "high" });

        Assert.Same(spend, copy.SpendSink);
        Assert.Same(balance, copy.BalanceSink);
        Assert.Same(gate, copy.SpendGate);
        Assert.Same(audit, copy.Audit);
        Assert.Equal(7, copy.MaxToolRounds);
        Assert.Equal(42, copy.Download.MaxSizeBytes);
        Assert.Equal("glm-5", copy.Model);
        Assert.Equal("high", copy.ReasoningEffort);
        Assert.False(copy.EnableXSearch);

        // Копия — новый объект: сдвиг модели у одного клиента не уводит другой.
        Assert.Equal("grok-4-6", source.Model);
    }

    [Fact]
    public async Task A_title_answer_reports_the_balance_it_saw()
    {
        var seen = new List<decimal?>();
        using var http = Http();
        var titles = new ChatTitleGenerator(http, Options(seen), () => new AppSettings());

        await titles.GenerateAsync("почему диск D заполнен?");

        Assert.Equal([4.20m], seen);
    }

    [Fact]
    public async Task A_summary_answer_reports_the_balance_it_saw()
    {
        var seen = new List<decimal?>();
        using var http = Http();
        var summaries = new ChatSummaryGenerator(http, Options(seen), () => new AppSettings());

        await summaries.CompactAsync("человек: привет\nассистент: здравствуйте");

        Assert.Equal([4.20m], seen);
    }

    [Fact]
    public async Task A_work_report_answer_reports_the_balance_it_saw()
    {
        var seen = new List<decimal?>();
        using var http = Http();
        var reports = new WorkReportWriter(http, Options(seen), () => new AppSettings());

        await reports.WriteAsync("почисти временные файлы", [], CancellationToken.None);

        Assert.Equal([4.20m], seen);
    }

    [Fact]
    public void The_options_never_print_the_key()
    {
        var options = new AgentOptions { ApiKey = "vk-very-secret-0123456789" };

        Assert.DoesNotContain("very-secret", options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", (options with { Model = "glm-5" }).ToString(), StringComparison.Ordinal);
    }

    private static AgentOptions Options(List<decimal?> seen) => new()
    {
        ApiKey = "test",
        BaseUrl = "https://api.venice.ai/api/v1",
        Model = "grok-4-6",
        BalanceSink = (_, balance) =>
        {
            lock (seen)
            {
                seen.Add(balance.Usd);
            }
        }
    };

    private static HttpClient Http() =>
        new(new BalanceHandler()) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };

    private sealed class BalanceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Answer, Encoding.UTF8, "application/json")
            };
            response.Headers.Add("x-venice-balance-usd", "4.20");
            return Task.FromResult(response);
        }
    }
}
