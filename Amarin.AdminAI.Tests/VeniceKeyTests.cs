using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ключ предъявляется на каждом запросе, а не висит на общем клиенте.
/// </summary>
/// <remarks>
/// Раньше конструктор <see cref="VeniceClient"/> ставил заголовок на
/// <c>DefaultRequestHeaders</c> через <c>??=</c>, и выходило две беды: клиент нёс чужой
/// <c>Bearer</c> в любой запрос, куда бы тот ни шёл, и первый ключ запоминался навсегда —
/// сменить его у живого клиента было нельзя. Глазами ни того, ни другого не видно.
/// </remarks>
public sealed class VeniceKeyTests
{
    private const string ModelsBody =
        """{"object":"list","type":"text","data":[{"id":"grok-4-6","object":"model","type":"text"}]}""";

    private static (VeniceClient Client, HttpClient Http, Recorder Recorder) Build(AgentOptions options)
    {
        var recorder = new Recorder();
        var http = new HttpClient(recorder) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        return (new VeniceClient(http, options), http, recorder);
    }

    [Fact]
    public async Task Every_request_carries_the_key()
    {
        var (client, _, recorder) = Build(new AgentOptions { ApiKey = "key-one-12345678" });

        await client.ListTextModelsAsync();

        Assert.Equal("Bearer key-one-12345678", Assert.Single(recorder.Authorizations));
    }

    [Fact]
    public async Task The_shared_client_is_left_unbranded()
    {
        var (client, http, _) = Build(new AgentOptions { ApiKey = "key-one-12345678" });

        await client.ListTextModelsAsync();

        // Тот самый заголовок, из-за которого запрос к GitHub уезжал с ключом Venice.
        Assert.Null(http.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task A_swapped_key_reaches_a_client_that_already_sent_requests()
    {
        var keys = new ApiKeyProvider("key-one-12345678");
        var (client, _, recorder) = Build(new AgentOptions { ApiKey = "ignored-fallback", Keys = keys });

        await client.ListTextModelsAsync();
        keys.Use("key-two-87654321");
        await client.ListTextModelsAsync();

        Assert.Equal(
            ["Bearer key-one-12345678", "Bearer key-two-87654321"],
            recorder.Authorizations);
    }

    [Fact]
    public async Task A_missing_key_sends_no_header_at_all()
    {
        var (client, _, recorder) = Build(new AgentOptions { ApiKey = "" });

        await client.ListTextModelsAsync();

        // Пустой «Bearer » Venice отвергает невнятной ошибкой; лучше не предъявлять ничего.
        Assert.Null(Assert.Single(recorder.Authorizations));
    }

    [Fact]
    public void Options_copied_for_the_agent_follow_the_key()
    {
        var keys = new ApiKeyProvider("key-one-12345678");
        var source = new AgentOptions { ApiKey = "ignored-fallback", Keys = keys };
        var copy = new AgentOptions { ApiKey = source.ApiKey, Keys = source.Keys };

        keys.Use("key-two-87654321");

        Assert.Equal("key-two-87654321", source.ApiKey);
        Assert.Equal("key-two-87654321", copy.ApiKey);
    }

    [Fact]
    public void Without_a_provider_the_plain_key_still_works()
    {
        Assert.Equal("key-one-12345678", new AgentOptions { ApiKey = "key-one-12345678" }.ApiKey);
    }

    [Fact]
    public void The_provider_stays_quiet_when_the_key_does_not_change()
    {
        var keys = new ApiKeyProvider("key-one-12345678");
        var changes = 0;
        keys.Changed += _ => changes++;

        keys.Use("key-one-12345678");
        Assert.Equal(0, changes);

        keys.Use("key-two-87654321");
        Assert.Equal(1, changes);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<string?> Authorizations { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ModelsBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
