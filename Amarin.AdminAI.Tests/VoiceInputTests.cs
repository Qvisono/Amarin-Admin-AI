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

    /// <summary>Точки у кнопки записи: громкость каждой доли куска своя (1.37.0-beta.7).</summary>
    [Fact]
    public void Each_part_of_a_chunk_has_its_own_level()
    {
        var chunk = new byte[1000];
        for (var i = 200; i < 300; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(chunk.AsSpan(i * 2), (short)(i % 2 == 0 ? 20000 : -20000));
        }

        var levels = WavFile.Levels(chunk, 5);

        Assert.Equal([0, 0, 1, 0, 0], levels);
        Assert.Equal(new double[5], WavFile.Levels([], 5));
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
        public string ContentType = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            ContentType = request.Content!.Headers.ContentType?.ToString() ?? "";
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"recognised words"}""") };
        }
    }

    /// <summary>
    /// OpenRouter получает звук JSON-телом (<c>input_audio</c>) — так его основным способом
    /// описывает сам OpenRouter (1.37.0-beta.7); ключ и адрес — провайдера модели.
    /// </summary>
    [Fact]
    public async Task OpenRouter_gets_the_audio_as_a_json_body()
    {
        var capture = new Capture();
        var options = new AgentOptions { ApiKey = "venice-secret", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        var client = new VeniceClient(new HttpClient(capture), options);
        var wav = WavFile.Build(new byte[3200]);

        var text = await client.TranscribeAsync(wav, "openrouter:some/stt-model", "ru", new ApiCredential(LlmProvider.OpenRouter, "or-secret"));

        Assert.Equal("recognised words", text);
        Assert.EndsWith("/audio/transcriptions", capture.Request!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("openrouter", capture.Request.RequestUri.Host, StringComparison.Ordinal);
        Assert.Equal("or-secret", capture.Request.Headers.Authorization!.Parameter);
        using var body = System.Text.Json.JsonDocument.Parse(capture.Body);
        Assert.Equal("some/stt-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("ru", body.RootElement.GetProperty("language").GetString());
        Assert.Equal("wav", body.RootElement.GetProperty("input_audio").GetProperty("format").GetString());
        Assert.Equal(Convert.ToBase64String(wav), body.RootElement.GetProperty("input_audio").GetProperty("data").GetString());
    }

    /// <summary>
    /// Venice — форма multipart, как её пишет curl: имена в кавычках, без <c>filename*</c> и с
    /// границей без кавычек. Форму .NET «как есть» часть серверов не разбирала.
    /// </summary>
    [Fact]
    public async Task Venice_gets_a_plain_multipart_form()
    {
        var capture = new Capture();
        var options = new AgentOptions { ApiKey = "venice-secret", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        var client = new VeniceClient(new HttpClient(capture), options);

        await client.TranscribeAsync(WavFile.Build(new byte[3200]), "openai/whisper-large-v3", "ru", new ApiCredential(LlmProvider.Venice, "venice-secret"));

        Assert.Contains("venice", capture.Request!.RequestUri!.Host, StringComparison.Ordinal);
        var type = capture.ContentType;
        Assert.StartsWith("multipart/form-data; boundary=", type, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", type, StringComparison.Ordinal);
        Assert.Contains("name=\"file\"; filename=\"speech.wav\"", capture.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"model\"", capture.Body, StringComparison.Ordinal);
        Assert.Contains("name=\"language\"", capture.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("filename*", capture.Body, StringComparison.Ordinal);
        Assert.Contains("openai/whisper-large-v3", capture.Body, StringComparison.Ordinal);
    }
}

/// <summary>
/// Есть ли чем распознать речь — по нему микрофон у поля ввода показывается или прячется.
/// До правки кнопка была всегда, и без распознавателя человек узнавал об этом только после
/// записи.
/// </summary>
public sealed class VoiceAvailabilityTests
{
    private static readonly System.Globalization.CultureInfo Russian = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    private static bool Available(VoiceEngine engine, string? model, bool local, bool key) =>
        Amarin.UI.VoiceAvailability.IsAvailable(
            new AppSettings { VoiceEngine = engine, VoiceModel = model },
            Russian,
            _ => local,
            _ => key);

    [Fact]
    public void Without_a_chosen_model_a_key_is_enough_for_the_default_whisper()
    {
        // Русского распознавателя в Windows нет: без модели по умолчанию микрофон не появлялся вовсе.
        Assert.True(Available(VoiceEngine.Auto, null, local: false, key: true));
        Assert.True(Available(VoiceEngine.Cloud, "  ", local: false, key: true));
        Assert.False(Available(VoiceEngine.Auto, null, local: false, key: false));
    }

    [Fact]
    public void The_default_cloud_model_follows_the_key_venice_first()
    {
        var settings = new AppSettings();

        Assert.Equal("openai/whisper-large-v3", Amarin.UI.VoiceAvailability.CloudModel(settings, _ => true));
        Assert.Equal(
            "openrouter:openai/whisper-large-v3",
            Amarin.UI.VoiceAvailability.CloudModel(settings, model => model.StartsWith("openrouter:", StringComparison.Ordinal)));
        Assert.Null(Amarin.UI.VoiceAvailability.CloudModel(settings, _ => false));
        Assert.Equal("stt-xai-v1", Amarin.UI.VoiceAvailability.CloudModel(new AppSettings { VoiceModel = " stt-xai-v1 " }, _ => false));
    }

    [Fact]
    public void The_route_names_why_there_is_nothing_to_recognise_with()
    {
        static Amarin.UI.VoiceRoute Route(VoiceEngine engine, bool local, bool key) =>
            Amarin.UI.VoiceAvailability.Route(new AppSettings { VoiceEngine = engine }, Russian, _ => local, _ => key);

        Assert.Equal(Amarin.UI.VoiceRoute.NoLocal, Route(VoiceEngine.Local, local: false, key: true));
        Assert.Equal(Amarin.UI.VoiceRoute.NoKey, Route(VoiceEngine.Auto, local: false, key: false));
        Assert.Equal(Amarin.UI.VoiceRoute.Cloud, Route(VoiceEngine.Auto, local: false, key: true));
        Assert.Equal(Amarin.UI.VoiceRoute.Local, Route(VoiceEngine.Auto, local: true, key: true));
    }

    [Fact]
    public void A_windows_recogniser_is_enough_unless_the_cloud_is_chosen()
    {
        Assert.True(Available(VoiceEngine.Auto, null, local: true, key: false));
        Assert.True(Available(VoiceEngine.Local, null, local: true, key: false));
        Assert.False(Available(VoiceEngine.Cloud, null, local: true, key: false));
    }

    [Fact]
    public void A_cloud_model_needs_a_key_and_is_ignored_on_this_pc_only()
    {
        Assert.True(Available(VoiceEngine.Cloud, "whisper", local: false, key: true));
        Assert.False(Available(VoiceEngine.Cloud, "whisper", local: false, key: false));
        Assert.False(Available(VoiceEngine.Local, "whisper", local: false, key: true));
    }

    [Fact]
    public void The_speech_language_falls_back_to_the_interface()
    {
        Assert.Equal("en", Amarin.UI.VoiceAvailability.SpeechCulture(new AppSettings(), "en").Name);
        Assert.Equal("ru-RU", Amarin.UI.VoiceAvailability.SpeechCulture(new AppSettings { VoiceLanguage = " ru-RU " }, "en").Name);
    }
}
