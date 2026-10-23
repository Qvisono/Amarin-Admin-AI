using System.Net;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Каталоги аудиомоделей Venice и OpenRouter разбираются в модели с голосами и ценой знака, а
/// озвучка уходит OpenAI-совместимым запросом к провайдеру модели и записывает свою цену.
/// </summary>
public sealed class AudioModelsTests
{
    private const string VeniceSpeech = """
        {"data":[{"id":"tts-kokoro","type":"tts","model_spec":{"name":"Kokoro Text to Speech",
          "pricing":{"input":{"usd":3.5,"diem":3.5}},"voices":["af_alloy","am_adam"]}}]}
        """;

    private const string OpenRouterSpeech = """
        {"data":[{"id":"elevenlabs/eleven-multilingual-v2","name":"ElevenLabs: Eleven Multilingual v2",
          "pricing":{"prompt":"0.00008","completion":"0"},"supported_voices":["george","sarah"]},
          {"id":"fish-audio/s1","name":"Fish Audio: S1","pricing":{"prompt":"0.000015"},"supported_voices":null}]}
        """;

    [Fact]
    public void A_venice_catalog_gives_bare_ids_voices_and_a_price_per_char()
    {
        var model = Assert.Single(AudioModelCatalog.Parse(VeniceSpeech, LlmProvider.Venice));

        Assert.Equal("tts-kokoro", model.Id);
        Assert.Equal("Kokoro Text to Speech", model.Name);
        Assert.Equal(["af_alloy", "am_adam"], model.Voices);
        Assert.Equal(3.5m / 1_000_000m, model.UsdPerChar);
    }

    [Fact]
    public void An_openrouter_catalog_gives_prefixed_ids_and_short_names()
    {
        var models = AudioModelCatalog.Parse(OpenRouterSpeech, LlmProvider.OpenRouter);

        Assert.Equal("openrouter:elevenlabs/eleven-multilingual-v2", models[0].Id);
        Assert.Equal("Eleven Multilingual v2", models[0].Name);
        Assert.Equal(["george", "sarah"], models[0].Voices);
        Assert.Equal(0.00008m, models[0].UsdPerChar);
        Assert.Empty(models[1].Voices);
        Assert.Equal(LlmProvider.OpenRouter, ModelRef.Of(models[0].Id));
    }

    [Fact]
    public void A_broken_catalog_is_an_empty_list() =>
        Assert.Empty(AudioModelCatalog.Parse("<html>", LlmProvider.Venice));

    private sealed class Speaker : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string Body = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            return response;
        }
    }

    [Fact]
    public async Task Speech_is_an_openai_style_request_to_the_models_provider_and_its_price_is_recorded()
    {
        var speaker = new Speaker();
        var spent = new List<(string Sku, VeniceCost Cost)>();
        var options = new AgentOptions
        {
            ApiKey = "venice-secret",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            SpendSink = (_, cost, sku) => spent.Add((sku, cost))
        };
        var client = new VeniceClient(new HttpClient(speaker), options);

        var audio = await client.SpeakAsync(
            "Привет",
            "openrouter:elevenlabs/eleven-multilingual-v2",
            "george",
            1.25,
            0.0001m,
            new ApiCredential(LlmProvider.OpenRouter, "or-secret"));

        Assert.Equal([1, 2, 3], audio.Data);
        Assert.Equal("audio/mpeg", audio.MediaType);
        Assert.EndsWith("/audio/speech", speaker.Request!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("openrouter", speaker.Request.RequestUri.Host, StringComparison.Ordinal);
        Assert.Equal("or-secret", speaker.Request.Headers.Authorization!.Parameter);
        using var body = JsonDocument.Parse(speaker.Body);
        Assert.Equal("elevenlabs/eleven-multilingual-v2", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Привет", body.RootElement.GetProperty("input").GetString());
        Assert.Equal("george", body.RootElement.GetProperty("voice").GetString());
        Assert.Equal("mp3", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(1.25, body.RootElement.GetProperty("speed").GetDouble());
        var (sku, cost) = Assert.Single(spent);
        Assert.Equal(VeniceSku.Speech, sku);
        Assert.Equal(0.0006m, cost.Usd);
        Assert.True(cost.Estimated);
    }

    [Fact]
    public void Long_text_is_cut_on_sentence_boundaries_within_the_limit()
    {
        var text = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"Предложение номер {i} о голосе."));

        var chunks = SpeechText.Chunks(text, 200);

        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 200, chunk));
        Assert.All(chunks, chunk => Assert.EndsWith(".", chunk, StringComparison.Ordinal));
        Assert.Equal(text, string.Join(" ", chunks));
        Assert.Single(SpeechText.Chunks("Коротко.", 200));
    }
}
