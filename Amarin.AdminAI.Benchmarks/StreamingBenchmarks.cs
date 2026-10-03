using System.Net;
using System.Net.Http.Headers;
using Amarin.Core;
using BenchmarkDotNet.Attributes;

namespace Amarin.AdminAI.Benchmarks;

/// <summary>
/// Поток ответа: разбор SSE, накопление текста и отдача накопленного после каждого кусочка — как
/// это делает ход чата. Сеть подставная: тело отдаётся из памяти, меряется только программа.
/// </summary>
public class StreamingBenchmarks
{
    private const string Model = "openrouter:openai/gpt-5";

    private VeniceClient? _client;
    private IReadOnlyList<ChatMessage> _messages = [];
    private int _seen;

    private VeniceClient Client => _client ?? throw new InvalidOperationException("GlobalSetup не выполнялся.");

    /// <summary>Кусочков в ответе: 600 — короткий ответ, 4000 — длинный (около 40 КБ текста).</summary>
    [Params(600, 4000)]
    public int Chunks { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var body = Fixtures.Sse(Chunks);
        var options = new AgentOptions
        {
            Keys = new ApiKeyProvider("sk-or-v1-benchmark", LlmProvider.OpenRouter),
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6"
        };

        _client = new VeniceClient(new HttpClient(new Replay(body)), options);
        _messages = [new ChatMessage { Role = "user", Content = ChatContent.Text("Почему не ставится обновление?") }];
    }

    [Benchmark]
    public async Task<int> StreamAnswer()
    {
        var streamed = await Client.StreamChatCompletionAsync(
            Model,
            Model,
            _messages,
            tools: null,
            toolChoice: null,
            new VeniceParameters(),
            onText: text => _seen = text.Length).ConfigureAwait(false);

        return streamed.Text.Length + _seen;
    }

    /// <summary>Отдаёт одно и то же тело на каждый запрос.</summary>
    private sealed class Replay(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
