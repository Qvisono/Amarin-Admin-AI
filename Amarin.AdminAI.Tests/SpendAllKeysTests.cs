using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// График трат в режиме «все ключи».
/// </summary>
/// <remarks>
/// С версии 1.23.0 у каждого слота моделей свой ключ, и сумма по одному ключу перестала
/// отвечать на вопрос «сколько стоит программа»: заголовки чатов платит один ключ, разговор —
/// другой, а человек видит только половину денег и не понимает, куда делась вторая.
/// </remarks>
public sealed class SpendAllKeysTests : IDisposable
{
    private const string FirstKey = "sk-or-v1-first";
    private const string SecondKey = "sk-or-v1-second";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-spendall-" + Guid.NewGuid().ToString("N"));

    public SpendAllKeysTests() => Directory.CreateDirectory(_root);

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

    private (SpendService Service, SpendLedger Ledger) Build()
    {
        var client = new VeniceClient(
            new HttpClient(new Offline()),
            new AgentOptions { ApiKey = FirstKey, BaseUrl = "https://api.venice.ai/api/v1" });

        var ledger = new SpendLedger(_root);
        return (new SpendService(client, new SpendHistoryStore(_root), ledger), ledger);
    }

    /// <summary>Сумма по всем ключам равна сумме отчётов по каждому.</summary>
    [Fact]
    public async Task All_keys_add_up_to_the_sum_of_the_singles()
    {
        var (service, ledger) = Build();
        ledger.Record(FirstKey, new VeniceCost { Usd = 0.25m, HasData = true }, "openrouter:openai/gpt-5");
        ledger.Record(SecondKey, new VeniceCost { Usd = 0.75m, HasData = true }, "openrouter:openai/gpt-5-mini");
        ledger.Flush();

        var first = new ApiCredential(LlmProvider.OpenRouter, FirstKey);
        var second = new ApiCredential(LlmProvider.OpenRouter, SecondKey);

        var combined = await service.GetReportAsync([first, second], SpendPeriod.Month, force: false);
        var one = await service.GetReportAsync(first, SpendPeriod.Month, force: false);
        var two = await service.GetReportAsync(second, SpendPeriod.Month, force: false);

        Assert.Equal(1.00m, combined.TotalUsd);
        Assert.Equal(one.TotalUsd + two.TotalUsd, combined.TotalUsd);
    }

    /// <summary>
    /// Ключ из окружения и его же копия в <c>keys.json</c> — один счёт и один файл журнала.
    /// Сложив их как разные, график показал бы двойные деньги.
    /// </summary>
    [Fact]
    public async Task The_same_secret_twice_is_counted_once()
    {
        var (service, ledger) = Build();
        ledger.Record(FirstKey, new VeniceCost { Usd = 0.40m, HasData = true }, "openrouter:openai/gpt-5");
        ledger.Flush();

        var credential = new ApiCredential(LlmProvider.OpenRouter, FirstKey);
        var report = await service.GetReportAsync(
            [credential, credential], SpendPeriod.Month, force: false);

        Assert.Equal(0.40m, report.TotalUsd);
    }

    /// <summary>Одна и та же модель у двух ключей — одна строка разбивки, а не две.</summary>
    [Fact]
    public async Task One_model_stays_one_row()
    {
        var (service, ledger) = Build();
        ledger.Record(FirstKey, new VeniceCost { Usd = 0.10m, HasData = true }, "openrouter:openai/gpt-5");
        ledger.Record(SecondKey, new VeniceCost { Usd = 0.30m, HasData = true }, "openrouter:openai/gpt-5");
        ledger.Flush();

        var report = await service.GetReportAsync(
            [
                new ApiCredential(LlmProvider.OpenRouter, FirstKey),
                new ApiCredential(LlmProvider.OpenRouter, SecondKey)
            ],
            SpendPeriod.Month,
            force: false);

        var row = Assert.Single(report.Models);
        Assert.Equal(0.40m, row.Usd);
    }

    [Fact]
    public async Task No_keys_at_all_says_so()
    {
        var (service, _) = Build();
        var report = await service.GetReportAsync([], SpendPeriod.Month, force: false);
        Assert.Equal(SpendStatus.NoKey, report.Status);
    }

    /// <summary>
    /// Сведение идёт по журналам, а не по готовым отчётам: у отчёта левый край периода
    /// «всё время» считается по первому непустому дню своего файла, и у двух ключей он разный.
    /// </summary>
    [Fact]
    public void Combining_journals_keeps_one_axis()
    {
        var older = new SpendHistoryFile
        {
            Days =
            [
                new SpendDay
                {
                    Date = DateTime.Today.AddDays(-9),
                    Usd = 1m,
                    Skus = [new SpendSkuBucket { Sku = "a", Usd = 1m, Requests = 1 }]
                }
            ]
        };

        var newer = new SpendHistoryFile
        {
            Days =
            [
                new SpendDay
                {
                    Date = DateTime.Today,
                    Usd = 2m,
                    Skus = [new SpendSkuBucket { Sku = "a", Usd = 2m, Requests = 1 }]
                }
            ]
        };

        var combined = SpendFold.Combine([older, newer]);

        Assert.Equal(2, combined.Days.Count);
        Assert.Equal(DateTime.Today.AddDays(-9), combined.Days[0].Date);
        Assert.Equal(3m, combined.Days.Sum(day => day.Usd));
    }

    /// <summary>Одинаковые дни складываются, а корзины SKU внутри них — тоже.</summary>
    [Fact]
    public void The_same_day_from_two_keys_adds_up()
    {
        var day = DateTime.Today;
        var left = new SpendHistoryFile
        {
            Days =
            [
                new SpendDay
                {
                    Date = day,
                    Usd = 1m,
                    Skus = [new SpendSkuBucket { Sku = "grok-4-6", Usd = 1m, Requests = 2 }]
                }
            ]
        };

        var right = new SpendHistoryFile
        {
            Days =
            [
                new SpendDay
                {
                    Date = day,
                    Usd = 3m,
                    Skus = [new SpendSkuBucket { Sku = "grok-4-6", Usd = 3m, Requests = 1 }]
                }
            ]
        };

        var combined = SpendFold.Combine([left, right]);

        var only = Assert.Single(combined.Days);
        Assert.Equal(4m, only.Usd);
        var bucket = Assert.Single(only.Skus);
        Assert.Equal(4m, bucket.Usd);
        Assert.Equal(3, bucket.Requests);
    }

    /// <summary>Сеть в этих проверках не нужна: у OpenRouter журнала списаний нет вовсе.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
    }
}
