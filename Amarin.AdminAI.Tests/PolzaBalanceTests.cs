using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class PolzaBalanceTests
{
    private static ApiKeyEntry Key(string id) => new(id, id, "polza-" + id, ApiKeySource.Stored, false, LlmProvider.Polza);
    private static VeniceBalance Usd(decimal amount) => new() { CanConsume = true, Usd = amount };

    [Fact]
    public void Available_rubles_are_converted_without_adding_reserved_amounts()
    {
        var data = ProviderBalances.FromPolza("""{"amount":"1400","available":"1250.50","reservedAmount":"149.50"}""", 100m);
        Assert.Equal(12.505m, data.Balances?.Usd);
        Assert.Equal(100m, data.RublesPerUsd);
        Assert.Null(ProviderBalances.FromPolza("""{"available":1000}""", null).Balances?.Usd);
        Assert.Null(ProviderBalances.FromPolza("""{"available":1000}""", 0m).Balances?.Usd);
    }

    [Fact]
    public void An_account_balance_is_counted_once_but_every_key_remains_in_the_breakdown()
    {
        var keys = new[] { Key("first"), Key("default") };
        var book = new BalanceBook(() => 100m, _ => keys[1].Credential);
        book.Remember(keys[0].Credential, Usd(5m));
        book.Remember(keys[1].Credential, Usd(7m));

        Assert.Equal(7m, book.Total(keys).Usd);
        Assert.Equal(2, book.Breakdown(keys).Count);
        var first = new BalanceBook(() => 100m);
        first.Remember(keys[0].Credential, Usd(5m));
        first.Remember(keys[1].Credential, Usd(7m));
        Assert.Equal(5m, first.Total(keys).Usd);
    }

    [Fact]
    public void A_rate_change_discards_saved_estimates_and_missing_rate_does_not_become_a_silent_provider()
    {
        decimal? rate = null;
        var keys = new[] { Key("first") };
        var book = new BalanceBook(() => rate);
        Assert.Empty(book.Stale(keys));
        Assert.Equal(0, book.Total(keys).Unknown);
        rate = 100m;
        Assert.Single(book.Stale(keys));
        book.Remember(keys[0].Credential, Usd(5m));
        Assert.Empty(book.Stale(keys));
        var saved = book.Snapshot();
        rate = 110m;
        Assert.Single(book.Stale(keys));
        Assert.Null(book.Total(keys).Usd);
        Assert.Empty(book.Breakdown(keys));
        var restored = new BalanceBook(() => rate);
        restored.Restore(saved);
        Assert.Single(restored.Stale(keys));
        book.Invalidate(keys, LlmProvider.Polza);
        Assert.Empty(book.Snapshot());
    }

    [Fact]
    public async Task The_balance_endpoint_uses_v2_and_the_request_key_and_skips_the_network_without_a_rate()
    {
        decimal? rate = null;
        var handler = new BalanceHandler();
        using var http = new HttpClient(handler);
        var seen = new List<VeniceBalance>();
        var client = new VeniceClient(http, new AgentOptions
        {
            RublesPerUsd = () => rate,
            BalanceSink = (_, balance) => seen.Add(balance)
        });
        var credential = Key("test").Credential;
        Assert.Null((await client.GetRateLimitsAsync(credential)).Balances?.Usd);
        Assert.Empty(handler.Requests);
        rate = 100m;
        Assert.Equal(12.5m, (await client.GetRateLimitsAsync(credential)).Balances?.Usd);
        Assert.Equal(100m, Assert.Single(seen).RublesPerUsd);
        Assert.Equal(["https://polza.ai/api/v2/balance|Bearer polza-test"], handler.Requests);
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    private sealed class BalanceHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string Json { get; init; } = """{"available":"1250","amount":"1400"}""";
        public Action? BeforeResponse { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri + "|" + request.Headers.Authorization);
            BeforeResponse?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Json, Encoding.UTF8, "application/json")
            });
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"available\":\"unknown\"}")]
    [InlineData("not-json")]
    public async Task An_invalid_balance_response_is_retryable_and_does_not_record_a_zero(string json)
    {
        var seen = new List<VeniceBalance>();
        using var http = new HttpClient(new BalanceHandler { Json = json });
        var client = new VeniceClient(http, new AgentOptions
        {
            RublesPerUsd = () => 100m,
            BalanceSink = (_, balance) => seen.Add(balance)
        });
        var error = await Assert.ThrowsAsync<VeniceApiException>(() => client.GetRateLimitsAsync(Key("test").Credential));
        Assert.IsNotType<BalanceNotReportedException>(error);
        Assert.Empty(seen);
    }

    [Fact]
    public async Task A_balance_response_from_the_previous_rate_is_not_recorded()
    {
        decimal? rate = 100m;
        var seen = new List<VeniceBalance>();
        using var http = new HttpClient(new BalanceHandler { BeforeResponse = () => rate = 110m });
        var client = new VeniceClient(http, new AgentOptions
        {
            RublesPerUsd = () => rate,
            BalanceSink = (_, balance) => seen.Add(balance)
        });
        await Assert.ThrowsAsync<VeniceApiException>(() => client.GetRateLimitsAsync(Key("test").Credential));
        Assert.Empty(seen);
    }

    [Fact]
    public void A_source_rate_is_preserved_even_if_it_changes_just_before_the_balance_is_remembered()
    {
        var keys = new[] { Key("test") };
        var book = new BalanceBook(() => 110m);
        book.Remember(keys[0].Credential, new VeniceBalance { Usd = 12.5m, RublesPerUsd = 100m });

        Assert.Equal(100m, Assert.Single(book.Snapshot()).RublesPerUsd);
        Assert.Null(book.Total(keys).Usd);
        Assert.Empty(book.Breakdown(keys));
        Assert.Single(book.Stale(keys));
    }
}
