using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Переводит заметки к выпуску моделью для окна «Что нового» (1.37.0). Заметки пишутся
/// по-английски, а читать их удобнее на языке интерфейса.
/// </summary>
/// <remarks>
/// <para>
/// Переводятся куски разобранных заметок (<see cref="ReleaseNotesOutline"/>), а не файл целиком:
/// разделы остаются теми же по числу и порядку, и колонка разделов не зависит от того, как модель
/// обошлась с заголовками. Ответ принимает <see cref="LanguageTranslator.Accept"/> — тот же
/// терпимый разбор JSON и та же замена тире, что у перевода интерфейса.
/// </para>
/// <para>
/// Заметки делятся по объёму на ровные части, не больше <see cref="MaxParts"/>, и все части
/// уходят разом (1.37.1): время перевода — время самой длинной части. До 1.37.1 партии были по
/// 3000 знаков без предела числа, и неровные: последняя часто была крошечной, а первые — у
/// предела, так что ждать приходилось их.
/// </para>
/// </remarks>
internal static class ReleaseNotesTranslator
{
    /// <summary>Сколько запросов переводят заметки одновременно, самое большее.</summary>
    internal const int MaxParts = 4;

    /// <summary>
    /// Сколько знаков исходника стоит отдельного запроса: короче — не делится, у каждого запроса
    /// своё ожидание ответа и свой системный промпт.
    /// </summary>
    internal const int PartChars = 1500;

    internal const string LeadKey = "lead";

    internal const string SystemPrompt = """
        You translate the release notes of a Windows desktop app.

        You are given a JSON object: keys are identifiers, values are parts of the notes in English
        Markdown. Return ONE JSON object and nothing else - no prose, no markdown fences, no comments.

        Rules:
        - Keep every key exactly as given. Never add, drop or rename a key.
        - Translate only the values, into the requested language.
        - Keep the Markdown as it is: list markers, line breaks, bold, links and the text inside
          backticks.
        - Keep keyboard shortcuts, file names, commands, product names and model names as they are.
        - Write plain, natural text with the meaning of the original and nothing added. No filler,
          no marketing tone, no explanations the original does not have.
        - Never use the long dash or the en dash. Where a dash is natural, use a plain hyphen with
          spaces around it, or rephrase the sentence.
        """;

    /// <summary>
    /// Переводит заметки на язык <paramref name="languageName"/>. <c>null</c> — не перевелось
    /// ничего: сеть, отказ провайдера или ответ, в котором нечему верить. Непереведённые куски
    /// удавшегося перевода остаются английскими.
    /// </summary>
    public static async Task<ReleaseNotesOutline?> TranslateAsync(
        VeniceClient client,
        string modelId,
        ApiCredential? credential,
        ReleaseNotesOutline outline,
        string languageName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(outline);

        // Цена перевода — не цена хода чата: счёт открытого разговора трогать нельзя.
        using var isolated = VeniceTurnScope.Suppress();

        var parts = Parts(ToStrings(outline));
        var replies = await Task.WhenAll(parts.Select(part =>
                TranslatePartAsync(client, modelId, credential, part, languageName, cancellationToken)))
            .ConfigureAwait(false);

        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var reply in replies)
        {
            foreach (var (key, value) in reply)
            {
                translated[key] = value;
            }
        }

