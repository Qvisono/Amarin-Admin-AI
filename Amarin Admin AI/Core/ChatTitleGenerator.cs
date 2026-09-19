namespace Amarin.Core;

/// <summary>Придуманный заголовок и то, во что обошлось его сочинение.</summary>
/// <remarks>
/// Цена возвращается наружу, потому что платил за неё человек, а в программе этих денег до сих
/// пор не было видно нигде. Заголовок мог и не получиться — цена от этого никуда не девается.
/// </remarks>
internal sealed record ChatTitleDraft(string? Title, VeniceCost? Cost);

internal sealed class ChatTitleGenerator
{
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly Func<AppSettings> _settings;

    public ChatTitleGenerator(HttpClient http, AgentOptions options, Func<AppSettings> settings)
    {
        _http = http;
        _options = options;
        _settings = settings;
    }

    public async Task<ChatTitleDraft> GenerateAsync(string userText, CancellationToken cancellationToken = default)
    {
        var text = userText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ChatTitleDraft(null, null);
        }

        // Заголовок считается рядом с ходом, но платит за себя сам, и в разбивке стоит своей
        // строкой. Сегодня контекст хода сюда не протекает — OnUserAppended срабатывает до
        // VeniceTurnScope.Push, — но гарантия эта висит на порядке двух строк в чужом методе:
        // протечёт, и цена заголовка окажется и в счёте хода, и прибавленной ещё раз.
        using var isolated = VeniceTurnScope.Suppress();
        using var charge = VeniceClient.ChargeAs(VeniceSku.ChatTitle);

        var settings = _settings();
        var model = ChatTitle.ResolveModel(settings, _options.Model);
        var language = ChatTitle.LanguageName();
        var options = new AgentOptions
        {
            ApiKey = _options.ApiKey,
            Keys = _options.Keys,

            // Ключ слота, а не выбранный: заголовки чатов человек мог отдать бесплатной
            // модели другого провайдера, пока разговор идёт у своего.
            Binding = _options.Keys?.CredentialFor(model, ModelSlots.ReadKey(settings, ModelSlot.Title)),
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
                        new ChatMessage { Role = "system", Content = ChatContent.Text(ChatTitle.SystemPrompt(language)) },
                        new ChatMessage { Role = "user", Content = ChatContent.Text(ChatTitle.UserPrompt(text, language)) }
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
                    (_settings().TitleReasoning ?? new ReasoningSettings()).ToChoice())
                .ConfigureAwait(false);

            // A GLM-class model titles the chat by thinking out loud first, and the tags would
            // end up in the sidebar.
            var reply = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;
            return new ChatTitleDraft(ChatTitle.Sanitize(reply), response.Cost?.ToCost());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new ChatTitleDraft(null, null);
        }
    }
}
