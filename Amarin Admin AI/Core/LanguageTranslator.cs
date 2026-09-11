using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Ход перевода: сколько партий готово из скольких.</summary>
/// <param name="Done">Готовых партий.</param>
/// <param name="Total">Всего партий.</param>
public sealed record TranslationProgress(int Done, int Total);

/// <summary>Что вышло: код языка и его строки, либо понятная причина отказа.</summary>
internal sealed record TranslationResult(
    bool Success,
    string? Error,
    IReadOnlyDictionary<string, string> Strings);

/// <summary>
/// Переводит строки интерфейса моделью — это и есть кнопка «new language».
/// </summary>
/// <remarks>
/// Каталог уходит партиями, а не целиком: сбой стоит одной партии, а не всего перевода, и
/// каждый ответ остаётся обозримым JSON-объектом, который модель дописывает до конца. Ответ
/// разбирается через <see cref="ToolArguments"/>: модель заворачивает JSON в ```` ```json ````,
/// дописывает пояснения после закрывающей скобки и путает кавычки — прямой
/// <c>JsonDocument.Parse</c> здесь ломается.
/// </remarks>
internal sealed class LanguageTranslator
{
    /// <summary>
    /// Модель перевода. Задана пользователем явно; если Venice её не примет, отказ будет с
    /// понятным текстом, а поправить надо ровно эту строку.
    /// </summary>
    public const string TranslationModelId = "openai-gpt-56-luna-pro";

    /// <summary>
    /// Ключей в одной партии. Сорок — компромисс: ответ не упирается в предел длины, а партий
    /// на трёхстах строках выходит меньше десятка.
    /// </summary>
    internal const int BatchSize = 40;

    private const string SystemPrompt = """
        You translate user-interface strings.

        You are given a JSON object: keys are string identifiers, values are Russian UI text.
        Return ONE JSON object and nothing else — no prose, no markdown fences, no comments.

        Rules:
        - Keep every key exactly as given. Never add, drop or rename a key.
        - Translate only the values, into the requested language.
        - Keep placeholders like {0} and {1} exactly as they are, same count, same numbers.
        - Keep line breaks that are already in the value.
        - Keep product names as they are: Venice, GitHub, JSON, PDF, YouTube, Amarin Admin AI.
        - UI text is short. Prefer the wording a native speaker would see in an app menu.
        """;

    private readonly VeniceClient _venice;

    public LanguageTranslator(VeniceClient venice) => _venice = venice;

    /// <summary>
    /// Переводит <paramref name="source"/> на язык <paramref name="languageName"/>.
    /// </summary>
    /// <param name="progress">Зовётся после каждой партии, на потоке вызывающего.</param>
    public async Task<TranslationResult> TranslateAsync(
        IReadOnlyDictionary<string, string> source,
        string languageName,
        Action<TranslationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return new TranslationResult(false, Loc.Get("S.Language.NeedName"), new Dictionary<string, string>());
        }

        // Цена перевода — не цена хода чата: счёт открытого разговора трогать нельзя.
        using var isolated = VeniceTurnScope.Suppress();

        var batches = Split(source).ToList();
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < batches.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string reply;
            try
            {
                reply = await AskAsync(batches[i], languageName, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (VeniceApiException ex)
            {
                return new TranslationResult(
                    false,
                    Loc.Format("S.Language.ModelRefused", TranslationModelId, ex.Message),
                    translated);
            }
            catch (HttpRequestException)
            {
                return new TranslationResult(false, Loc.Get("S.Language.NoNetwork"), translated);
            }

            foreach (var (key, value) in Accept(batches[i], reply))
            {
                translated[key] = value;
            }

            progress?.Invoke(new TranslationProgress(i + 1, batches.Count));
        }

        if (translated.Count == 0)
        {
            return new TranslationResult(false, Loc.Get("S.Language.NothingTranslated"), translated);
        }

        return new TranslationResult(true, null, translated);
    }

