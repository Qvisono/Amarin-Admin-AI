using System.Buffers.Binary;
using System.Net;
using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Голосовой ввод (D14): WAV, громкость, запрос распознавания и разбор ответа.</summary>
public sealed class VoiceInputTests
{
    [Fact]
    public void The_wav_header_describes_16_khz_mono_16_bit()
    {
        var pcm = new byte[3200];
        var wav = WavFile.Build(pcm);

        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(16_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(pcm.Length, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(TimeSpan.FromMilliseconds(100), WavFile.Duration(pcm.Length));
    }

    [Fact]
    public void Silence_is_zero_and_a_loud_signal_fills_the_meter()
    {
        var loud = new byte[200];
        for (var i = 0; i < 100; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(loud.AsSpan(i * 2), (short)(i % 2 == 0 ? 20000 : -20000));
        }

        Assert.Equal(0, WavFile.Level(new byte[200]));
        Assert.Equal(1, WavFile.Level(loud));
    }

    [Theory]
    [InlineData("""{"text":" hello there "}""", "hello there", null)]
    [InlineData("""{"text":"hi","usage":{"cost":0.0012}}""", "hi", "0.0012")]
    [InlineData("""{"text":"hi","cost":0.5}""", "hi", "0.5")]
    [InlineData("""not json""", "", null)]
    public void The_answer_gives_text_and_a_price_only_if_named(string body, string text, string? cost)
    {
        var (parsed, price) = VeniceClient.ParseTranscription(body);

        Assert.Equal(text, parsed);
        Assert.Equal(cost is null ? null : decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture), price);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string Body = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"recognised words"}""") };
        }
    }

    [Fact]
    public async Task The_request_is_an_openai_style_multipart_form_to_the_models_provider()
    {
        var capture = new Capture();
        var options = new AgentOptions { ApiKey = "venice-secret", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        var client = new VeniceClient(new HttpClient(capture), options);

        var text = await client.TranscribeAsync(
            WavFile.Build(new byte[3200]),
            "openrouter:some/stt-model",
            "ru",
            new ApiCredential(LlmProvider.OpenRouter, "or-secret"));

        Assert.Equal("recognised words", text);
        Assert.EndsWith("/audio/transcriptions", capture.Request!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("openrouter", capture.Request.RequestUri.Host, StringComparison.Ordinal);
        Assert.Equal("or-secret", capture.Request.Headers.Authorization!.Parameter);
        Assert.Matches("name=\"?model\"?", capture.Body);
        Assert.Contains("some/stt-model", capture.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("openrouter:some", capture.Body, StringComparison.Ordinal);
        Assert.Matches("name=\"?language\"?", capture.Body);
        Assert.Matches("filename=\"?speech\\.wav\"?", capture.Body);
    }
}
