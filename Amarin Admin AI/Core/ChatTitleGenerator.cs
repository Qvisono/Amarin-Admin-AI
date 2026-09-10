namespace Amarin.Core;

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

    public async Task<string?> GenerateAsync(string userText, CancellationToken cancellationToken = default)
    {
        var text = userText.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var settings = _settings();
        var model = ChatTitle.ResolveModel(settings, _options.Model);
        var options = new AgentOptions
        {
            ApiKey = _options.ApiKey,
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
                        new ChatMessage { Role = "system", Content = ChatContent.Text(ChatTitle.SystemPrompt) },
                        new ChatMessage { Role = "user", Content = ChatContent.Text(ChatTitle.UserPrompt(text)) }
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
            return ChatTitle.Sanitize(reply);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
