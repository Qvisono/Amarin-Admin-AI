using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// «Пересказ видео → Инфографика»: субтитры, один текстовый запрос, чтобы их ужать, и один запрос
/// рисования. Намеренно вне цикла инструментов: модели не предлагают инструментов, решать ей
/// нечего, три шага идут по порядку, а ответ — одна картинка.
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
    /// Проходит всю цепочку и дописывает одно сообщение ассистента с готовой картинкой. Ход
    /// сообщается обычным наблюдателем хода — интерфейсу ничего нового не нужно.
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
            Text = Loc.Format("S.Infographic.Request", videoUrl)
        };
        session.Messages.Add(user);
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text(user.Text) });
        session.UpdatedAt = now;
        observer.OnUserAppended(user);

        // Свой контекст, как у обычного хода: инфографика тоже тратит деньги, а брифовый запрос
        // прежде уходил на модель, которую оставил в клиенте предыдущий ход чата. «Авто» тут
        // разворачивается в настоящую модель: в отличие от обычного хода маршрутизатора здесь
        // нет, и «auto» уходила в запрос как есть — Venice отвечала на неё 404.
        var briefModel = ResolveForSingleShot(ReadSelectedModel(session));
        var briefKey = KeyFor(briefModel, ReadSelectedKey(session));
        var turn = new VeniceTurnContext
        {
            RequestedModelId = briefModel,
            ModelId = briefModel,
            Credential = briefKey,
            Reasoning = ReasoningChoice.Disabled
        };
        using var turnScope = VeniceTurnScope.Push(turn);

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
            Progress(Loc.Get("S.Infographic.Subtitles"));
            var transcript = await YouTubeTranscriptTool
                .FetchTranscriptAsync(videoUrl, language: null, cancellationToken)
                .ConfigureAwait(false);

            if (!transcript.Success)
            {
                Fail(session, assistant, observer, clock, transcript.Error ?? Loc.Get("S.Infographic.NoTranscript"));
                return;
            }

            Progress(Loc.Get("S.Infographic.Brief"));
            var imagePrompt = await BuildInfographicPromptAsync(
                    briefModel, briefKey, transcript.Text, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(imagePrompt))
            {
                Fail(session, assistant, observer, clock, Loc.Get("S.Infographic.NoBrief"));
                return;
            }

            Progress(Loc.Get("S.Infographic.Drawing"));
            var base64 = await _venice
                .GenerateImageAsync(
                    imagePrompt,
                    model: VeniceClient.DefaultImageModel,
                    aspectRatio: InfographicRatio,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var image = new ImageAttachment(base64, "image/png", Loc.Get("S.Infographic.Alt"));
            var handle = ChatImageRegistry.Register(image);

            // В ответе только картинка: просили инфографику, а не пересказ, а субтитры доступны
            // другой кнопкой.
            assistant.Text = $"![{Loc.Get("S.Infographic.Alt")}]({handle})";
            assistant.Images = [image with { Label = handle }];
            assistant.Duration = clock.Elapsed;
            assistant.Cost = turn.Total.HasData ? turn.Total : assistant.Cost;
            assistant.Status = AssistantStatus.Complete;

            session.ApiMessages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = ChatContent.Text("(the infographic of the video was sent to the user)")
            });
            session.UpdatedAt = DateTime.Now;

            clock.Stop();
            observer.OnAssistantCompleted(assistant);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            assistant.Text = string.IsNullOrWhiteSpace(assistant.Text) ? Loc.Get("S.Infographic.Cancelled") : assistant.Text;
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
    /// Один запрос к текстовой модели: ужать субтитры в запрос рисования. Модель — та же, что у
    /// чата, но вызов идёт напрямую через <see cref="VeniceClient.CreateChatCompletionAsync"/> без
    /// инструментов и не трогает ни цикл инструментов, ни историю сессии.
    /// </summary>
    private async Task<string> BuildInfographicPromptAsync(
        string model,
        ApiCredential credential,
        string transcript,
        CancellationToken cancellationToken)
    {
        // Только бриф: сама картинка уходит в статью картинок своим sku модели.
        using var charge = VeniceClient.ChargeAs(VeniceSku.Infographic);
        var response = await _venice.CreateChatCompletionAsync(
                model,
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
                ReasoningChoice.Disabled,
                credential)
            .ConfigureAwait(false);

        return ReasoningSplit.Split(
            ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;
    }
}
