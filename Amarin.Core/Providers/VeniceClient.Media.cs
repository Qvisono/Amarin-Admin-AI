using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Amarin.Core;

public sealed partial class VeniceClient
{
    public async Task<string> ScrapeUrlAsync(string url, CancellationToken cancellationToken = default)
    {
        var credential = RequireVenice(VeniceCredential(), "Чтение страниц");
        await GuardSpendAsync(credential, cancellationToken).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ScrapeUrlRequest { Url = url },
            VeniceJsonContext.Default.ScrapeUrlRequest);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = Request(HttpMethod.Post, "augment/scrape", credential);
        httpRequest.Content = content;
        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        UpdateBalanceFromHeaders(response, credential);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice scrape error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var markdown = root.TryGetProperty("content", out var contentProp)
            ? contentProp.GetString()
            : null;

        AddCost(new VeniceCost { Usd = 0.01m, HasData = true }, VeniceSku.Scrape, credential);
        return string.IsNullOrWhiteSpace(markdown) ? "Страница пуста или контент не извлечён." : markdown;
    }

    /// <summary>
    /// Распознавание речи в облаке (D14): OpenAI-совместимый <c>POST audio/transcriptions</c>.
    /// Звук — WAV, формой multipart или JSON-телом, как провайдер описывает его основным способом
    /// (<see cref="ProviderSpec.TranscribesJson"/>).
    /// </summary>
    /// <remarks>
    /// Цена записывается, только если провайдер назвал её в ответе (<c>usage.cost</c> или
    /// <c>cost</c>): придумывать тариф за провайдера нельзя, а без цифры деньги лучше не показать,
    /// чем показать неверные.
    /// </remarks>
    public async Task<string> TranscribeAsync(
        byte[] wav,
        string modelId,
        string? language,
        ApiCredential credential,
        CancellationToken cancellationToken = default)
    {
        await GuardSpendAsync(credential, cancellationToken).ConfigureAwait(false);
        using var content = TranscriptionBody(ProviderSpec.For(ModelRef.Of(modelId)), wav, ModelRef.Bare(modelId), language);
        using var httpRequest = Request(HttpMethod.Post, "audio/transcriptions", credential);
        httpRequest.Content = content;
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        UpdateBalanceFromHeaders(response, credential);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(body));
        }

        var (text, cost) = ParseTranscription(body);
        if (cost is { } usd)
        {
            AddCost(new VeniceCost { Usd = usd, HasData = true }, VeniceSku.Transcription, credential);
        }

        return text;
    }

    /// <summary>
    /// Озвучка текста моделью провайдера (1.37.0): OpenAI-совместимый <c>POST audio/speech</c>,
    /// как его принимают Venice и OpenRouter. В ответ приходит сам звук, MP3.
    /// </summary>
    /// <remarks>
    /// Цену ответ не называет, поэтому она считается по цене знака из каталога
    /// (<see cref="AudioModelCatalog"/>) и помечается оценкой — как у прямых провайдеров. Каталог
    /// цену не назвал — траты не записываются, а не выдумываются.
    /// </remarks>
    /// <param name="speed">Скорость речи (1 — обычная); null — не передавать, не все модели её берут.</param>
    public async Task<SpeechAudio> SpeakAsync(
        string text,
        string modelId,
        string? voice,
        double? speed,
        decimal? usdPerChar,
        ApiCredential credential,
        CancellationToken cancellationToken = default)
    {
        await GuardSpendAsync(credential, cancellationToken).ConfigureAwait(false);
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["model"] = ModelRef.Bare(modelId),
            ["input"] = text,
            ["response_format"] = "mp3"
        };
        if (!string.IsNullOrWhiteSpace(voice))
        {
            body["voice"] = voice.Trim();
        }

        if (speed is { } rate && Math.Abs(rate - 1) > 0.01)
        {
            body["speed"] = Math.Round(rate, 2);
        }

        using var content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var httpRequest = Request(HttpMethod.Post, "audio/speech", credential);
        httpRequest.Content = content;
        using var response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        UpdateBalanceFromHeaders(response, credential);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw ApiError(credential, (int)response.StatusCode, ExtractErrorMessage(error));
        }

        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (usdPerChar is { } price && price > 0)
        {
            AddCost(new VeniceCost { Usd = price * text.Length, HasData = true, Estimated = true }, VeniceSku.Speech, credential);
        }

        return new SpeechAudio(audio, response.Content.Headers.ContentType?.ToString() ?? "audio/mpeg");
    }

    /// <summary>Тело запроса распознавания по тому, как его ждёт провайдер.</summary>
    /// <remarks>
    /// Форму собирает не <c>MultipartFormDataContent.Add(…, имя, файл)</c>: он пишет имена без
    /// кавычек, добавляет <c>filename*</c>, которого RFC 7578 в форме не допускает, и берёт
    /// границу в кавычки — часть серверов такую форму не разбирает. Здесь — как у curl.
    /// </remarks>
    internal static HttpContent TranscriptionBody(ProviderSpec spec, byte[] wav, string model, string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? null : language.Trim();
        if (spec.TranscribesJson)
        {
            var json = new JsonObject
            {
                ["model"] = model,
                ["input_audio"] = new JsonObject { ["data"] = Convert.ToBase64String(wav), ["format"] = "wav" },
                ["response_format"] = "json"
            };
            if (lang is not null)
            {
                json["language"] = lang;
            }

            return new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        }

        var boundary = "amarin" + Guid.NewGuid().ToString("N");
        var form = new MultipartFormDataContent(boundary);
        form.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = "\"speech.wav\"" };
        form.Add(file);
        Field("model", model);
        Field("response_format", "json");
        if (lang is not null)
        {
            Field("language", lang);
        }

        return form;

        void Field(string name, string value)
        {
            var part = new StringContent(value);
            part.Headers.ContentType = null;
            part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
            form.Add(part);
        }
    }

    /// <summary>Текст и цена из ответа распознавания; незнакомый ответ — пустой текст.</summary>
    internal static (string Text, decimal? Cost) ParseTranscription(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var text = root.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? ""
                : "";
            decimal? cost = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("cost", out var usageCost) && usageCost.TryGetDecimal(out var fromUsage))
            {
                cost = fromUsage;
            }
            else if (root.TryGetProperty("cost", out var plain) && plain.ValueKind == JsonValueKind.Number &&
                     plain.TryGetDecimal(out var fromRoot))
            {
                cost = fromRoot;
            }

            return (text, cost);
        }
        catch (JsonException)
        {
            return ("", null);
        }
    }

    /// <summary>
    /// Рисующая модель по умолчанию — «nano banana» от Google через Venice. Дороже диффузионной,
    /// но только она пишет на картинке читаемый текст, а для схем и инфографики в этом весь смысл.
    /// </summary>
    public const string DefaultImageModel = "nano-banana-pro";

    /// <summary>True для линейки на Gemini: её размер задаётся соотношением сторон, а не пикселями.</summary>
    private static bool UsesAspectRatio(string model) =>
        model.StartsWith("nano-banana", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Картинка стоит куда дороже текста вокруг, и без этой цены шапка сообщения показывала только
    /// токены чата — сотые цента за ход, стоивший центы. Берётся, лишь когда Venice не назвал ни
    /// цены, ни пригодного изменения остатка.
    /// </summary>
    private const decimal FallbackImageUsd = 0.10m;

    /// <summary>
    /// Рисует одну картинку и возвращает её base64 PNG. Venice берёт плату за картинку, и списание
    /// входит в <see cref="RequestCost"/> — так оно попадает в шапку сообщения.
    /// </summary>
    /// <param name="aspectRatio">Соотношение сторон, «3:4» для вертикальной инфографики. Модели с размером в пикселях его не учитывают.</param>
    public async Task<string> GenerateImageAsync(
        string prompt,
        int width = 1024,
        int height = 1024,
        string? model = null,
        string? aspectRatio = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        var credential = RequireVenice(VeniceCredential(), "Рисование картинок");
        await GuardSpendAsync(credential, cancellationToken).ConfigureAwait(false);
        var resolved = string.IsNullOrWhiteSpace(model) ? DefaultImageModel : model;
        var byRatio = UsesAspectRatio(resolved);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ImageGenerateRequest
            {
                Model = resolved,
                Prompt = prompt,
                Width = byRatio ? null : width,
                Height = byRatio ? null : height,
                AspectRatio = byRatio ? (aspectRatio ?? RatioFor(width, height)) : null,
                Resolution = byRatio ? "2K" : null
            },
            VeniceJsonContext.Default.ImageGenerateRequest);

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        using var httpRequest = Request(HttpMethod.Post, "image/generate", credential);
        httpRequest.Content = content;

        var balanceBefore = LastBalance?.Usd;

        using var response = await _http.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        UpdateBalanceFromHeaders(response, credential);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new VeniceApiException(
                $"Venice image error ({(int)response.StatusCode}): {ExtractErrorMessage(body)}");
        }

        var result = JsonSerializer.Deserialize(body, VeniceJsonContext.Default.ImageGenerateResponse);
        if (result?.Images.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)) is not { } image)
        {
            throw new VeniceApiException(Loc.Get("S.Provider.NoImage"));
        }

        AddCost(
            PriceImage(result.Cost, balanceBefore, LastBalance?.Usd),
            resolved + "-image",
            credential);
        return image;
    }

    /// <summary>
    /// Цена картинки, лучший источник первым: число в теле ответа Venice, иначе изменение остатка
    /// за этот вызов, иначе постоянная прикидка. Изменению остатка верим, только если оно правдоподобно:
    /// пополнение или параллельный запрос дали бы в шапке сообщения дикое число.
    /// </summary>
    private static VeniceCost PriceImage(VeniceCostResponse? reported, decimal? before, decimal? after)
    {
        var cost = reported?.ToCost();
        if (cost is { HasData: true })
        {
            return cost;
        }

        if (before is { } start && after is { } end)
        {
            var spent = start - end;
            if (spent > 0m && spent <= 1m)
            {
                return new VeniceCost { Usd = spent, HasData = true };
            }
        }

        return new VeniceCost { Usd = FallbackImageUsd, HasData = true };
    }

    /// <summary>Переводит желаемый размер в пикселях в ближайшее соотношение, которое берут модели «по соотношению».</summary>
    private static string RatioFor(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return "1:1";
        }

        var ratio = width / (double)height;
        return ratio switch
        {
            < 0.72 => "3:4",
            < 0.95 => "4:5",
            < 1.06 => "1:1",
            < 1.4 => "5:4",
            _ => "4:3"
        };
    }
}