    internal static IEnumerable<Dictionary<string, string>> Split(
        IReadOnlyDictionary<string, string> source)
    {
        var batch = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            batch[key] = value;
            if (batch.Count == BatchSize)
            {
                yield return batch;
                batch = new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Отбирает из ответа модели то, чему можно верить.
    /// </summary>
    /// <remarks>
    /// Лишние ключи выбрасываются, недостающие просто не попадают в перевод — у них останется
    /// русский. Расхождение по подстановкам <c>{0}</c> отбраковывает ключ целиком: это уже не
    /// «звучит криво», а исключение при форматировании прямо в интерфейсе.
    /// </remarks>
    internal static Dictionary<string, string> Accept(
        IReadOnlyDictionary<string, string> asked,
        string reply)
    {
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);

        JsonElement json;
        try
        {
            json = ToolArguments.Parse(reply);
        }
        catch (JsonException)
        {
            // Ответ не похож на JSON вовсе — партия пропадает, у её ключей останется русский.
            return accepted;
        }

        if (json.ValueKind != JsonValueKind.Object)
        {
            return accepted;
        }

        foreach (var (key, original) in asked)
        {
            if (!json.TryGetProperty(key, out var element) ||
                element.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value) || !SamePlaceholders(original, value))
            {
                continue;
            }

            accepted[key] = value;
        }

        return accepted;
    }

    internal static bool SamePlaceholders(string original, string translated)
    {
        var expected = Placeholders(original);
        var actual = Placeholders(translated);
        return expected.Count == actual.Count && !expected.Except(actual).Any();
    }

    private static HashSet<string> Placeholders(string value)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < value.Length - 2; i++)
        {
            if (value[i] != '{' || !char.IsDigit(value[i + 1]))
            {
                continue;
            }

            var end = value.IndexOf('}', i);
            if (end > i)
            {
                found.Add(value[i..(end + 1)]);
            }
        }

        return found;
    }

    private async Task<string> AskAsync(
        IReadOnlyDictionary<string, string> batch,
        string languageName,
        CancellationToken cancellationToken)
    {
        var payload = new StringBuilder()
            .Append("Target language: ").AppendLine(languageName)
            .AppendLine("Translate the values of this object:")
            .Append(JsonSerializer.Serialize(batch, AppJson.Options))
            .ToString();

        var response = await _venice.CreateChatCompletionAsync(
                TranslationModelId,
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
                ReasoningChoice.Disabled)
            .ConfigureAwait(false);

        var text = ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "";
        return ReasoningSplit.Split(text).Answer;
    }

    /// <summary>
    /// Код языка для имени файла. Модель называет язык словом, а хранится он как <c>de.json</c>.
    /// </summary>
    internal static string CodeFor(string languageName)
    {
        var trimmed = (languageName ?? "").Trim().ToLowerInvariant();
        var known = trimmed switch
        {
            "english" or "английский" => "en",
            "deutsch" or "german" or "немецкий" => "de",
            "français" or "francais" or "french" or "французский" => "fr",
            "español" or "espanol" or "spanish" or "испанский" => "es",
            "italiano" or "italian" or "итальянский" => "it",
            "português" or "portugues" or "portuguese" or "португальский" => "pt",
            "polski" or "polish" or "польский" => "pl",
            "українська" or "ukrainian" or "украинский" => "uk",
            "türkçe" or "turkce" or "turkish" or "турецкий" => "tr",
            "中文" or "chinese" or "китайский" => "zh",
            "日本語" or "japanese" or "японский" => "ja",
            "한국어" or "korean" or "корейский" => "ko",
            _ => ""
        };

        if (known.Length > 0)
        {
            return known;
        }

        // Незнакомый язык: латиница из названия, иначе устойчивый суффикс — лишь бы имя файла
        // было допустимым и одинаковым при повторном переводе того же языка.
        var letters = new string([.. trimmed.Where(char.IsAsciiLetter)]);
        return letters.Length >= 2
            ? letters[..Math.Min(8, letters.Length)]
            : "lang" + Math.Abs(trimmed.GetHashCode(StringComparison.Ordinal)) % 10000;
    }
}
