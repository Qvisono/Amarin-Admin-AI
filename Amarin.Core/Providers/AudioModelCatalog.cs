using System.Globalization;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Что делает аудиомодель: озвучивает текст или распознаёт речь.</summary>
public enum AudioKind
{
    Speech,
    Transcription
}

/// <summary>
/// Аудиомодель провайдера. <see cref="Id"/> — с приставкой провайдера, как у моделей чата
/// (<c>openrouter:…</c>), у Venice — голый.
/// </summary>
/// <param name="Voices">Голоса модели озвучки; первый — тот, что звучит, когда голос не выбран.</param>
/// <param name="UsdPerChar">Цена знака озвучки из каталога; null — каталог её не назвал.</param>
public sealed record AudioModel(string Id, string Name, LlmProvider Provider, IReadOnlyList<string> Voices, decimal? UsdPerChar);

/// <summary>
/// Каталоги моделей озвучки и распознавания Venice и OpenRouter — для выбора в настройках голоса.
/// </summary>
/// <remarks>
/// <para>
/// Оба каталога открыты: Venice отдаёт их по <c>models?type=tts|asr</c>, OpenRouter — по
/// <c>models?output_modalities=speech|transcription</c> (в обычный список моделей они не входят).
/// Поэтому клиент свой, без ключа, как у <see cref="ModelPriceBook"/>: ключу туда ехать незачем.
/// </para>
/// <para>
/// Запоминается только удачная загрузка, как у <see cref="VeniceModelListCache"/>: отказ сети не
/// должен оставлять пустой список до перезапуска.
/// </para>
/// </remarks>
public sealed class AudioModelCatalog(HttpClient http)
{
    /// <summary>Провайдеры с аудиомоделями, в порядке показа.</summary>
    public static readonly IReadOnlyList<LlmProvider> Providers = [LlmProvider.Venice, LlmProvider.OpenRouter];

    private readonly Dictionary<(AudioKind, LlmProvider), IReadOnlyList<AudioModel>> _loaded = [];
    private readonly Lock _gate = new();

    /// <summary>Уже загруженный список или null.</summary>
    public IReadOnlyList<AudioModel>? Cached(AudioKind kind, LlmProvider provider)
    {
        lock (_gate)
        {
            return _loaded.TryGetValue((kind, provider), out var models) ? models : null;
        }
    }

    /// <summary>Модель из загруженных списков по полному идентификатору.</summary>
    public AudioModel? Find(AudioKind kind, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        lock (_gate)
        {
            return _loaded.Where(entry => entry.Key.Item1 == kind)
                .SelectMany(entry => entry.Value)
                .FirstOrDefault(model => string.Equals(model.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Список провайдера; отказ сети — пустой список, без запоминания.</summary>
    public async Task<IReadOnlyList<AudioModel>> LoadAsync(AudioKind kind, LlmProvider provider, CancellationToken cancellationToken)
    {
        if (Cached(kind, provider) is { } cached)
        {
            return cached;
        }

        var spec = ProviderSpec.For(provider);
        var query = provider == LlmProvider.OpenRouter
            ? "/models?output_modalities=" + (kind == AudioKind.Speech ? "speech" : "transcription")
            : "/models?type=" + (kind == AudioKind.Speech ? "tts" : "asr");
        try
        {
            using var response = await http.GetAsync(new Uri(spec.BaseUrl + query), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var models = Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), provider);
            if (models.Count > 0)
            {
                lock (_gate)
                {
                    _loaded[(kind, provider)] = models;
                }
            }

            return models;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Ответ каталога: у Venice имя и голоса — в <c>model_spec</c>, у OpenRouter — в самой записи.</summary>
    internal static List<AudioModel> Parse(string body, LlmProvider provider)
    {
        var models = new List<AudioModel>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return models;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (Text(item, "id") is not { Length: > 0 } id)
                {
                    continue;
                }

                var venice = provider == LlmProvider.Venice;
                var spec = item.TryGetProperty("model_spec", out var found) && found.ValueKind == JsonValueKind.Object ? found : default;
                var name = (venice ? Text(spec, "name") : Text(item, "name")) ?? id;
                var voices = Strings(venice ? spec : item, venice ? "voices" : "supported_voices");
                models.Add(new AudioModel(ModelRef.Qualify(provider, id), Shorten(name), provider, voices, Price(item, spec, venice)));
            }
        }
        catch (JsonException)
        {
            models.Clear();
        }

        return models;
    }

    /// <summary>
    /// Цена знака: у Venice — долларов за миллион знаков (<c>pricing.input.usd</c>), у OpenRouter —
    /// за знак (<c>pricing.prompt</c>).
    /// </summary>
    private static decimal? Price(JsonElement item, JsonElement spec, bool venice)
    {
        if (venice)
        {
            return spec.ValueKind == JsonValueKind.Object &&
                   spec.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object &&
                   pricing.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object &&
                   input.TryGetProperty("usd", out var usd) && usd.TryGetDecimal(out var perMillion)
                ? perMillion / 1_000_000m
                : null;
        }

        return item.TryGetProperty("pricing", out var price) && price.ValueKind == JsonValueKind.Object &&
               Text(price, "prompt") is { } prompt &&
               decimal.TryParse(prompt, NumberStyles.Float, CultureInfo.InvariantCulture, out var perChar)
            ? perChar
            : null;
    }

    /// <summary>«Mistral: Voxtral TTS 3» — без имени производителя впереди: провайдер и так подписан.</summary>
    private static string Shorten(string name)
    {
        var colon = name.IndexOf(": ", StringComparison.Ordinal);
        return colon > 0 && colon < name.Length - 2 ? name[(colon + 2)..] : name;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> Strings(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0)]
            : [];
}

/// <summary>Звук озвучки и его тип из ответа (<c>audio/mpeg</c>, <c>audio/wav</c>, <c>audio/pcm;rate=…</c>).</summary>
public sealed record SpeechAudio(byte[] Data, string MediaType);
