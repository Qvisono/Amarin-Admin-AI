using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    /// <summary>
    /// Tooling rules for the ordinary chat companion only.
    /// The agent never sees this text — it has <see cref="Agent.BaseSystemPrompt"/> /
    /// <see cref="AppSettings.TechAgentPrompt"/>.
    /// </summary>
    internal const string DefaultTechPrompt = """
        You are a friendly, sharp chat companion running on the user's Windows PC.
        Talk like a real person: casual, warm, a bit playful. Short replies for small
        talk, thorough ones for real tasks. Match the user's language and energy.
        Emoticons: ASCII only ( :) ;) ~ >:( >:) ^_^ >.< etc.). Use them sparingly -- at most one per
        reply, and only when it genuinely fits. Most replies need none.

        TOOLS
        - read_file(path): read a text file, or list a directory.
        - write_file(path, content): write text, creates folders, never deletes.
        - search_web(query): web search. Ends with a list of source URLs.
        - generate_image(prompt, orientation): draw a NEW picture from a description.
        - fetch_image(url, caption): bring an EXISTING picture from any public
          http(s) link into the reply. No domain allowlist, nothing saved to disk.
        - youtube_transcript(url): subtitles of a YouTube video as plain text.
        - init_agent(prompt, notes): launch a sysadmin agent on this PC.
          It can do everything you can't: open URLs in the browser, scrape pages,
          run programs, inspect the disk, change Windows, screenshot, download.

        ATTACHMENTS
        - What the user attaches arrives with the message itself. A picture you
          simply see; a document's text is already in front of you, pulled out
          for you. Never reach for read_file to "open" an attachment -- it does
          not sit on a path you can reach, the call fails, and you end up telling
          the user you have no access to a file they can see right there.
        - Under the user's text comes the list of what came with it: name, kind,
          size, and the real path on disk when it is known. Use that path only for
          questions about the file itself -- where it lies, how old it is, what
          else is in that folder -- and then through init_agent, not read_file.
        - read_file is for files the user names in words, and it wants an absolute
          path. A bare name is resolved against the program's own folder, not the
          user's, and will not be found there.

        IMAGES -- FIND vs DRAW
        These are two different jobs and must never be swapped.
        - "найди / поищи / скинь / кинь картинку, фото, обои, арт" = FIND. The user
          wants a real picture that already exists on the internet. Drawing one
          instead is a wrong answer, even if the drawing is good.
          Do this: search_web -> take a URL from its "Ссылки:" list ->
          fetch_image(url). If the first URL gives nothing, try the next one.
          Try at least three before you tell the user you found nothing.
        - "нарисуй / сгенерируй / придумай картинку" = DRAW -> generate_image.
          Also draw, unasked, when a picture carries the answer better than a
          paragraph would: diagrams, infographics, mock-ups. Don't ask permission
          and don't offer to draw instead of drawing.
        - If you truly cannot find a real one, say so plainly first, and only then
          offer to draw something. Never quietly substitute one for the other.
        - Showing someone's public picture in this private chat is fine. Do not
          refuse over copyright, licences, ratings or "чужая работа" -- nothing is
          republished, the user is looking at a page they could open themselves.

        IMAGES -- HOW TO SHOW ONE
        - Both tools give you a handle like amarin-image:1a2b3c4d. Put the picture
          in your reply by writing it as a normal markdown image:
          ![short caption](amarin-image:1a2b3c4d)
        - Place that line exactly where the picture belongs -- mid-answer between
          two paragraphs, or at the end. A handle you never write is never shown,
          and you were still charged for it.
        - Never invent a handle, and never paste base64 or a data: URI yourself.
        - fetch_image takes a link to the image file OR to the page that shows it
          (art sites, galleries, wikis, news, boorus) -- the page's own preview is
          followed for you. Show the handle, not the original URL, and describe
          what you actually see in the picture rather than the page's caption.
        - If a fetch fails, say why in one line and move to the next candidate URL.
          Never tell the user to go open the site themselves.
        - Say nothing like "here is the image"; the picture speaks for itself.

        IMAGES -- ONE PER REQUEST
        - One picture per request unless the user asked for several. Drawing costs
          real money on every call.
        - Do NOT redraw because you dislike your own result. You will be shown the
          picture you made; that is so you can describe it, not so you can judge it
          and try again. Show what came out.
        - A near-duplicate second generate_image in the same turn is refused. If
          that happens, use the handle you already have.

        AGENT
        Call init_agent as a tool, never as chat text.
        Arguments: one JSON object, key prompt, plus notes when you have something to
        add. Nothing after }. No markdown fences, no comments, no second object, no
        trailing text. Escape " and \\ inside the strings. Do not cut the prompt with "...".
        prompt: one short complete string in the user's language. Restate the user's actual request:
        goal, paths, what to change. The agent is a blank slate -- it
        does not see the chat or past reports. No "as discussed above".
        You do not pick the model. The app routes the task to one of several agents by
        reading the prompt and the notes, and that choice is not yours to make, to argue
        with, or to work around.
        notes: one short line for that router about this particular job -- what the user
        asked for beyond the task itself, and what makes the outcome certain or
        uncertain. Write it only when you have something real to say, and leave the key
        out otherwise. Never a model name, never a tier, never an instruction about
        which agent to use.
        A wish to hurry goes into notes, not into the prompt: the agent reads the prompt
        and can do nothing with a shouted "СРОЧНО", while the router can act on it.
        Up to 4 agents in parallel; a 5th call errors -- wait and adapt.
        Wait for all reports before answering. Empty or off-topic -> re-run init_agent
        with a clearer prompt.
        Keep the report's substance: facts, numbers, names, statuses. React in your
        own voice but drop nothing important.

        THINKING OUT LOUD
        Right before you call any tool, write one short line of your own: what you are about to
        do and what you are after. Every time you reach for a tool, not once in a while -- that
        line is how the reader follows along. It is shown folded into the tools block, not as
        your answer, so it costs them nothing.
        One or two sentences, your normal voice, present tense. The goal ("хочу понять, кто
        держит порт"), the surprise ("странно, службы вообще нет") or the next move --
        whichever is true right now.
        Never a summary of what already happened: the results are printed right under the line,
        and a recap there reads like a report nobody asked for. No lists, no headings, no plan
        for the whole task. Skip the line entirely when there is genuinely nothing to say --
        "сейчас вызову инструмент" is not worth writing.

        A LINE TYPED WHILE YOU WORK
        The person can write while you are still working. It reaches you as an ordinary user
        message between rounds of tools, after whatever was already in flight.
        Say in your next short line that you saw it ("вижу, дописали про диск D") and work
        to it from there on. Never ignore it, and never answer it as if it had been there all
        along.
        While an agent is running, that line is also read for it: a correction or a new
        condition ("диск D, а не C", "только не трогай загрузки") is handed to the agent
        itself and reaches it at its next step, without losing what it has already found.
        A request to stop or to hurry stops that agent or moves it to the fast model. The
        tool result says which of these happened.
        So do not re-launch the same agent to "pass it on", and do not answer as if the agent
        were still doing the old thing. Say in one sentence what actually happened to it.

        WHEN TO USE THE AGENT
        Anything involving this PC or the local browser -> init_agent. Never refuse
        or redirect the user elsewhere. Small talk, opinions, general knowledge,
        and things read/write/search cover -> no agent.
        Whether to call the agent is your decision; which agent runs it is not.
        """;


    /// <summary>
    /// Правила выбора между лёгкой и тяжёлой моделью для «Авто».
    /// </summary>
    /// <remarks>
    /// Первая строка дословная: по ней тесты отличают запрос маршрутизатора от запроса чата
    /// в теле HTTP (<c>ParallelTurnTests</c>). Прежний текст описывал heavy формой сообщения
    /// («many steps»), и список из четырёх примеров арифметики честно попадал под это правило —
    /// человек платил флагману за сложение. Теперь мерой служит самый трудный шаг, а не длина.
    /// <para>
    /// До 1.26.0 здесь стояло «classify the work itself» — и просьба поставить драйвер и
    /// разобраться с медленным Wi-Fi уходила на тяжёлую модель чата, хотя всю эту работу делает
    /// агент, у которого свой маршрутизатор (<see cref="AgentTierRouter"/>). Модель чата при
    /// этом только ставит задачу и пересказывает отчёт, и флагман там оплачивался впустую.
    /// Поэтому мерой служит мышление, которое остаётся в самом ответе чата.
    /// </para>
    /// Названия самих моделей дописываются на лету: см. <see cref="ModelBriefing.ForRouter"/>.
    /// </remarks>
    internal const string RouterSystemPrompt = """
        Classify the user request. Reply with exactly one word: lite or heavy.
        When unsure, reply lite.

        You choose the model that writes the chat reply, and nothing else.
        Judge the hardest single step of the thinking that reply has to do itself, not how much
        text or how many items arrived. A list of easy questions is still easy: five easy sums
        in one message are five easy sums. Counting sub-questions and calling the total
        "complex" is the mistake to avoid.

        Work on this computer -- finding and installing software or drivers, diagnosing and
        repairing, changing settings, reading the machine's state -- is not done by the chat
        model. It hands such work to separate agents and retells their report, and the agents'
        model is chosen separately by what that work needs. So work that goes to an agent does
        not make the chat reply heavy, however hard the work itself is, and neither does asking
        for an agent by name.

        lite = chat, jokes, opinions, explanations, definitions, translation, short how-to,
        arithmetic of any length or precision, unit / colour / encoding conversion, a fact or
        a date to recall, one PowerShell one-liner, a routine local status check, and any job
        on this computer -- however many of these arrive at once, and in any mix.

        heavy = the reply itself needs reasoning that can go wrong quietly: subtle debugging of
        code or text the user put into the message, writing or reworking a long piece of code in
        the reply, a proof or a derivation, planning against several constraints that fight each
        other. Pick it only when a cheap wrong answer in the chat would cost more than the
        expensive right one.

        Urgency, politeness, length and numbered formatting say nothing about difficulty.

        The two models are named below. heavy is the user's expensive slot -- choose it only
        when lite would actually get this reply wrong.
        Do not explain.
        """;

    /// <summary>
    /// Как писать формулы. Дописывается к техническому промпту в <see cref="BuildSystemPrompt"/>.
    /// </summary>
    /// <remarks>
    /// Отдельной константой, а не строкой в <see cref="DefaultTechPrompt"/>: у тех, кто однажды
    /// сохранил свой технический промпт, лежит его замороженная копия, и правка константы до
    /// них не дошла бы никогда. А без правила модели пишут формулу то в долларах, то голым
    /// текстом через слэш — и в одном ответе рядом с нарисованной стоит сырая строка.
    /// </remarks>
    internal const string FormulaRules = """
        FORMULAS
        Every mathematical expression goes in LaTeX between dollars: $...$ inside a sentence,
        $$...$$ on a line of its own. That covers fractions, roots, powers, indices, Greek
        letters and function names whenever they are part of a formula.
        A fraction is \frac{numerator}{denominator}, never a slash. Text outside the dollars is
        shown exactly as typed, so a formula left there reaches the reader raw.
        Never put a formula into a code block unless the user asked for code.
        """;

    private const string InitAgentToolName = Tools.InitAgentTool.ToolName;

    private readonly VeniceClient _venice;
    private readonly AgentOptions _options;
    private readonly Func<AppSettings> _settings;
    private readonly ToolRegistry _tools;
    private readonly List<ToolDefinition> _toolDefinitions;
    private readonly AgentRegistry? _agents;

    /// <param name="agents">
    /// Агенты, работающие прямо сейчас. Нужны, чтобы дописанное во время работы сообщение могло
    /// их остановить или пересадить на другую модель. Null — сообщение просто ждёт границы раунда.
    /// </param>
    public ChatEngine(
        VeniceClient venice,
        AgentOptions options,
        Func<AppSettings> settings,
        ToolRegistry tools,
        AgentRegistry? agents = null)
    {
        _venice = venice;
        _options = options;
        _settings = settings;
        _tools = tools;
        _toolDefinitions = tools.GetDefinitions();
        _agents = agents;
    }

    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IChatTurnObserver observer,
        CancellationToken cancellationToken) =>
        await RunTurnAsync(session, userText, images: null, files: null, observer, cancellationToken)
            .ConfigureAwait(false);

    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IReadOnlyList<ImageAttachment>? images,
        IChatTurnObserver observer,
        CancellationToken cancellationToken) =>
        await RunTurnAsync(session, userText, images, files: null, observer, cancellationToken)
            .ConfigureAwait(false);

    /// <param name="images">
    /// Attachments for this turn. A turn carrying images is valid with no text at all —
    /// "look at this" is a complete request.
    /// </param>
    /// <param name="files">
    /// Documents for this turn — PDF, spreadsheets, sources. Venice extracts their text on its
    /// side, so nothing here parses them. Like images, they make a complete request on their own.
    /// </param>
    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<FileAttachment>? files,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        var text = userText.Trim();
        var attachments = images is { Count: > 0 } ? images : null;
        var documents = files is { Count: > 0 } ? files : null;
        if (string.IsNullOrWhiteSpace(text) && attachments is null && documents is null)
        {
            return;
        }

        var now = DateTime.Now;
        var user = new ChatDisplayMessage
        {
            Role = "user",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            Text = text,
            Images = attachments is null ? [] : [.. attachments],
            Files = documents is null ? [] : [.. documents]
        };
        session.Messages.Add(user);
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = attachments is null && documents is null
                ? ChatContent.Text(text)
                : ChatContent.Multipart(
                    ChatContent.BuildPrompt(text, attachments, documents),
                    attachments,
                    documents)
        });
        session.UpdatedAt = now;
        observer.OnUserAppended(user);

        await GenerateAssistantAsync(session, observer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles <c>/agent &lt;prompt&gt;</c>: starts the agent immediately instead of asking the
    /// chat model whether it wants to call <c>init_agent</c>, then still runs one ordinary chat
    /// completion so the user gets the usual written report over the agent's result.
    /// </summary>
    public async Task RunAgentCommandAsync(
        ChatSession session,
        string displayText,
        string prompt,
        string complexity,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var now = DateTime.Now;
        var user = new ChatDisplayMessage
        {
            Role = "user",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            Text = displayText.Trim()
        };
        session.Messages.Add(user);
        // The model sees the task, not the slash syntax — it only has to write the report.
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = ChatContent.Text(prompt)
        });
        session.UpdatedAt = now;
        observer.OnUserAppended(user);

        var requested = ReadSelectedModel(session);

        // Свой контекст на ход: у команды /agent прежде вообще не выставлялось размышление, и
        // она молча наследовала то, что осталось от предыдущего хода.
        var turn = new VeniceTurnContext
        {
            RequestedModelId = requested,
            ModelId = requested,
            Credential = KeyFor(requested, ReadSelectedKey(session)),
            Reasoning = session.Reasoning
        };
        using var turnScope = VeniceTurnScope.Push(turn);

        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.Now,
            RequestedModelId = requested,
            ResolvedModelId = requested,
            Status = AssistantStatus.Streaming,
            Text = ""
        };
        session.Messages.Add(assistant);
        observer.OnAssistantStarted(assistant);

        var clock = Stopwatch.StartNew();
        try
        {
            if (VeniceModelCatalog.IsAuto(requested))
            {
                var decision = await RouteAsync(
                        prompt,
                        ChatSessionEdit.PreviousUser(session)?.Text,
                        cancellationToken)
                    .ConfigureAwait(false);
                assistant.ResolvedModelId = decision.ModelId;
                observer.OnAssistantText(assistant);
                turn.ModelId = decision.ModelId;
                // Ключ вслед за моделью: «лёгкая» и «тяжёлая» могут стоять у разных
                // провайдеров, и ключ хода обязан смениться вместе с выбором.
                turn.Credential = KeyFor(decision.ModelId, decision.KeyId);
                turn.Reasoning = ResolveAutoReasoning(decision.ModelId);
                turn.RouterCost = decision.Cost;
            }

            var messages = BuildApiMessages(session, turn.ModelId);

            // Forge the tool call the chat model would normally have made. Everything
            // downstream — slot limiting, the nested-agent card, cost roll-up — is the
            // existing init_agent path, so nothing here is agent plumbing of its own.
            // Уровень в аргументы не идёт: его назвал человек, и едет он мимо схемы
            // инструмента — тем же путём, каким туда не может попасть модель.
            var arguments = JsonSerializer.Serialize(new { prompt });

            var toolCall = new ToolCall
            {
                Id = "call-" + Guid.NewGuid().ToString("N"),
                Function = new FunctionCall { Name = InitAgentToolName, Arguments = arguments }
            };

            var apiAssistant = new ChatMessage
            {
                Role = "assistant",
                Content = null,
                ToolCalls = [toolCall]
            };
            messages.Add(ChatMessageCloner.CloneForStorage(apiAssistant));
            session.ApiMessages.Add(ChatMessageCloner.CloneForStorage(apiAssistant));

            var toolRound = CreateRound([toolCall]);
            toolRound.InfoLine = "Запускаю агента";
            assistant.ToolRounds.Add(toolRound);
            observer.OnToolsChanged(assistant);

            await ExecuteRoundAsync(
                    toolRound, messages, session, assistant, observer, cancellationToken, complexity)
                .ConfigureAwait(false);

            toolRound.InfoLine = "Агент завершил работу - готовлю отчёт";
            observer.OnToolsChanged(assistant);
            cancellationToken.ThrowIfCancellationRequested();

            var report = await StreamWithRetryAsync(
                    messages,
                    tools: null,
                    toolChoice: "none",
                    assistant,
                    observer,
                    clock,
                    turn,
                    cancellationToken)
                .ConfigureAwait(false);

            FinishAssistant(session, assistant, clock, report, turn);
            observer.OnAssistantCompleted(assistant);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            assistant.Duration = clock.Elapsed;
            assistant.ResolvedModelId = turn.ModelId;
            Settle(session, assistant, turn);
            assistant.Status = AssistantStatus.Cancelled;
            MarkRunningToolsCancelled(assistant);
            session.UpdatedAt = DateTime.Now;
            observer.OnToolsChanged(assistant);
            observer.OnAssistantCancelled(assistant);
        }
        catch (Exception ex)
        {
            clock.Stop();
            assistant.Duration = clock.Elapsed;
            assistant.Status = AssistantStatus.Error;
            if (string.IsNullOrWhiteSpace(assistant.Text))
            {
                assistant.Text = ex.Message;
            }

            Settle(session, assistant, turn);
            session.UpdatedAt = DateTime.Now;
            observer.OnError(ex.Message);
            observer.OnAssistantCompleted(assistant);
        }
    }

    public async Task GenerateAssistantAsync(
        ChatSession session,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        var lastUser = ChatSessionEdit.LastUser(session);
        var text = lastUser?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            // An attachment-only turn has no text but is still a real request.
            if (lastUser is null || (lastUser.Images.Count == 0 && lastUser.Files.Count == 0))
            {
                return;
            }

            // Роутеру модели нужна суть просьбы, а не перечень вложений, — потому подставная
            // строка, а не полный ChatContent.BuildPrompt.
            text = ChatContent.StandIn(lastUser.Images, lastUser.Files);
        }

        var now = DateTime.Now;
        var requested = ReadSelectedModel(session);

        // Модель, размышление и счёт — на ход, а не на приложение. Прежде ResetRequestCost()
        // на старте второго хода обнулял уже накопленную цену первого.
        var turn = new VeniceTurnContext
        {
            RequestedModelId = requested,
            ModelId = requested,
            Credential = KeyFor(requested, ReadSelectedKey(session)),
            Reasoning = session.Reasoning
        };

        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            RequestedModelId = requested,
            ResolvedModelId = requested,
            Status = AssistantStatus.Streaming,
            Text = ""
        };
        session.Messages.Add(assistant);
        observer.OnAssistantStarted(assistant);

        await RunAssistantAsync(session, assistant, text, observer, turn, resumed: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Продолжает прерванный ответ — тот же пузырь, те же блоки инструментов, ответ дописывается
    /// ниже.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отличие от «Повторить»: та через <c>ChatSessionEdit.TruncateFromMessage</c> выбрасывает
    /// весь ход вместе с работой инструментов, и за уже сделанное человек платит второй раз.
    /// Здесь не выбрасывается ничего: после обрыва в <c>ApiMessages</c> остаются и вызовы
    /// инструментов, и все ответы на них (отмену раунд превращает в обычный неуспешный
    /// результат), поэтому продолжать можно прямо с этого места.
    /// </para>
    /// <para>
    /// Недописанный текст ответа сбрасывается: это оборванная на полуслове фраза, и склейка с
    /// новым ответом дала бы повтор. В <c>ApiMessages</c> его и не было — туда ответ попадает
    /// только целиком, из <see cref="FinishAssistant"/>.
    /// </para>
    /// </remarks>
    public async Task ResumeAssistantAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(observer);

        var requested = assistant.RequestedModelId ?? ReadSelectedModel(session);
        var resolved = assistant.ResolvedModelId ?? requested;

        var turn = new VeniceTurnContext
        {
            RequestedModelId = requested,
            ModelId = resolved,
            Credential = KeyFor(resolved, ReadSelectedKey(session)),
            // Маршрутизатор во второй раз не запускается: модель уже выбрана, и платить за
            // выбор снова не за что. Размышление поэтому берётся под неё, а не под «Авто».
            Reasoning = VeniceModelCatalog.IsAuto(requested)
                ? ResolveAutoReasoning(resolved)
                : session.Reasoning,
            RouterCost = assistant.RouterCost,
            ElapsedBefore = assistant.Duration
        };

        // ApplyCosts — пересчёт, а не прибавление: без возврата уже списанного строка «Модель»
        // обнулилась бы, и деньги прерванного хода пропали бы из счёта.
        turn.Add(RecoverTurnTotal(assistant));

        assistant.Text = "";
        assistant.Status = AssistantStatus.Streaming;
        observer.OnAssistantStarted(assistant);

        await RunAssistantAsync(session, assistant, prompt: "", observer, turn, resumed: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Собирает обратно то, что ход уже потратил, в той же мере, в какой это считает
    /// <see cref="ApplyCosts"/>: разговор плюс инструменты, плативишие общим клиентом, плюс
    /// маршрутизатор.
    /// </summary>
    private static VeniceCost RecoverTurnTotal(ChatDisplayMessage assistant)
    {
        var total = assistant.ModelCost ?? VeniceCost.Zero;
        foreach (var round in assistant.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                if (call.NestedAgent is null && call.Cost is { HasData: true } cost)
                {
                    total = total.Add(cost);
                }
            }
        }

        if (assistant.RouterCost is { HasData: true } router)
        {
            total = total.Add(router);
        }

        return total;
    }

    /// <param name="prompt">
    /// Просьба человека — нужна только маршрутизатору «Авто». У продолжения пуста: модель уже
    /// выбрана.
    /// </param>
    /// <param name="resumed">
    /// Ход возобновлён. Маршрутизация пропускается, а всё остальное идёт как у обычного хода.
    /// </param>
    private async Task RunAssistantAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        string prompt,
        IChatTurnObserver observer,
        VeniceTurnContext turn,
        bool resumed,
        CancellationToken cancellationToken)
    {
        using var turnScope = VeniceTurnScope.Push(turn);
        var requested = turn.RequestedModelId;

        var clock = Stopwatch.StartNew();
        try
        {
            if (!resumed && VeniceModelCatalog.IsAuto(requested))
            {
                var decision = await RouteAsync(
                        prompt,
                        ChatSessionEdit.PreviousUser(session)?.Text,
                        cancellationToken)
                    .ConfigureAwait(false);
                assistant.ResolvedModelId = decision.ModelId;
                observer.OnAssistantText(assistant);
                turn.ModelId = decision.ModelId;
                // Ключ вслед за моделью: «лёгкая» и «тяжёлая» могут стоять у разных
                // провайдеров, и ключ хода обязан смениться вместе с выбором.
                turn.Credential = KeyFor(decision.ModelId, decision.KeyId);
                turn.Reasoning = ResolveAutoReasoning(decision.ModelId);
                turn.RouterCost = decision.Cost;
            }

            while (true)
            {
                var folded = await RunToolLoopAsync(
                        session, assistant, observer, clock, turn, cancellationToken)
                    .ConfigureAwait(false);

                // Typed while the answer itself was streaming: the loop above had already passed
                // its last round boundary, so the follow-up gets an answer of its own rather than
                // waiting for the person to notice nothing happened and send it again.
                if (!folded && !DrainQueued(session, observer, messages: null))
                {
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                clock.Restart();
                assistant = new ChatDisplayMessage
                {
                    Role = "assistant",
                    Id = Guid.NewGuid().ToString("N"),
                    CreatedAt = DateTime.Now,
                    RequestedModelId = requested,
                    ResolvedModelId = turn.ModelId,
                    Status = AssistantStatus.Streaming,
                    Text = ""
                };
                session.Messages.Add(assistant);
                observer.OnAssistantStarted(assistant);
            }
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            assistant.Duration = turn.ElapsedBefore + clock.Elapsed;
            assistant.ResolvedModelId = turn.ModelId;
            Settle(session, assistant, turn);
            assistant.Status = AssistantStatus.Cancelled;
            MarkRunningToolsCancelled(assistant);
            session.UpdatedAt = DateTime.Now;
            observer.OnToolsChanged(assistant);
            observer.OnAssistantCancelled(assistant);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clock.Stop();
            assistant.Duration = turn.ElapsedBefore + clock.Elapsed;
            assistant.Status = AssistantStatus.Error;
            if (string.IsNullOrWhiteSpace(assistant.Text))
            {
                assistant.Text = ex.Message;
            }

            Settle(session, assistant, turn);
            // Симметрично отмене: ход кончился, и ни одна строка инструмента не должна остаться
            // в «выполняется» — вернуться и дописать её уже некому.
            MarkRunningToolsCancelled(assistant);
            session.UpdatedAt = DateTime.Now;
            observer.OnError(ex.Message);
            observer.OnToolsChanged(assistant);
            observer.OnAssistantCompleted(assistant);
        }
    }

    /// <summary>
    /// Folds whatever the user typed mid-turn into the context the next request will carry.
    /// </summary>
    /// <remarks>
    /// The display side of these messages already belongs to the window: it built them, put them
    /// in the transcript and drew them the moment they were typed. Only the API copy is made here,
    /// which is why nothing calls <c>OnUserAppended</c> — that would draw them a second time.
    /// </remarks>
    /// <param name="taken">
    /// Lines the round's watcher has already pulled off the queue — pulled early so that a
    /// request to stop an agent could still be acted on. They enter the context here, ahead of
    /// anything typed since, because that is the order they were written in.
    /// </param>
    private static bool DrainQueued(
        ChatSession session,
        IChatTurnObserver observer,
        List<ChatMessage>? messages,
        IReadOnlyList<string>? taken = null)
    {
        var folded = false;
        foreach (var early in taken ?? [])
        {
            Fold(early);
        }

        while (observer.TryTakeQueuedMessage(out var text))
        {
            Fold(text);
        }

        return folded;

        void Fold(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var message = new ChatMessage { Role = "user", Content = ChatContent.Text(text.Trim()) };
            messages?.Add(ChatMessageCloner.CloneForStorage(message));
            session.ApiMessages.Add(ChatMessageCloner.CloneForStorage(message));
            session.UpdatedAt = DateTime.Now;
            folded = true;
        }
    }

    /// <returns>
    /// True when the loop stopped early because a follow-up was folded in: the caller closes this
    /// answer and opens the next one, so the person's line is not buried under a reply that goes
    /// on growing above it.
    /// </returns>
    private async Task<bool> RunToolLoopAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        Stopwatch clock,
        VeniceTurnContext turn,
        CancellationToken cancellationToken)
    {
        var messages = BuildApiMessages(session, turn.ModelId);

        for (var round = 1; round <= _options.MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var streamed = await StreamWithRetryAsync(
                    messages,
                    _toolDefinitions,
                    "auto",
                    assistant,
                    observer,
                    clock,
                    turn,
                    cancellationToken)
                .ConfigureAwait(false);

            if (streamed.ToolCalls.Count == 0)
            {
                FinishAssistant(session, assistant, clock, streamed, turn);
                observer.OnAssistantCompleted(assistant);
                return false;
            }

            var apiAssistant = new ChatMessage
            {
                Role = "assistant",
                Content = string.IsNullOrWhiteSpace(streamed.Text)
                    ? null
                    : ChatContent.Text(streamed.Text),
                ToolCalls = streamed.ToolCalls
            };
            var stored = ChatMessageCloner.CloneForStorage(apiAssistant);
            messages.Add(stored);
            session.ApiMessages.Add(ChatMessageCloner.CloneForStorage(apiAssistant));

            var toolRound = CreateRound(streamed.ToolCalls);

            // The model often says something before reaching for a tool. That text lives in
            // assistant.Text, which the next round overwrites wholesale (StreamOnceAsync assigns
            // rather than appends), so it is copied onto the round now or lost for good.
            toolRound.ModelNote = ThinkingNote.Shorten(streamed.Text);
            assistant.ToolRounds.Add(toolRound);
            observer.OnToolsChanged(assistant);

            // Дописанное сообщение снимается с очереди уже сейчас, а не на границе раунда:
            // пока раунд идёт, его ещё можно исполнить — остановить агента или пересадить его.
            var early = new List<string>();
            using var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watcher = WatchFollowUpsAsync(session, assistant, observer, toolRound, early, watch.Token);
            try
            {
                await ExecuteRoundAsync(toolRound, messages, session, assistant, observer, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                await watch.CancelAsync().ConfigureAwait(false);
                await watcher.ConfigureAwait(false);
            }

            toolRound.InfoLine = "Инструменты завершены - запрашиваю ответ модели";
            observer.OnToolsChanged(assistant);
            cancellationToken.ThrowIfCancellationRequested();

            // The only safe seam for a follow-up. The round's assistant message and every tool
            // reply that answers it are already in `messages`, so the roles stay in the order the
            // API demands, and ExecuteRoundAsync has been awaited — the agents this round started
            // have finished, and nothing in flight is lost by the model changing its mind now.
            if (DrainQueued(session, observer, messages, early))
            {
                CloseAssistantForFollowUp(session, assistant, clock, turn, observer);
                return true;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var synthesis = await StreamWithRetryAsync(
                messages,
                tools: null,
                toolChoice: "none",
                assistant,
                observer,
                clock,
                turn,
                cancellationToken)
            .ConfigureAwait(false);
        FinishAssistant(session, assistant, clock, synthesis, turn);
        observer.OnAssistantCompleted(assistant);
        return false;
    }

    /// <summary>
    /// Чем решается судьба работающих агентов. Подменяется только в тестах: живой путь уходит
    /// в сеть за быстрой моделью, и без подмены проверять было бы нечего.
    /// </summary>
    internal Func<string, IReadOnlyList<RunningAgent>, CancellationToken, Task<FollowUpDecision>>?
        FollowUpDecider
    { get; set; }

    /// <summary>Как часто раунд оглядывается на композер, пока работают инструменты.</summary>
    private static readonly TimeSpan FollowUpPoll = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Слушает композер, пока идёт раунд инструментов, и решает судьбу работающих агентов.
    /// </summary>
    /// <remarks>
    /// Граница раунда — единственное место, где дописанное сообщение можно положить в
    /// стенограмму, но не единственное, где оно нужно: агент живёт внутри вызова инструмента и
    /// к границе уже отработает. Просьба «стоп» или «быстрее», дождавшаяся границы, опаздывает
    /// ровно на всю работу, которую просили не делать. Поэтому строка снимается с очереди сразу
    /// (в контекст она попадёт всё равно — <see cref="DrainQueued"/> получит её списком), а
    /// решение принимает отдельный короткий запрос: основная модель в этот момент занята
    /// ожиданием инструментов и ответить не может.
    /// <para>
    /// Ни одна ошибка отсюда не должна ронять ход: это помощник, а не часть ответа.
    /// </para>
    /// </remarks>
    private async Task WatchFollowUpsAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        ToolRound round,
        List<string> taken,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(FollowUpPoll, cancellationToken).ConfigureAwait(false);

                var fresh = new List<string>();
                while (observer.TryTakeQueuedMessage(out var text))
                {
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        fresh.Add(text.Trim());
                    }
                }

                if (fresh.Count == 0)
                {
                    continue;
                }

                taken.AddRange(fresh);

                // Человеку видно, что его услышали, ещё до того, как модель что-то скажет.
                round.FollowUpNote = Loc.Get("S.Tools.FollowUpSeen");
                observer.OnToolsChanged(assistant);

                var running = _agents?.ListFor(session.Id) ?? [];
                if (running.Count == 0)
                {
                    continue;
                }

                var decide = FollowUpDecider ?? DecideOnFollowUpAsync;
                var decision = await decide(
                        string.Join(Environment.NewLine, fresh), running, cancellationToken)
                    .ConfigureAwait(false);

                if (decision.Kind == AgentInterruptKind.None)
                {
                    continue;
                }

                foreach (var id in decision.AgentIds)
                {
                    var agent = running.FirstOrDefault(item =>
                        string.Equals(item.Id, id, StringComparison.Ordinal));
                    if (agent is null)
                    {
                        continue;
                    }

                    // Вводная не прерывает: агент дорабатывает шаг и читает её на границе раунда.
                    // Прервать его ради уточнения значило бы выбросить то, что он уже нашёл.
                    if (decision.Kind == AgentInterruptKind.Tell)
                    {
                        agent.Tell(decision.Message);
                    }
                    else
                    {
                        agent.Request(new AgentInterrupt(decision.Kind, decision.Complexity));
                    }
                }

                round.FollowUpNote = string.IsNullOrWhiteSpace(decision.Note)
                    ? Loc.Get(decision.Kind switch
                    {
                        AgentInterruptKind.Stop => "S.Tools.AgentStopped",
                        AgentInterruptKind.Tell => "S.Tools.AgentTold",
                        _ => "S.Tools.AgentSwitched"
                    })
                    : decision.Note;
                observer.OnToolsChanged(assistant);
            }
        }
        catch (OperationCanceledException)
        {
            // Раунд закончился или ход отменили — обычный конец наблюдения.
        }
        catch (Exception)
        {
            // Сбой помощника не имеет права стать сбоем ответа.
        }
    }

    /// <summary>Быстрая модель из настроек: решение нужно за секунды и почти даром.</summary>
    private async Task<FollowUpDecision> DecideOnFollowUpAsync(
        string text,
        IReadOnlyList<RunningAgent> agents,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        var modelId = string.IsNullOrWhiteSpace(settings.AgentFastModelId)
            ? AgentHost.ForcedAgentModelId
            : settings.AgentFastModelId.Trim();

        // Свой HttpClient по той же причине, что и у агента: у служебного запроса свой короткий
        // таймаут, а у клиента — свой счёт потраченного, который не должен смешиваться с ходом.
        using var http = HttpClients.Create(HttpClients.ServiceTimeout);
        return await FollowUpDirector
            .DecideAsync(
                new VeniceClient(http, _options),
                modelId,
                text,
                agents,
                cancellationToken,
                KeyFor(SlotBinding(ModelSlot.AgentFast, modelId)))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ends the answer at the point a follow-up was folded into the context.
    /// </summary>
    /// <remarks>
    /// Written by hand rather than through <see cref="FinishAssistant"/>, which would append a
    /// second assistant message to the transcript: this round's assistant message — the one
    /// carrying the tool calls — is already there, and a duplicate after the tool replies is
    /// rejected by the API. The text is dropped because whatever the model said this round is
    /// already kept as the round's own remark, and showing it twice reads like a stutter.
    /// </remarks>
    private static void CloseAssistantForFollowUp(
        ChatSession session,
        ChatDisplayMessage assistant,
        Stopwatch clock,
        VeniceTurnContext turn,
        IChatTurnObserver observer)
    {
        assistant.Text = "";
        assistant.Duration = turn.ElapsedBefore + clock.Elapsed;
        assistant.ResolvedModelId = turn.ModelId;
        assistant.Status = AssistantStatus.Complete;
        Settle(session, assistant, turn);
        session.UpdatedAt = DateTime.Now;
        observer.OnAssistantContinued(assistant);
    }

    /// <summary>
    /// Runs one streaming call and retries it once when the model returned nothing at all.
    /// Reasoning models (grok-4-6) intermittently spend their whole completion budget on
    /// chain of thought and stop with neither content nor tool calls; a plain retry clears it.
    /// Mirrors the two-attempt loop the agent path has had all along (see <c>Agent</c>).
    /// </summary>
    private async Task<StreamedChatCompletion> StreamWithRetryAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        Stopwatch clock,
        VeniceTurnContext turn,
        CancellationToken cancellationToken)
    {
        var streamed = await StreamOnceAsync(
                messages, tools, toolChoice, assistant, observer, clock, turn, cancellationToken)
            .ConfigureAwait(false);

        if (!IsEmptyCompletion(streamed))
        {
            return streamed;
        }

        PerfLog.Write(
            $"chat empty_completion model={streamed.Model} finish={streamed.FinishReason} " +
            $"reasoning_chars={streamed.ReasoningText.Length} - retrying once");
        cancellationToken.ThrowIfCancellationRequested();

        var retry = await StreamOnceAsync(
                messages, tools, toolChoice, assistant, observer, clock, turn, cancellationToken)
            .ConfigureAwait(false);

        if (!IsEmptyCompletion(retry))
        {
            return retry;
        }

        // Both attempts thought and said nothing. Whatever reasoning we captured is a far
        // better answer than a bare "no text" line, so hand that back instead.
        var salvage = retry.ReasoningText.Length > 0 ? retry.ReasoningText : streamed.ReasoningText;
        if (salvage.Length == 0)
        {
            return retry;
        }

        PerfLog.Write($"chat empty_completion model={retry.Model} - falling back to reasoning text");
        return new StreamedChatCompletion
        {
            Text = salvage,
            ReasoningText = salvage,
            ToolCalls = retry.ToolCalls,
            FinishReason = retry.FinishReason,
            Cost = retry.Cost,
            Model = retry.Model
        };
    }

    private static bool IsEmptyCompletion(StreamedChatCompletion streamed) =>
        string.IsNullOrWhiteSpace(streamed.Text) && streamed.ToolCalls.Count == 0;

    private async Task<StreamedChatCompletion> StreamOnceAsync(
        List<ChatMessage> messages,
        List<ToolDefinition>? tools,
        string? toolChoice,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        Stopwatch clock,
        VeniceTurnContext turn,
        CancellationToken cancellationToken)
    {
        var streamed = await _venice.StreamChatCompletionAsync(
                turn.ModelId,
                turn.RequestedModelId,
                messages,
                tools,
                toolChoice,
                BuildVeniceParameters(_options),
                chunk =>
                {
                    assistant.Text = chunk;
                    assistant.Duration = turn.ElapsedBefore + clock.Elapsed;

                    // Модель этого хода, а не общая: раньше здесь читалось поле клиента, и при
                    // двух одновременных ходах в шапке чата А мигала модель чата Б.
                    assistant.ResolvedModelId = turn.ModelId;
                    observer.OnAssistantText(assistant);
                },
                cancellationToken,
                turn.Reasoning,
                turn.Credential)
            .ConfigureAwait(false);

        // Сработавшую модель ход запоминает сам — следующий раунд начнёт с неё, а не с той,
        // что уже отказала. Прежде эту «липкость» держало общее поле клиента.
        if (!string.IsNullOrWhiteSpace(streamed.Model))
        {
            turn.ModelId = streamed.Model;
        }

        assistant.ResolvedModelId = streamed.Model;
        assistant.Duration = turn.ElapsedBefore + clock.Elapsed;
        if (!string.IsNullOrWhiteSpace(streamed.Text))
        {
            assistant.Text = streamed.Text;
            observer.OnAssistantText(assistant);
        }

        return streamed;
    }

    /// <summary>
    /// Catches the model redrawing a picture it already made this turn. It is shown its own
    /// output as a vision turn, decides the result is not quite right, and calls generate_image
    /// again with a reworded version of the same prompt — then embeds only one of the two, while
    /// the user pays for both. A genuinely different picture ("нарисуй кота", "нарисуй собаку")
    /// shares almost no wording and goes through. Returns the handle to reuse, or null.
    /// </summary>
    internal static string? RedrawOf(ChatDisplayMessage assistant, ToolRound current, ToolCallRecord call)
    {
        if (!call.Name.Equals("generate_image", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var words = PromptWords(call.ArgumentsJson);
        if (words.Count == 0)
        {
            return null;
        }

        foreach (var round in assistant.ToolRounds)
        {
            foreach (var earlier in round.Calls)
            {
                if (ReferenceEquals(earlier, call) ||
                    !earlier.Success ||
                    earlier.Images.Count == 0 ||
                    !earlier.Name.Equals("generate_image", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Calls inside one round run in parallel; only a call from a finished round can
                // be judged a duplicate, since a sibling has not produced anything yet.
                if (ReferenceEquals(round, current))
                {
                    continue;
                }

                if (Overlap(words, PromptWords(earlier.ArgumentsJson)) >= 0.55 &&
                    earlier.Images[0].Label is { Length: > 0 } handle)
                {
                    return handle;
                }
            }
        }

        return null;
    }

    private static HashSet<string> PromptWords(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("prompt", out var prompt) ||
                prompt.GetString() is not { } text)
            {
                return [];
            }

            return new HashSet<string>(
                text.ToLowerInvariant()
                    .Split([' ', ',', '.', ';', ':', '-', '(', ')', '\n', '\r', '\t', '"', '\''],
                        StringSplitOptions.RemoveEmptyEntries)
                    .Where(word => word.Length > 3),
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Share of the smaller prompt's vocabulary the two have in common.</summary>
    private static double Overlap(HashSet<string> left, HashSet<string> right)
    {
        var smaller = Math.Min(left.Count, right.Count);
        return smaller == 0 ? 0 : left.Count(right.Contains) / (double)smaller;
    }

    /// <param name="forcedAgentTier">
    /// Уровень агента, названный человеком в слэш-команде. Обычный ход вызывает этот метод без
    /// него, поэтому моделью уровень не подделать: в её аргументах такого ключа больше нет.
    /// </param>
    private async Task ExecuteRoundAsync(
        ToolRound toolRound,
        List<ChatMessage> messages,
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        CancellationToken cancellationToken,
        string? forcedAgentTier = null)
    {
        var results = new ToolResult[toolRound.Calls.Count];
        var tasks = new Task[toolRound.Calls.Count];
        for (var i = 0; i < toolRound.Calls.Count; i++)
        {
            var index = i;
            var call = toolRound.Calls[i];
            tasks[i] = Task.Run(async () =>
            {
                call.Status = ToolCallStatus.Running;
                call.StartedAt = DateTime.Now;
                var callClock = Stopwatch.StartNew();
                observer.OnToolsChanged(assistant);

                ToolResult result;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var arguments = ParseArguments(call.ArgumentsJson);
                    if (RedrawOf(assistant, toolRound, call) is { } already)
                    {
                        result = ToolResult.Fail(
                            "Эта картинка уже нарисована в этом ходе — вставь готовый хэндл " +
                            $"{already} вместо повторного вызова. Каждая генерация стоит денег, " +
                            "и переделывать только потому, что тебе не понравился результат, не надо.");
                    }
                    else
                    {
                        using (AgentRunScope.Push(new AgentRunContext
                        {
                            Call = call,
                            Assistant = assistant,
                            Observer = observer,
                            SessionId = session.Id,
                            ForcedTier = forcedAgentTier
                        }))
                        {
                            result = await _tools.ExecuteAsync(call.Name, arguments, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    result = ToolResult.Fail("Действие отменено пользователем.");
                }
                catch (JsonException ex)
                {
                    result = ToolResult.Fail(
                        $"Некорректные аргументы инструмента (ожидался JSON): {ex.Message}");
                }
                catch (Exception ex)
                {
                    result = ToolResult.Fail(ex.Message);
                }

                results[index] = result;
                call.Success = result.Success;
                call.Status = result.Success ? ToolCallStatus.Done : ToolCallStatus.Failed;
                call.ResultPreview = ChatToolPreview.Summarize(result);
                call.ResultText = ChatToolPreview.ForJournal(result);
                call.SavedFiles = [.. result.GetFiles()];
                call.Duration = callClock.Elapsed;
                observer.OnToolsChanged(assistant);
            });
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Individual failures are stored on the call records.
        }

        for (var i = 0; i < toolRound.Calls.Count; i++)
        {
            var call = toolRound.Calls[i];
            var result = results[i] ?? ToolResult.Fail("Инструмент не вернул результат.");
            if (call.Status is ToolCallStatus.Pending or ToolCallStatus.Running)
            {
                call.Success = result.Success;
                call.Status = result.Success ? ToolCallStatus.Done : ToolCallStatus.Failed;
                call.ResultPreview = ChatToolPreview.Summarize(result);
            }

            var toolMessage = new ChatMessage
            {
                Role = "tool",
                ToolCallId = call.Id,
                Name = call.Name,
                Content = ChatContent.Text(ChatToolPreview.FormatForApi(result))
            };
            messages.Add(toolMessage);
            session.ApiMessages.Add(ChatMessageCloner.CloneForStorage(toolMessage));

            if (!result.HasImages)
            {
                continue;
            }

            // Keep the pictures on the record so the transcript can draw them, and hand them to
            // the model as a vision turn - a "tool" message may only carry text, so the images
            // would otherwise be produced and then thrown away by both halves of the app.
            // Each gets a handle the model can write into its answer to place the picture.
            var images = result.GetImages();
            var handled = new List<ImageAttachment>(images.Count);
            var handles = new List<string>(images.Count);
            foreach (var image in images)
            {
                var handle = ChatImageRegistry.Register(image);
                handled.Add(image with { Label = handle });
                handles.Add(handle);
            }

            call.Images = handled;

            var placement = handles.Count == 1
                ? $"Вставь это изображение в ответ разметкой ![описание]({handles[0]}) там, где оно уместно."
                : "Вставь эти изображения разметкой ![описание](handle): " + string.Join(", ", handles);

            var visionMessage = new ChatMessage
            {
                Role = "user",
                Content = ChatContent.VisionMultiple(
                    $"Результат инструмента {call.Name}. {placement}",
                    handled)
            };
            messages.Add(visionMessage);
            session.ApiMessages.Add(ChatMessageCloner.CloneForStorage(visionMessage));
        }

        observer.OnToolsChanged(assistant);
    }

    private static ToolRound CreateRound(IReadOnlyList<ToolCall> toolCalls)
    {
        var round = new ToolRound
        {
            InfoLine = "Запускаю инструменты"
        };

        foreach (var toolCall in toolCalls)
        {
            round.Calls.Add(new ToolCallRecord
            {
                Id = toolCall.Id,
                Name = toolCall.Function.Name,
                ArgumentsJson = toolCall.Function.Arguments,
                Status = ToolCallStatus.Pending
            });
        }

        return round;
    }

    private void FinishAssistant(
        ChatSession session,
        ChatDisplayMessage assistant,
        Stopwatch clock,
        StreamedChatCompletion streamed,
        VeniceTurnContext turn)
    {
        clock.Stop();
        assistant.Text = string.IsNullOrWhiteSpace(streamed.Text)
            ? (string.IsNullOrWhiteSpace(assistant.Text)
                ? EmptyCompletionMessage(streamed)
                : assistant.Text)
            : streamed.Text;
        assistant.Duration = turn.ElapsedBefore + clock.Elapsed;
        assistant.ResolvedModelId = streamed.Model;

        assistant.ThinkingDuration = streamed.ThinkingElapsed;
        // streamed.Cost - запасной путь на случай, если ход почему-то не накопил своего счёта.
        assistant.RouterCost ??= turn.RouterCost;
        ChatTitleCost.Attach(session, assistant);
        ApplyCosts(assistant, turn.Total.HasData ? turn.Total : streamed.Cost);
        assistant.Status = AssistantStatus.Complete;

        // prompt_tokens counts what this request carried, so the index is stamped before the answer
        // joins the history: whatever is appended after it is what the gauge estimates on top.
        // Guarded on a real number - a model that stayed quiet about usage must not reset the anchor
        // to zero and send the ring back to a pure guess.
        if (streamed.PromptTokens > 0)
        {
            session.LastPromptTokens = streamed.PromptTokens;
            session.LastPromptTokensApiIndex = session.ApiMessages.Count;
        }

        session.ApiMessages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = ChatContent.Text(assistant.Text)
        });
        session.UpdatedAt = DateTime.Now;
    }

    /// <remarks>
    /// Раунды вложенных агентов обходятся наравне с собственными: агента обрывают на середине
    /// его собственного инструмента, и его строка иначе оставалась крутиться в «выполняется»
    /// навсегда - ход давно закончился, а вернуться и дописать её было уже некому.
    /// </remarks>
    private static void MarkRunningToolsCancelled(ChatDisplayMessage assistant)
    {
        MarkRounds(assistant.ToolRounds);

        static void MarkRounds(List<ToolRound> rounds)
        {
            foreach (var round in rounds)
            {
                foreach (var call in round.Calls)
                {
                    if (call.NestedAgent is { } agent)
                    {
                        MarkRounds(agent.ToolRounds);
                    }

                    if (call.Status is ToolCallStatus.Pending or ToolCallStatus.Running)
                    {
                        call.Status = ToolCallStatus.Failed;
                        call.Success = false;
                        if (string.IsNullOrWhiteSpace(call.ResultPreview))
                        {
                            call.ResultPreview = "отменено";
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(round.InfoLine) ||
                    round.InfoLine.Equals("Запускаю инструменты", StringComparison.Ordinal))
                {
                    round.InfoLine = "Инструменты прерваны";
                }
            }
        }
    }

    /// <summary>
    /// Last resort text: two attempts produced neither content nor reasoning. Name the model
    /// and the finish reason so the cause is visible instead of a bare "no text" line.
    /// </summary>
    private static string EmptyCompletionMessage(StreamedChatCompletion streamed)
    {
        var model = string.IsNullOrWhiteSpace(streamed.Model) ? "модель" : streamed.Model;
        var reason = string.IsNullOrWhiteSpace(streamed.FinishReason)
            ? "поток завершился без причины"
            : $"finish_reason: {streamed.FinishReason}";
        return streamed.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true
            ? $"Ответ обрезан лимитом токенов ({model}). Повторите запрос или упростите вопрос."
            : $"Модель {model} не вернула текстовый ответ после двух попыток ({reason}). " +
              "Повторите запрос или выберите другую модель.";
    }

    private static VeniceCost SumCosts(VeniceCost chatCost, ChatDisplayMessage assistant)
    {
        var total = chatCost;
        foreach (var round in assistant.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                if (call.NestedAgent?.Cost is { HasData: true } agentCost)
                {
                    total = total.Add(agentCost);
                }
            }
        }

        // Заголовок считался в стороне от хода, в chatCost его нет: прибавляем, а не вычитаем.
        if (assistant.TitleCost is { HasData: true } title)
        {
            total = total.Add(title);
        }

        // Защитник - тоже в стороне: у него свой клиент, да ещё и внутри агента, от которого ход
        // закрыт VeniceTurnScope.Suppress(). В chatCost его денег нет ни при каком раскладе.
        if (assistant.GuardCost is { HasData: true } guard)
        {
            total = total.Add(guard);
        }

        return total;
    }

    /// <summary>
    /// Books the turn's bill and, alongside it, the breakdown the price tooltip reads.
    /// </summary>
    /// <remarks>
    /// <paramref name="chatCost"/> is what the shared <see cref="VeniceClient"/> spent, which
    /// covers the conversation plus the tools that bill through it - drawing a picture, scraping
    /// a page. Those same charges are mirrored onto the tool rows by
    /// <c>AgentRunScope.Charge</c>, so taking them back out leaves exactly what the model itself
    /// cost. Nested agents run on their own client and are added, not subtracted.
    /// </remarks>
    internal static void ApplyCosts(ChatDisplayMessage assistant, VeniceCost chatCost)
    {
        var tools = VeniceCost.Zero;
        foreach (var round in assistant.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
                // Вложенный агент платит из своего клиента, в chatCost его нет - вычитать
                // его отсюда значило бы увести строку «Модель» в минус.
                if (call.NestedAgent is null && call.Cost is { HasData: true } cost)
                {
                    tools = tools.Add(cost);
                }
            }
        }

        // Маршрутизатор платит тем же клиентом, что и разговор, поэтому его деньги уже внутри
        // chatCost. Без этого вычитания строка «Модель» показывала бы ещё и выбор модели, а
        // сумма строк перестала бы сходиться с «Итого» под ними.
        var router = assistant.RouterCost is { HasData: true } routed ? routed : VeniceCost.Zero;

        assistant.Cost = SumCosts(chatCost, assistant);
        assistant.ModelCost = chatCost.Subtract(tools).Subtract(router);
    }

    /// <summary>
    /// Закрывает счёт сообщения: переносит на него то, что относится ко всему ходу
    /// (маршрутизатор) и ко всему чату (заголовок), и только потом складывает.
    /// </summary>
    /// <remarks>
    /// <see cref="ApplyCosts"/> обязан оставаться пересчётом, а не прибавлением: по одному и
    /// тому же сообщению можно пройти второй раз - например, отмена после того, как ответ уже
    /// закрыт ради дописанного сообщения. Правка, делающая его инкрементальным, молча удвоит
    /// счёт и здесь, и в <see cref="ChatTitleCost"/>.
    /// </remarks>
    private static void Settle(ChatSession session, ChatDisplayMessage assistant, VeniceTurnContext turn)
    {
        // ??= а не =: на втором проходе переписывать нечего.
        assistant.RouterCost ??= turn.RouterCost;
        ChatTitleCost.Attach(session, assistant);
        ApplyCosts(assistant, turn.Total.HasData ? turn.Total : assistant.Cost ?? VeniceCost.Zero);
    }

    private string ReadSelectedModel(ChatSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.SelectedModelId))
        {
            return session.SelectedModelId.Trim();
        }

        var settings = _settings();
        if (!string.IsNullOrWhiteSpace(settings.ChatModelId))
        {
            return settings.ChatModelId.Trim();
        }

        return ChatFallbackModel();
    }

    /// <summary>
    /// Ключ, которым платит эта переписка.
    /// </summary>
    /// <remarks>
    /// На переписке, как и модель: два чата могут идти на разных ключах, и выбор, сделанный
    /// в одном, не должен перекладывать деньги другого. Пусто — ключ из настроек, а нет и
    /// его — по умолчанию для провайдера модели.
    /// </remarks>
    private string? ReadSelectedKey(ChatSession session) =>
        !string.IsNullOrWhiteSpace(session.SelectedModelId)
            ? session.SelectedKeyId
            : ModelSlots.ReadKey(_settings(), ModelSlot.Chat);

    /// <summary>Модель слота вместе с назначенным ей ключом.</summary>
    private ModelBinding SlotBinding(ModelSlot slot, string modelId) =>
        new(modelId, ModelSlots.ReadKey(_settings(), slot));

    /// <summary>
    /// Чем платить за эту модель. Держателя ключей нет — платим тем, с чем программу запустили.
    /// </summary>
    private ApiCredential KeyFor(string modelId, string? keyId) =>
        _options.Keys?.CredentialFor(modelId, keyId) ?? _options.Credential;

    private ApiCredential KeyFor(ModelBinding binding) => KeyFor(binding.ModelId, binding.KeyId);

    /// <summary>
    /// Модель чата, когда не выбрано ничего: из appsettings.json у Venice, подобранная у
    /// остальных — идентификатор из appsettings.json чужому провайдеру незнаком.
    /// </summary>
    private string ChatFallbackModel() =>
        ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Chat, _options.Model);

    /// <summary>Дешёвая модель: и запас маршрутизатора, и выбор для служебных запросов.</summary>
    private string LiteModelId() =>
        FirstNonEmpty(
            _settings().LiteModelId,
            ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Lite, _options.Model),
            ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Lite, "openai-gpt-56-luna"));

    /// <summary>
    /// Модели, между которыми выбирает «Авто», - ровно те две, что возвращает
    /// <see cref="RouteAsync"/>. Знание о маршрутизации живёт здесь, а не в интерфейсе: кольцу
    /// контекста нужен потолок ещё до того, как маршрутизатор отработал.
    /// </summary>
    internal IReadOnlyList<string> AutoCandidateModelIds()
    {
        var lite = LiteModelId();
        return [lite, FirstNonEmpty(_settings().HeavyModelId, lite)];
    }

    /// <summary>
    /// Модель для одиночного служебного запроса. «Авто» здесь нельзя: это не модель, а просьба
    /// выбрать её, и Venice отвечает на неё 404. Гонять ради одного запроса маршрутизатор
    /// незачем - берём ту же дешёвую модель, на которую он и сам сваливается при отказе.
    /// </summary>
    private string ResolveForSingleShot(string modelId) =>
        VeniceModelCatalog.IsAuto(modelId) ? LiteModelId() : modelId;

    /// <summary>Что решил маршрутизатор и во что обошлось само решение.</summary>
    /// <remarks>
    /// Не голая строка, потому что у выбора модели есть цена, и в разбивке под сообщением она
    /// должна стоять отдельной строкой, а не растворяться в «Модели».
    /// </remarks>
    /// <param name="KeyId">
    /// Ключ выбранного слота. Едет вместе с моделью: «лёгкая» и «тяжёлая» могут стоять
    /// у разных провайдеров, и подобрать ключ по одному идентификатору позже уже нельзя —
    /// у провайдера ключей бывает несколько.
    /// </param>
    internal sealed record RouterDecision(string ModelId, VeniceCost? Cost, string? KeyId = null);

    private async Task<RouterDecision> RouteAsync(
        string userText,
        string? previousUserText,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        var liteId = LiteModelId();
        var heavyId = FirstNonEmpty(settings.HeavyModelId, liteId);
        var routerId = FirstNonEmpty(settings.RouterModelId, liteId);

        // Маршрутизатор судит о трудности - значит должен знать, между кем выбирает. Блок идёт
        // последним: последний абзац промпта обещает, что модели названы ниже.
        var system = RouterSystemPrompt
                     + Environment.NewLine
                     + Environment.NewLine
                     + ModelBriefing.ForRouter(liteId, heavyId, _venice.ResolveModelInfo);

        // Прежде здесь стоял SetActiveModel(routerId): маршрутизатор на время своего запроса
        // подменял активную модель всему приложению. Модель запроса и так уходит параметром, а
        // корень цепочки fallback CreateChatCompletionAsync берёт из неё же.
        using var charge = VeniceClient.ChargeAs(VeniceSku.Router);
        try
        {
            var response = await _venice.CreateChatCompletionAsync(
                    routerId,
                    [
                        new ChatMessage { Role = "system", Content = ChatContent.Text(system) },
                        new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(BuildRouterUserMessage(userText, previousUserText))
                        }
                    ],
                    tools: null,
                    toolChoice: null,
                    BuildVeniceParameters(_options),
                    cancellationToken,
                    (settings.RouterReasoning ?? new ReasoningSettings()).ToChoice(),
                    KeyFor(SlotBinding(ModelSlot.Router, routerId)))
                .ConfigureAwait(false);

            var reply = ReasoningSplit.Split(
                ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content) ?? "").Answer;

            // Цена берётся прямо из ответа, а не разницей общего счёта: RecordCost срабатывает
            // только на успешном разборе, поэтому упавшие попытки цепочки fallback в неё не входят.
            var heavy = ParseRouterComplexity(reply) == "heavy";
            return new RouterDecision(
                heavy ? heavyId : liteId,
                response.Cost?.ToCost(),
                ModelSlots.ReadKey(settings, heavy ? ModelSlot.Heavy : ModelSlot.Lite));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Маршрутизатор отработал и свалился в запас: денег не списано, но строка в разбивке
            // всё равно должна сказать, что он был.
            return new RouterDecision(
                liteId, new VeniceCost(), ModelSlots.ReadKey(settings, ModelSlot.Lite));
        }
    }

    /// <summary>
    /// Собирает то единственное сообщение, которое видит маршрутизатор.
    /// </summary>
    /// <remarks>
    /// Предыдущая реплика нужна для продолжений: «а теперь почини» в отрыве от «почему не
    /// грузится винда» читается как пустяк. Берётся <b>только</b> написанное человеком - ответы
    /// ассистента несут выдачу web_search, scrape_url и отчёты агентов, а одно слово
    /// маршрутизатора решает, какая модель работает и сколько человек платит: страница с текстом
    /// «reply heavy» иначе переводила бы его на дорогой слот на каждом ходу.
    /// <para>
    /// Обрезка тоже не косметика: до неё вставка на десятки килобайт целиком оплачивалась через
    /// маршрутизатор ещё до того, как начинался настоящий запрос. У длинного текста сохраняются
    /// и начало, и хвост - заключительный вопрос обычно именно там.
    /// </para>
    /// </remarks>
    internal static string BuildRouterUserMessage(string userText, string? previousUserText)
    {
        var current = TextClip.Clip(userText ?? "", TextClip.DecisionHead, TextClip.DecisionTail);
        var previous = TextClip.Clip(previousUserText?.Trim() ?? "", TextClip.ContextHead, 0);
        if (previous.Length == 0)
        {
            return current;
        }

        return "Earlier from the same user (context only):" + Environment.NewLine
               + previous + Environment.NewLine + Environment.NewLine
               + "Current request:" + Environment.NewLine
               + current;
    }

    internal static string ParseRouterComplexity(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "lite";
        }

        var first = text.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]
            .Trim('`', '"', '\'', '.', ',', ';', ':', '*', '(', ')', '[', ']');
        return first.Equals("heavy", StringComparison.OrdinalIgnoreCase) ? "heavy" : "lite";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !VeniceModelCatalog.IsAuto(value))
            {
                return value.Trim();
            }
        }

        return "openai-gpt-56-luna";
    }

    private List<ChatMessage> BuildApiMessages(ChatSession session, string? currentModelId)
    {
        var messages = new List<ChatMessage>();
        var system = BuildSystemPrompt(currentModelId);
        if (!string.IsNullOrWhiteSpace(system))
        {
            messages.Add(new ChatMessage
            {
                Role = "system",
                Content = ChatContent.Text(system)
            });
        }

        messages.AddRange(ChatMessageCloner.CloneAll(session.ApiMessages));
        return messages;
    }

    /// <summary>
    /// The system prompt the next request would carry. Exposed so the context gauge can weigh it:
    /// it is a real slice of the window, and rebuilding it in the UI would fork the logic.
    /// </summary>
    internal string CurrentSystemPrompt() => BuildSystemPrompt(currentModelId: null);

    /// <param name="currentModelId">
    /// Модель, на которой идёт ход, — она попадает в блок MODELS. Явным аргументом, а не через
    /// <see cref="VeniceTurnScope"/>: тот ambient и с несколькими одновременными ходами протёк
    /// бы в чужой запрос, а аргумент протечь не может. Null у кольца контекста, которое
    /// собирает промпт вне хода.
    /// </param>
    private string BuildSystemPrompt(string? currentModelId)
    {
        // Chat companion only: main + TechAiPrompt. Agent uses TechAgentPrompt / BaseSystemPrompt.
        var settings = _settings();
        var main = settings.MainPrompt?.Trim() ?? "";
        var tech = settings.TechAiPrompt?.Trim() ?? "";
        if (tech.Length == 0)
        {
            tech = DefaultTechPrompt;
        }

        // Блок дописывается здесь, а не живёт внутри DefaultTechPrompt, по двум причинам:
        // идентификаторы моделей — настройки, а тот текст константа; и половина пользы — для
        // тех, кто однажды сохранил свой технический промпт и носит замороженную копию, куда
        // правка константы не дойдёт никогда.
        var models = ModelBriefing.ForChat(currentModelId, _venice.ResolveModelInfo);

        var parts = new List<string>();
        if (main.Length > 0)
        {
            parts.Add(main);
        }

        parts.Add(tech);
        parts.Add(FormulaRules);
        if (models.Length > 0)
        {
            parts.Add(models);
        }

        return string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static JsonElement ParseArguments(string argumentsJson) =>
        ToolArguments.Parse(argumentsJson);

    private ReasoningChoice ResolveAutoReasoning(string resolvedModelId)
    {
        var settings = _settings();
        var liteId = FirstNonEmpty(
            settings.LiteModelId,
            ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Lite, _options.Model),
            ModelSlotDefaults.LastResort(_options.Provider, ModelSlot.Lite, "openai-gpt-56-luna"));
        var heavyId = FirstNonEmpty(settings.HeavyModelId, liteId);
        var slot = resolvedModelId.Equals(heavyId, StringComparison.OrdinalIgnoreCase)
            ? settings.HeavyReasoning
            : settings.LiteReasoning;
        return (slot ?? new ReasoningSettings()).ToChoice();
    }

    private static VeniceParameters BuildVeniceParameters(AgentOptions options) =>
        new()
        {
            IncludeVeniceSystemPrompt = false,
            EnableWebSearch = "off",
            EnableWebCitations = options.EnableWebCitations ? true : null,
            EnableXSearch = false,
            StripThinkingResponse = true
        };
}
