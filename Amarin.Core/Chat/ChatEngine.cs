using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    /// <summary>
    /// Правила инструментов только для обычного чата. Агент этого текста не видит — у него
    /// <see cref="Agent.BaseSystemPrompt"/> / <see cref="AppSettings.TechAgentPrompt"/>.
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

    /// <summary>
    /// Те же инструменты без <c>read_instruction</c> — для хода, когда включённых инструкций
    /// нет. Собран заранее, а не фильтруется на каждый запрос: список один и тот же.
    /// </summary>
    private readonly List<ToolDefinition> _toolDefinitionsWithoutInstructions;

    private readonly AgentRegistry? _agents;
    private readonly InstructionLibrary? _instructions;

    /// <summary>
    /// Куда инструменты чата идут за разрешением. Null — спросить некого, и любая запись
    /// отклоняется: молча писать в систему из чата нельзя.
    /// </summary>
    private readonly ConfirmationQueue? _confirmations;

    /// <summary>
    /// Проверка SynGuard вместо живой. Только для тестов: живая уходит в сеть, а проверять надо
    /// то, что вокруг неё.
    /// </summary>
    internal Func<SynGuardRequest, CancellationToken, Task<SynGuardReport>>? Guard { get; set; }

    /// <param name="agents">
    /// Агенты, работающие прямо сейчас. Нужны, чтобы дописанное во время работы сообщение могло
    /// их остановить или пересадить на другую модель. Null — сообщение просто ждёт границы раунда.
    /// </param>
    /// <param name="instructions">
    /// Инструкции пользователя. Null — блока о них в промпте нет, и инструмента чтения тоже.
    /// </param>
    /// <param name="confirmations">
    /// Очередь подтверждений — та же, что у агента. Null — запись из чата отклоняется.
    /// </param>
    public ChatEngine(
        VeniceClient venice,
        AgentOptions options,
        Func<AppSettings> settings,
        ToolRegistry tools,
        AgentRegistry? agents = null,
        InstructionLibrary? instructions = null,
        ConfirmationQueue? confirmations = null)
    {
        _venice = venice;
        _options = options;
        _settings = settings;
        _tools = tools;
        _toolDefinitions = tools.GetDefinitions();
        _toolDefinitionsWithoutInstructions = _toolDefinitions
            .Where(definition => !string.Equals(
                definition.Function.Name, ReadInstructionTool.ToolName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        _agents = agents;
        _instructions = instructions;
        _confirmations = confirmations;
    }

    /// <summary>
    /// Включённые инструкции на этот ход. Снимается один раз, и по нему выбираются и блок
    /// промпта, и список инструментов: разойдись они, модель видела бы оглавление без способа
    /// его открыть или инструмент без оглавления.
    /// </summary>
    /// <remarks>Профиль чата (D11) сужает набор до выбранных в нём.</remarks>
    private IReadOnlyList<Instruction> ActiveInstructions(ChatSession? session = null) =>
        ChatProfile.Filter(_instructions?.EnabledSnapshot() ?? [], session?.Profile);

    /// <summary>
    /// Инструменты хода: без инструкций инструмент их чтения только сбивал бы модель, а
    /// выключенных человеком инструментов модель не видит вовсе.
    /// </summary>
    internal List<ToolDefinition> ToolsFor(IReadOnlyList<Instruction> instructions) =>
        ToolGate.WithoutDisabled(
            instructions.Count > 0 ? _toolDefinitions : _toolDefinitionsWithoutInstructions,
            _settings());

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
    /// Картинки хода. С ними ход годится и без текста — «посмотри» уже полная просьба.
    /// </param>
    /// <param name="files">
    /// Документы хода — PDF, таблицы, исходники. Текст из них достаёт Venice на своей стороне,
    /// здесь их никто не разбирает. Как и картинки, сами по себе они — полная просьба.
    /// </param>
    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<FileAttachment>? files,
        IChatTurnObserver observer,
        CancellationToken cancellationToken) =>
        await RunTurnAsync(session, userText, images, files, quotes: null, observer, cancellationToken)
            .ConfigureAwait(false);

    /// <param name="quotes">
    /// Фрагменты прежних ответов, на которые отвечает это сообщение. Сами по себе просьбой не
    /// являются: сообщение из одних цитат без текста не отправляется, как и пустое.
    /// </param>
    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IReadOnlyList<ImageAttachment>? images,
        IReadOnlyList<FileAttachment>? files,
        IReadOnlyList<MessageQuote>? quotes,
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
            Files = documents is null ? [] : [.. documents],
            Quotes = quotes is null ? [] : [.. quotes]
        };
        session.Messages.Add(user);

        // Маршрутизатор «Авто» по-прежнему читает только Text: цитата — слова ассистента, а
        // в них может оказаться текст чужой страницы, которому не место в выборе модели.
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ForUser(user, session.Messages, session.Messages.Count - 1)
        });
        session.UpdatedAt = now;
        observer.OnUserAppended(user);

        await GenerateAssistantAsync(session, observer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Обрабатывает <c>/agent &lt;задача&gt;</c>: запускает агента сразу, не спрашивая модель
    /// чата, хочет ли она звать <c>init_agent</c>, а потом всё же делает один обычный запрос —
    /// человек получает привычный письменный отчёт по итогу агента.
    /// </summary>
    /// <param name="placed">
    /// Сообщение человека, уже поставленное в ленту и историю, — якорь правки
    /// (<see cref="ChatBranches.Fork"/>). Null — команду только что отправили.
    /// </param>
    public async Task RunAgentCommandAsync(
        ChatSession session,
        string displayText,
        string prompt,
        string complexity,
        IChatTurnObserver observer,
        CancellationToken cancellationToken,
        ChatDisplayMessage? placed = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        var now = DateTime.Now;
        if (placed is null)
        {
            var user = new ChatDisplayMessage
            {
                Role = "user",
                Id = Guid.NewGuid().ToString("N"),
                CreatedAt = now,
                Text = displayText.Trim()
            };
            session.Messages.Add(user);
            // Модель видит задачу, а не команду через «/»: ей остаётся только написать отчёт.
            session.ApiMessages.Add(AgentCommandTurn(prompt));
            session.UpdatedAt = now;
            observer.OnUserAppended(user);
        }

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

            var messages = BuildApiMessages(session, turn.ModelId, ActiveInstructions(session));

            // Подставляем вызов, который обычно сделала бы модель чата: дальше всё — лимит
            // слотов, карточка вложенного агента, сложение цены — идёт обычным путём init_agent.
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
            toolRound.InfoLine = EngineLines.StartingAgent;
            assistant.ToolRounds.Add(toolRound);
            observer.OnToolsChanged(assistant);

            await ExecuteRoundAsync(
                    toolRound, messages, session, assistant, observer, cancellationToken, complexity)
                .ConfigureAwait(false);

            toolRound.InfoLine = EngineLines.AgentDone;
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

    /// <summary>
    /// Есть ли на что отвечать: текст или вложения. Сообщение из одних вложений — тоже просьба.
    /// </summary>
    /// <remarks>
    /// Перегенерация проверяет это до развилки: <see cref="GenerateAssistantAsync"/> на пустом
    /// вопросе выходит молча, и спрятанный ответ остался бы без нового на своём месте.
    /// </remarks>
    internal static bool IsRequest([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ChatDisplayMessage? user) =>
        user is not null &&
        (!string.IsNullOrWhiteSpace(user.Text) || user.Images.Count > 0 || user.Files.Count > 0);

    /// <summary>Запись истории под команду <c>/agent</c>: модель видит задачу, а не синтаксис команды.</summary>
    internal static ChatMessage AgentCommandTurn(string prompt) =>
        new() { Role = "user", Content = ChatContent.Text(prompt) };

    /// <param name="into">
    /// Ответ, уже стоящий в ленте, — якорь нового варианта после перегенерации
    /// (<see cref="ChatBranches.Fork"/>). Движок дописывает в него, как «Продолжить» дописывает в
    /// прерванный, а не ставит второй пузырь. Null — обычный новый ответ.
    /// </param>
    public async Task GenerateAssistantAsync(
        ChatSession session,
        IChatTurnObserver observer,
        CancellationToken cancellationToken,
        ChatDisplayMessage? into = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        var lastUser = ChatSessionEdit.LastUser(session);
        var text = lastUser?.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            if (!IsRequest(lastUser))
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

        var assistant = into ?? new ChatDisplayMessage
        {
            Role = "assistant",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now
        };
        assistant.RequestedModelId = requested;
        assistant.ResolvedModelId = requested;
        assistant.Status = AssistantStatus.Streaming;
        assistant.Text = "";
        if (into is null)
        {
            session.Messages.Add(assistant);
        }

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
    /// Отличие от «Повторить»: та прячет ход в вариант (<see cref="ChatBranches.Fork"/>) и
    /// начинает ответ заново, и за уже сделанную работу инструментов человек платит второй раз.
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

                // Написано, пока шёл сам ответ: цикл выше уже прошёл последнюю границу раунда, и
                // дописанное получает свой ответ — иначе человек ждал бы, пока заметит, что ничего
                // не произошло, и отправил бы ещё раз.
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
    /// Вплетает написанное посреди хода в контекст следующего запроса.
    /// </summary>
    /// <remarks>
    /// Показ этих сообщений — дело окна: оно их собрало, положило в ленту и нарисовало сразу при
    /// наборе. Здесь делается только копия для API, поэтому <c>OnUserAppended</c> никто не зовёт —
    /// он нарисовал бы их второй раз.
    /// </remarks>
    /// <param name="taken">
    /// Строки, которые наблюдатель раунда уже снял с очереди — заранее, чтобы успеть исполнить
    /// просьбу остановить агента. В контекст они идут раньше написанного позже: в том порядке
    /// их и писали.
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
    /// True — цикл остановлен раньше, потому что вплетено дописанное: вызывающий закрывает этот
    /// ответ и открывает следующий, чтобы строка человека не тонула под растущим над ней ответом.
    /// </returns>
    private async Task<bool> RunToolLoopAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        Stopwatch clock,
        VeniceTurnContext turn,
        CancellationToken cancellationToken)
    {
        var instructions = ActiveInstructions(session);
        var messages = BuildApiMessages(session, turn.ModelId, instructions);
        var tools = ToolsFor(instructions);

        for (var round = 1; round <= _options.MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var streamed = await StreamWithRetryAsync(
                    messages,
                    tools,
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

            // Модель часто что-то говорит перед вызовом инструмента. Этот текст лежит в
            // assistant.Text, а следующий раунд его перезапишет (StreamOnceAsync присваивает, а не
            // дописывает), — копируем на раунд сейчас, иначе он пропадёт.
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

            toolRound.InfoLine = EngineLines.ToolsDone;
            observer.OnToolsChanged(assistant);
            if (cancellationToken.IsCancellationRequested)
            {
                // Наблюдатель мог уже снять дописанные строки с очереди — тогда
                // RescueQueued их не увидит, и в ленте реплика осталась бы, а в истории модели
                // нет. Ответы инструментов раунда к этому месту уже записаны, поэтому
                // дописать строки можно, не разрывая пару «вызовы — ответы».
                DrainQueued(session, observer, messages, early);
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Единственный безопасный шов для дописанного. Сообщение ассистента этого раунда и все
            // ответы инструментов на него уже в `messages` — роли идут в порядке, которого требует
            // API, а ExecuteRoundAsync дождались: агенты раунда закончили, и смена решения модели
            // ничего идущего не потеряет.
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
    /// Один потоковый запрос и один повтор, если модель не вернула ничего. Рассуждающие модели
    /// (grok-4-6) временами тратят весь бюджет ответа на размышление и останавливаются без текста
    /// и без вызовов; простой повтор это лечит. Так же две попытки делает и агент (<c>Agent</c>).
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

        // Обе попытки думали и ничего не сказали. Пойманное размышление — куда лучший ответ, чем
        // голое «нет текста», отдаём его.
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

    private static void FinishAssistant(
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

        // prompt_tokens считает то, что нёс этот запрос, поэтому отметка ставится до того, как ответ
        // ляжет в историю: всё, что допишется после, кольцо контекста прикидывает сверху. Только при
        // настоящем числе — модель, промолчавшая о расходе, не должна сбрасывать опору в ноль и
        // возвращать кольцо к чистой прикидке.
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
                            call.ResultPreview = EngineLines.Cancelled;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(round.InfoLine) ||
                    round.InfoLine.Equals(EngineLines.RunningTools, StringComparison.Ordinal))
                {
                    round.InfoLine = EngineLines.ToolsStopped;
                }
            }
        }
    }

    /// <summary>
    /// Закрывает ответы, застывшие на «пишется»: процесс закрыли или он упал посреди хода, и
    /// записанный чат так и остался со статусом <see cref="AssistantStatus.Streaming"/>.
    /// </summary>
    /// <remarks>
    /// Зовут только для чата без живого хода. Иначе такой ответ при каждом открытии показывал
    /// бы вечную анимацию «думаю» и одну кнопку «Стоп», которой нечего останавливать, а у его
    /// инструментов крутились бы значки «выполняется». Прерванный ответ получает то же, что и
    /// отменённый человеком, — в том числе «Продолжить».
    /// </remarks>
    /// <returns>Было ли что закрывать.</returns>
    internal static bool CloseInterruptedReplies(ChatSession session)
    {
        var closed = false;
        lock (session.Gate)
        {
            foreach (var message in ChatBranches.AllMessages(session))
            {
                if (message.Role != "assistant" || message.Status != AssistantStatus.Streaming)
                {
                    continue;
                }

                message.Status = AssistantStatus.Cancelled;
                MarkRunningToolsCancelled(message);
                closed = true;
            }
        }

        return closed;
    }

    /// <summary>
    /// Последний текст, когда две попытки не дали ни ответа, ни размышления: называет модель и
    /// причину остановки, чтобы было видно, в чём дело, а не голое «нет текста».
    /// </summary>
    private static string EmptyCompletionMessage(StreamedChatCompletion streamed)
    {
        var model = string.IsNullOrWhiteSpace(streamed.Model) ? Loc.Get("S.Turn.SomeModel") : streamed.Model;
        var reason = string.IsNullOrWhiteSpace(streamed.FinishReason)
            ? Loc.Get("S.Turn.StreamEndedSilently")
            : $"finish_reason: {streamed.FinishReason}";
        return streamed.FinishReason?.Equals("length", StringComparison.OrdinalIgnoreCase) == true
            ? Loc.Format("S.Turn.CutByTokens", model)
            : Loc.Format("S.Turn.NoTextTwice", model, reason);
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
