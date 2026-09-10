using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// "Summarize video → Create infographic": subtitles, then a one-shot text call to boil them
/// down, then one image call. Deliberately outside the tool loop — the model is never offered
/// a tool and never decides anything here; the three steps run in a fixed order and the answer
/// is a single picture.
/// </summary>
internal sealed partial class ChatEngine
{
    /// <summary>Portrait — an infographic is read top to bottom.</summary>
    private const string InfographicRatio = "3:4";

    private const string BriefSystemPrompt = """
        You turn a video transcript into a prompt for an image model that will draw an infographic.

        Output ONLY the image prompt, in English, as one paragraph. No preamble, no markdown.

        The prompt must describe a single portrait infographic poster and must spell out, verbatim,
        every word that should appear inside the picture:
        - a short title naming the video's subject
        - 4 to 6 labelled blocks, each a key point in at most 6 words
        - any concrete figures, dates or names worth showing
        - a one-line takeaway at the bottom

        Also state the visual treatment: flat vector style, clean sans-serif lettering, a limited
        palette, simple icons, generous spacing, no photographic elements. Keep the whole prompt
        under 250 words. Write the infographic's own text in the language the transcript is in.
        """;

    /// <summary>
    /// Runs the whole chain and appends one assistant message holding the finished picture.
    /// Progress is reported through the ordinary turn observer, so the UI needs no new plumbing.
    /// </summary>
    public async Task RunVideoInfographicAsync(
        ChatSession session,
        string videoUrl,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        var now = DateTime.Now;
        var user = new ChatDisplayMessage
        {
            Role = "user",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            Text = $"Инфографика по видео: {videoUrl}"
        };
        session.Messages.Add(user);
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text(user.Text) });
        session.UpdatedAt = now;
        observer.OnUserAppended(user);

        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.Now,
            RequestedModelId = VeniceClient.DefaultImageModel,
            ResolvedModelId = VeniceClient.DefaultImageModel,
            Status = AssistantStatus.Streaming
        };
        session.Messages.Add(assistant);
        observer.OnAssistantStarted(assistant);

        var clock = System.Diagnostics.Stopwatch.StartNew();

        void Progress(string line)
        {
            assistant.Text = line;
            assistant.Duration = clock.Elapsed;
            observer.OnAssistantText(assistant);
        }

        try
        {
            Progress("Получаю субтитры…");
            var transcript = await YouTubeTranscriptTool
                .FetchTranscriptAsync(videoUrl, language: null, cancellationToken)
                .ConfigureAwait(false);

            if (!transcript.Success)
            {
                Fail(session, assistant, observer, clock, transcript.Error ?? "Не удалось получить расшифровку.");
                return;
            }

            Progress("Выделяю главное…");
            var imagePrompt = await BuildInfographicPromptAsync(transcript.Text, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(imagePrompt))
            {
                Fail(session, assistant, observer, clock, "Не удалось собрать описание инфографики по расшифровке.");
                return;
            }

            Progress("Рисую инфографику…");
            var base64 = await _venice
                .GenerateImageAsync(
                    imagePrompt,
                    model: VeniceClient.DefaultImageModel,
                    aspectRatio: InfographicRatio,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var image = new ImageAttachment(base64, "image/png", "Инфографика");
            var handle = ChatImageRegistry.Register(image);

            // The body is nothing but the picture — the user asked for an infographic, not a
            // retelling, and the transcript is already available through the other button.
            assistant.Text = $"![Инфографика]({handle})";
            assistant.Images = [image with { Label = handle }];
            assistant.Duration = clock.Elapsed;
            assistant.Cost = _venice.RequestCost.HasData ? _venice.RequestCost : assistant.Cost;
            assistant.Status = AssistantStatus.Complete;

            session.ApiMessages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = ChatContent.Text("(инфографика по видео отправлена пользователю)")
            });
            session.UpdatedAt = DateTime.Now;

            clock.Stop();
            observer.OnAssistantCompleted(assistant);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            assistant.Text = string.IsNullOrWhiteSpace(assistant.Text) ? "Отменено." : assistant.Text;
            assistant.Duration = clock.Elapsed;
            assistant.Status = AssistantStatus.Cancelled;
            session.UpdatedAt = DateTime.Now;
            observer.OnAssistantCancelled(assistant);
        }
        catch (Exception ex)
        {
            Fail(session, assistant, observer, clock, ex.Message);
        }
    }

    private static void Fail(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        System.Diagnostics.Stopwatch clock,
        string message)
    {
        clock.Stop();
        assistant.Text = message;
        assistant.Duration = clock.Elapsed;
        assistant.Status = AssistantStatus.Error;
        session.UpdatedAt = DateTime.Now;
        observer.OnAssistantCompleted(assistant);
    }

    /// <summary>
    /// One-shot call to a text model to compress the transcript into an image prompt. Uses the
    /// chat's own model but goes through <see cref="VeniceClient.CreateChatCompletionAsync"/>
    /// directly with no tools, so nothing here touches the tool loop or the session's history.
    /// </summary>
    private async Task<string> BuildInfographicPromptAsync(string transcript, CancellationToken cancellationToken)
    {
        var response = await _venice.CreateChatCompletionAsync(
                _venice.ActiveModel,
                [
                    new ChatMessage { Role = "system", Content = ChatContent.Text(BriefSystemPrompt) },
                    new ChatMessage { Role = "user", Content = ChatContent.Text(transcript) }
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

        return ReasoningSplit.Split(
            ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;
    }
}
