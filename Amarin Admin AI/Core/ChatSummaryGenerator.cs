using System.Text;

namespace Amarin.Core;

/// <summary>Дописанная сводка и то, во что обошлось её сочинение.</summary>
/// <remarks>
/// Цена возвращается наружу по той же причине, что и у заголовка: платил за неё человек, и она
/// обязана попасть в разбивку счёта. Сводка могла и не получиться — деньги от этого не вернутся.
/// </remarks>
internal sealed record ChatSummaryDraft(string? Summary, VeniceCost? Cost);

/// <summary>Чат, найденный поиском по смыслу, и цена всего поиска.</summary>
internal sealed record ChatSearchResult(IReadOnlyList<string> ChatIds, VeniceCost? Cost);

/// <summary>
/// Пишет сводку переписки и ищет по сводкам — и то и другое моделью «быстрая».
/// </summary>
/// <remarks>
/// Устроен как <see cref="ChatTitleGenerator"/> и по тем же причинам: свой клиент, потому что
/// поле модели у общего одно на всех, и <see cref="VeniceTurnScope.Suppress"/>, потому что эти
/// деньги не принадлежат открытому ходу.
/// </remarks>
internal sealed class ChatSummaryGenerator
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly Func<AppSettings> _settings;

    public ChatSummaryGenerator(HttpClient http, AgentOptions options, Func<AppSettings> settings)
    {
        _http = http;
        _options = options;
        _settings = settings;
    }

    /// <summary>
    /// Дописывает сводку по новой части переписки. <paramref name="previous"/> пустая — сводка
    /// собирается с нуля по последним сообщениям.
    /// </summary>
    public async Task<ChatSummaryDraft> UpdateAsync(
        string? previous,
        string exchange,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(exchange))
        {
            return new ChatSummaryDraft(null, null);
        }

        var language = ChatSummary.LanguageName();
        var answer = await AskAsync(
                ChatSummary.SystemPrompt(language),
                ChatSummary.UserPrompt(previous, exchange, language),
                cancellationToken)
            .ConfigureAwait(false);

        return new ChatSummaryDraft(ChatSummary.Sanitize(answer.Text), answer.Cost);
    }

    /// <summary>
    /// Ищет среди сводок то, о чём спросили. Чаты без сводки в запрос не попадают — искать
    /// в них нечего.
    /// </summary>
    public async Task<ChatSearchResult> SearchAsync(
        string query,
        IReadOnlyList<(string ChatId, string Summary)> chats,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chats);

        if (string.IsNullOrWhiteSpace(query) || chats.Count == 0)
        {
            return new ChatSearchResult([], null);
        }

        var summaries = chats.Select(item => item.Summary).ToList();
        var answer = await AskAsync(
                ChatSummary.SearchSystemPrompt(),
                ChatSummary.SearchUserPrompt(query, summaries),
                cancellationToken)
            .ConfigureAwait(false);

        var numbers = ChatSummary.ParseSearchAnswer(answer.Text, chats.Count);
        return new ChatSearchResult(
            [.. numbers.Select(number => chats[number - 1].ChatId)],
            answer.Cost);
    }

    /// <summary>
    /// Склеивает пару «вопрос — ответ» в текст для модели. Инструменты и картинки опускаются:
    /// сводка пересказывает разговор, а что делалось с компьютером, показывает журнал.
    /// </summary>
    public static string BuildExchange(IEnumerable<ChatDisplayMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var sb = new StringBuilder();
        foreach (var message in messages)
        {
            var text = message.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var who = message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                ? "Assistant"
                : "User";
            sb.Append(who).Append(": ").Append(text).Append('\n');
        }

        return sb.ToString().Trim();
    }

    private async Task<(string? Text, VeniceCost? Cost)> AskAsync(
        string system,
        string user,
        CancellationToken cancellationToken)
    {
        // Сводка считается рядом с ходом, но платит за себя сама и стоит в разбивке своей
        // строкой. Без этого её цена попала бы и в счёт хода, и была бы прибавлена ещё раз.
        using var isolated = VeniceTurnScope.Suppress();

        var settings = _settings();
        var model = ChatSummary.ResolveModel(settings, _options.Model);
        var options = new AgentOptions
        {
            ApiKey = _options.ApiKey,
            Keys = _options.Keys,
            SpendSink = _options.SpendSink,
            BaseUrl = _options.BaseUrl,
            Model = model,
            EnableWebCitations = false,
            EnableXSearch = false,
            WebSearch = "off"
        };

        var venice = new VeniceClient(_http, options);
        try
        {
            var response = await venice.CreateChatCompletionAsync(
                    model,
                    [
                        new ChatMessage { Role = "system", Content = ChatContent.Text(system) },
                        new ChatMessage { Role = "user", Content = ChatContent.Text(user) }
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
                    (settings.AgentFastReasoning ?? new ReasoningSettings()).ToChoice())
                .ConfigureAwait(false);

            // Рассуждающая модель иначе положила бы ход своих мыслей прямо в сводку.
            var reply = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;
            return (reply, response.Cost?.ToCost());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Сводка — удобство, а не работа, за которой пришёл человек: её провал не повод
            // ни ронять ход, ни показывать ошибку.
            return (null, null);
        }
    }
}
