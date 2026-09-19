using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// График трат по ключу провайдера, у которого журнала списаний нет.
/// </summary>
/// <remarks>
/// Сходить и получить отказ — не то же самое, что не ходить вовсе: страница трат открывается
/// по каждому клику в настройках, а ключей у человека может быть несколько, и каждый лишний
/// запрос он ждёт. Своего журнала программы для такого ключа достаточно.
/// </remarks>
public sealed class SpendServiceProviderTests : IDisposable
{
    private const string OpenRouterKey = "sk-or-v1-abcdef";
    private const string VeniceKey = "vk-abcdef";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-spendprov-" + Guid.NewGuid().ToString("N"));

    public SpendServiceProviderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (SpendService Service, SpendLedger Ledger, Recorder Recorder) Build()
    {
        var recorder = new Recorder();
        var client = new VeniceClient(
            new HttpClient(recorder),
            new AgentOptions { ApiKey = VeniceKey, BaseUrl = "https://api.venice.ai/api/v1" });

        var ledger = new SpendLedger(_root);
        return (new SpendService(client, new SpendHistoryStore(_root), ledger), ledger, recorder);
    }

    [Fact]
    public async Task An_openrouter_key_never_asks_for_a_journal_that_does_not_exist()
    {
        var (service, ledger, recorder) = Build();
        ledger.Record(OpenRouterKey, new VeniceCost { Usd = 0.25m, HasData = true }, "openrouter:openai/gpt-5");
        ledger.Flush();

        var report = await service.GetReportAsync(
            new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey),
            SpendPeriod.Month,
            force: true);

        Assert.Empty(recorder.Urls);
        Assert.Equal(SpendStatus.Local, report.Status);
        Assert.Equal(0.25m, report.TotalUsd);
    }

    /// <summary>
    /// Разбивка «на что ушло» по-прежнему называет модель, а не сводит все траты в «прочее»:
    /// собственная пометка журнала — точный идентификатор модели.
    /// </summary>
    [Fact]
    public async Task The_breakdown_still_names_the_model()
    {
        var (service, ledger, _) = Build();
        ledger.Record(OpenRouterKey, new VeniceCost { Usd = 0.1m, HasData = true },
                      "openrouter:anthropic/claude-sonnet-4.5");
        ledger.Flush();

        var report = await service.GetReportAsync(
            new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey),
            SpendPeriod.Month,
            force: true);

        Assert.Contains(report.Models, row => row.Title.Contains("Claude", StringComparison.Ordinal));
    }

    /// <summary>А ключ Venice за журналом ходит, как и ходил: там он есть.</summary>
    [Fact]
    public async Task A_venice_key_still_goes_for_the_journal()
    {
        var (service, _, recorder) = Build();

        await service.GetReportAsync(
            new ApiCredential(LlmProvider.Venice, VeniceKey), SpendPeriod.Month, force: true);

        Assert.Contains(recorder.Urls, url => url.Contains("billing/usage-history", StringComparison.Ordinal));
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