        return translated.Count == 0 ? null : FromStrings(outline, translated);
    }

    /// <summary>Куски заметок под ключами: <c>lead</c>, <c>s0.title</c>, <c>s0.body</c>, …</summary>
    internal static Dictionary<string, string> ToStrings(ReleaseNotesOutline outline)
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (outline.Lead.Length > 0)
        {
            strings[LeadKey] = outline.Lead;
        }

        for (var i = 0; i < outline.Sections.Count; i++)
        {
            strings[TitleKey(i)] = outline.Sections[i].Title;
            if (outline.Sections[i].Body.Length > 0)
            {
                strings[BodyKey(i)] = outline.Sections[i].Body;
            }
        }

        return strings;
    }

    /// <summary>Те же заметки с переведёнными кусками; чего нет в переводе — остаётся как было.</summary>
    internal static ReleaseNotesOutline FromStrings(
        ReleaseNotesOutline outline,
        IReadOnlyDictionary<string, string> translated)
    {
        var sections = outline.Sections
            .Select((section, i) => new ReleaseNotesSection(
                translated.GetValueOrDefault(TitleKey(i), section.Title),
                translated.GetValueOrDefault(BodyKey(i), section.Body)))
            .ToList();
        return new ReleaseNotesOutline(translated.GetValueOrDefault(LeadKey, outline.Lead), sections);
    }

    /// <summary>
    /// Части по порядку заметок: их число растёт с объёмом (по части на <see cref="PartChars"/>
    /// знаков, не больше <see cref="MaxParts"/>), и каждая набирается до своей доли объёма. Раздел
    /// не делится: заголовок и тело переводятся вместе, иначе модель назвала бы одно и то же
    /// разными словами.
    /// </summary>
    internal static List<Dictionary<string, string>> Parts(IReadOnlyDictionary<string, string> strings)
    {
        var units = strings
            .GroupBy(pair => UnitOf(pair.Key))
            .Select(unit => (Pairs: unit.ToList(), Length: unit.Sum(pair => pair.Value.Length)))
            .ToList();
        var total = units.Sum(unit => unit.Length);
        var count = Math.Clamp((total + PartChars - 1) / PartChars, 1, Math.Min(MaxParts, Math.Max(1, units.Count)));
        var share = (double)total / count;

        var parts = new List<Dictionary<string, string>>();
        var part = new Dictionary<string, string>(StringComparer.Ordinal);
        var size = 0;
        foreach (var (pairs, length) in units)
        {
            // Раздел уходит в следующую часть, если больше его половины не влезло бы в долю этой:
            // так части выходят ровными, а не «полная, полная и остаток».
            if (part.Count > 0 && parts.Count < count - 1 && size + length / 2.0 > share)
            {
                parts.Add(part);
                part = new Dictionary<string, string>(StringComparer.Ordinal);
                size = 0;
            }

            foreach (var (key, value) in pairs)
            {
                part[key] = value;
            }

            size += length;
        }

        if (part.Count > 0)
        {
            parts.Add(part);
        }

        return parts;
    }

    private static string TitleKey(int index) => "s" + index.ToString(CultureInfo.InvariantCulture) + ".title";

    private static string BodyKey(int index) => "s" + index.ToString(CultureInfo.InvariantCulture) + ".body";

    private static string UnitOf(string key) => key.Split('.')[0];

    private static async Task<Dictionary<string, string>> TranslatePartAsync(
        VeniceClient client,
        string modelId,
        ApiCredential? credential,
        Dictionary<string, string> part,
        string languageName,
        CancellationToken cancellationToken)
    {
        var payload = new StringBuilder()
            .Append("Target language: ").AppendLine(languageName)
            .AppendLine("Translate the values of this object:")
            .Append(JsonSerializer.Serialize(part, AppJson.Options))
            .ToString();

        using var charge = VeniceClient.ChargeAs(VeniceSku.Translate);
        try
        {
            var response = await client.CreateChatCompletionAsync(
                    modelId,
                    [
                        new ChatMessage { Role = "system", Content = ChatContent.Text(SystemPrompt) },
                        new ChatMessage { Role = "user", Content = ChatContent.Text(payload) }
                    ],
                    tools: null,
                    toolChoice: null,
                    new VeniceParameters
                    {
                        IncludeVeniceSystemPrompt = false,
                        EnableWebSearch = "off",
                        EnableXSearch = false,
                        StripThinkingResponse = true
                    },
                    cancellationToken,
                    ReasoningChoice.Disabled,
                    credential)
                .ConfigureAwait(false);

            var text = ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "";
            return LanguageTranslator.Accept(part, ReasoningSplit.Split(text).Answer);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Ловится целиком: перевод заметок — удобство, и отказ провайдера, обрыв сети или
            // тайм-аут клиента значат лишь, что эта часть останется английской.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
