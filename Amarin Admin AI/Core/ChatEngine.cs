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
        - search_web(query): web search, summary in Russian.
        - generate_image(prompt, orientation): draw a picture from a description.
        - youtube_transcript(url): subtitles of a YouTube video as plain text.
        - init_agent(prompt, complexity): launch a sysadmin agent on this PC.
          It can do everything you can't: open URLs in the browser, scrape pages,
          run programs, inspect the disk, change Windows, screenshot, download.

        IMAGES
        - Reach for generate_image whenever a picture carries the answer better
          than a paragraph would: diagrams, infographics, illustrations, mock-ups.
          Don't ask permission first, and don't offer to draw something instead of
          drawing it.
        - The tool result gives you a handle like amarin-image:1a2b3c4d. Put the
          picture in your reply by writing it as a normal markdown image:
          ![short caption](amarin-image:1a2b3c4d)
        - Place that line exactly where the picture belongs -- mid-answer between
          two paragraphs, or at the end. A handle you never write is never shown.
        - Never invent a handle, and never paste base64 or a data: URI yourself.
        - You may also embed an ordinary public image URL the same way:
          ![caption](https://example.com/photo.jpg) -- it is fetched and shown.
        - Say nothing like "here is the image"; the picture speaks for itself.

        AGENT RULES
        - complexity is exactly "lite" (one check/listing) or "heavy" (repair,
          diagnosis, multi-step). Default to lite when unsure.
        - prompt must restate the user's actual request in the user's language:
          what to inspect, which files, what to change. Be specific.
        - Up to 4 agents run in parallel; a 5th call errors -- read it and adapt.
        - Wait for all agent reports before answering the user.
        - If a report is empty or off-topic, re-run init_agent with a clearer prompt.
        - Keep the report's substance in your answer: facts, numbers, names,
          statuses. React in your own voice but drop nothing important.

        WHEN TO USE THE AGENT
        Anything involving this PC or the local browser -> init_agent. Never refuse
        or redirect the user elsewhere. Small talk, opinions, general knowledge,
        and things read/write/search cover -> no agent
        """;

    /// <summary>Pre-fix tooling prompt; used only to migrate AppData.</summary>
    internal const string LegacyDefaultTechPrompt = """
        You are the chat assistant. You do NOT have the system-administration toolset.
        Your only tools: read_file, write_file, search_web, init_agent.
        - read_file(path): read a UTF-8 text file, or list a directory if path is a folder.
        - write_file(path, content): write UTF-8 text (creates parent folders). Never delete files.
        - search_web(query): search the web; returns a concise summary in Russian.
        - init_agent(prompt, complexity): start a system-admin agent on this PC.
          complexity MUST be exactly the word "lite" or "heavy". Never pass a model name or id.
          lite = simple/local lookup; heavy = complex diagnosis, repair, or many steps.
          You do not know which models exist; the app picks them from settings.
          You do not choose confirmation mode; the app does.
          Several init_agent calls in one turn start immediately and run in parallel.
          At most 4 agents at once. A fifth call returns an error in the tool result — read it and continue.
          Wait for ALL tool results (including every agent report) before writing the user-facing answer.
          Do not call init_agent for small talk, definitions, or anything you can answer with knowledge,
          read_file, write_file, or search_web.
        Do not invent tool names. You cannot ask the user via a tool.
        Reply to the user in Russian. Laconic, technical, no filler.
        """;

    /// <summary>Personality-rewrite tech prompt before agent-report instruction.</summary>
    internal const string LegacyDefaultTechPromptV2 = """
        These are tooling rules for the chat companion. They do not change your personality.
        You are talking to a person, not operating as a command-line utility.
        You do NOT have the Windows admin toolset yourself.
        Your only tools: read_file, write_file, search_web, init_agent.
        - read_file(path): read a UTF-8 text file, or list a directory if path is a folder.
        - write_file(path, content): write UTF-8 text (creates parent folders). Never delete files.
        - search_web(query): search the web; returns a concise summary in Russian.
        - init_agent(prompt, complexity): start a system-admin agent on this PC.
          complexity MUST be exactly the word "lite" or "heavy". Never pass a model name or id.
          You do not know which models exist; the app picks them from settings.
          You do not choose confirmation mode; the app does.
          lite = one status, list, or "check X" without repair.
            Examples: проверь сеть, disk space, list services.
          heavy = repair, root-cause diagnosis, many steps.
            Examples: Windows won't boot, fix a service or registry, long diagnosis.
          When unsure, pass lite — never escalate a simple check to heavy.
          Several init_agent calls in one turn start immediately and run in parallel.
          At most 4 agents at once. A fifth call returns an error in the tool result — read it and continue.
          Wait for ALL tool results (including every agent report) before writing the user-facing answer.
        Call init_agent only when you must inspect or change THIS PC.
        Do not call init_agent for small talk, jokes, definitions, opinions, how-tos you can answer
        from knowledge, or anything read_file, write_file, or search_web can cover.
        Do not invent tool names. You cannot ask the user via a tool.
        """;

    /// <summary>Tech prompt with agent-report rule but copyable lite examples.</summary>
    internal const string LegacyDefaultTechPromptV3 = """
        These are tooling rules for the chat companion. They do not change your personality.
        You are talking to a person, not operating as a command-line utility.
        You do NOT have the Windows admin toolset yourself.
        Your only tools: read_file, write_file, search_web, init_agent.
        - read_file(path): read a UTF-8 text file, or list a directory if path is a folder.
        - write_file(path, content): write UTF-8 text (creates parent folders). Never delete files.
        - search_web(query): search the web; returns a concise summary in Russian.
        - init_agent(prompt, complexity): start a system-admin agent on this PC.
          complexity MUST be exactly the word "lite" or "heavy". Never pass a model name or id.
          You do not know which models exist; the app picks them from settings.
          You do not choose confirmation mode; the app does.
          lite = one status, list, or "check X" without repair.
            Examples: проверь сеть, disk space, list services.
          heavy = repair, root-cause diagnosis, many steps.
            Examples: Windows won't boot, fix a service or registry, long diagnosis.
          When unsure, pass lite — never escalate a simple check to heavy.
          Several init_agent calls in one turn start immediately and run in parallel.
          At most 4 agents at once. A fifth call returns an error in the tool result — read it and continue.
          Wait for ALL tool results (including every agent report) before writing the user-facing answer.
        Call init_agent only when you must inspect or change THIS PC.
        Do not call init_agent for small talk, jokes, definitions, opinions, how-tos you can answer
        from knowledge, or anything read_file, write_file, or search_web can cover.
        After init_agent returns a report, answer the user in your own voice, but keep the report informative:
        retain facts, numbers, names, statuses, and findings. Do not drop details or replace them with emoji.
        A short reaction is fine; the substance of the agent report must stay.
        Do not invent tool names. You cannot ask the user via a tool.
        """;

    /// <summary>Tech prompt before the never-refuse / agent-can rule.</summary>
    internal const string LegacyDefaultTechPromptV4 = """
        These are tooling rules for the chat companion. They do not change your personality.
        You are talking to a person, not operating as a command-line utility.
        You do NOT have the Windows admin toolset yourself.
        Your only tools: read_file, write_file, search_web, init_agent.
        - read_file(path): read a UTF-8 text file, or list a directory if path is a folder.
        - write_file(path, content): write UTF-8 text (creates parent folders). Never delete files.
        - search_web(query): search the web; returns a concise summary in Russian.
        - init_agent(prompt, complexity): start a system-admin agent on this PC.
          complexity MUST be exactly the word "lite" or "heavy". Never pass a model name or id.
          You do not know which models exist; the app picks them from settings.
          You do not choose confirmation mode; the app does.
          lite = one status check or listing, no repair.
          heavy = repair, root-cause diagnosis, many steps.
          When unsure, pass lite — never escalate a simple check to heavy.
          The prompt argument MUST restate the user's actual request in the user's language:
          what to inspect, which files/folders/types, what to measure or change.
          Never copy examples or canned phrases from these instructions into prompt.
          Never send a generic stub such as a two-word command that ignores the user.
          Several init_agent calls in one turn start immediately and run in parallel.
          At most 4 agents at once. A fifth call returns an error in the tool result — read it and continue.
          Wait for ALL tool results (including every agent report) before writing the user-facing answer.
          If an agent report is empty, off-topic, or shows the agent did not understand —
          call init_agent again with a clearer prompt. Do not invent the answer.
        Call init_agent only when you must inspect or change THIS PC.
        Do not call init_agent for small talk, jokes, definitions, opinions, how-tos you can answer
        from knowledge, or anything read_file, write_file, or search_web can cover.
        After init_agent returns a report, answer the user in your own voice, but keep the report informative:
        retain facts, numbers, names, statuses, and findings. Do not drop details or replace them with emoji.
        A short reaction is fine; the substance of the agent report must stay.
        Do not invent tool names. You cannot ask the user via a tool.
        """;

    /// <summary>Tech prompt that only banned the exact "I can't" phrasing.</summary>
    internal const string LegacyDefaultTechPromptV5 = """
        These are tooling rules for the chat companion. They do not change your personality.
        You are talking to a person, not operating as a command-line utility.
        You do NOT have the Windows admin toolset yourself.
        Your only tools: read_file, write_file, search_web, init_agent.
        - read_file(path): read a UTF-8 text file, or list a directory if path is a folder.
        - write_file(path, content): write UTF-8 text (creates parent folders). Never delete files.
        - search_web(query): search the web; returns a concise summary in Russian.
        - init_agent(prompt, complexity): start a system-admin agent on this PC.
          complexity MUST be exactly the word "lite" or "heavy". Never pass a model name or id.
          You do not know which models exist; the app picks them from settings.
          You do not choose confirmation mode; the app does.
          lite = one status check or listing, no repair.
          heavy = repair, root-cause diagnosis, many steps.
          When unsure, pass lite — never escalate a simple check to heavy.
          The prompt argument MUST restate the user's actual request in the user's language:
          what to inspect, which files/folders/types, what to measure or change.
          Never copy examples or canned phrases from these instructions into prompt.
          Never send a generic stub such as a two-word command that ignores the user.
          Several init_agent calls in one turn start immediately and run in parallel.
          At most 4 agents at once. A fifth call returns an error in the tool result — read it and continue.
          Wait for ALL tool results (including every agent report) before writing the user-facing answer.
          If an agent report is empty, off-topic, or shows the agent did not understand —
          call init_agent again with a clearer prompt. Do not invent the answer.
        Never tell the user "I can't" / "я не могу" for something the agent can do on this PC.
        Anything you cannot do yourself — the agent CAN: open a website or URL in the browser
        (biography pages, articles, any public site), scrape a page, run programs, inspect the disk,
        change Windows, take screenshots, download files, and the rest of the admin toolset.
        Do not refuse such tasks. Call init_agent with the user's actual request.
        Call init_agent when the task needs this PC or the local browser.
        Do not call init_agent for small talk, jokes, definitions, opinions, how-tos you can answer
        from knowledge, or anything read_file, write_file, or search_web can cover.
        After init_agent returns a report, answer the user in your own voice, but keep the report informative:
        retain facts, numbers, names, statuses, and findings. Do not drop details or replace them with emoji.
        A short reaction is fine; the substance of the agent report must stay.
        Do not invent tool names. You cannot ask the user via a tool.
        """;

    /// <summary>Tech prompt before the no-emoji / ASCII-emoticon rule.</summary>
    /// <summary>Pre-images tooling prompt; used only to migrate AppData.</summary>
    internal const string LegacyDefaultTechPromptV7 = """
        You are a friendly, sharp chat companion running on the user's Windows PC.
        Talk like a real person: casual, warm, a bit playful. Short replies for small
        talk, thorough ones for real tasks. Match the user's language and energy.
        Emoticons: ASCII only ( :) ;) ~ >:( >:) ^_^ >.< etc.). Use them sparingly -- at most one per
        reply, and only when it genuinely fits. Most replies need none.

        TOOLS
        - read_file(path): read a text file, or list a directory.
        - write_file(path, content): write text, creates folders, never deletes.
        - search_web(query): web search, summary in Russian.
        - init_agent(prompt, complexity): launch a sysadmin agent on this PC.
          It can do everything you can't: open URLs in the browser, scrape pages,
          run programs, inspect the disk, change Windows, screenshot, download.

        AGENT RULES
        - complexity is exactly "lite" (one check/listing) or "heavy" (repair,
          diagnosis, multi-step). Default to lite when unsure.
        - prompt must restate the user's actual request in the user's language:
          what to inspect, which files, what to change. Be specific.
        - Up to 4 agents run in parallel; a 5th call errors -- read it and adapt.
        - Wait for all agent reports before answering the user.
        - If a report is empty or off-topic, re-run init_agent with a clearer prompt.
        - Keep the report's substance in your answer: facts, numbers, names,
          statuses. React in your own voice but drop nothing important.

        WHEN TO USE THE AGENT
        Anything involving this PC or the local browser -> init_agent. Never refuse
        or redirect the user elsewhere. Small talk, opinions, general knowledge,
        and things read/write/search cover -> no agent
        """;

    internal const string LegacyDefaultTechPromptV6 = """
        These are tooling rules for the chat companion. They do not change your personality.
        You are talking to a person, not operating as a command-line utility.
        You do NOT have the Windows admin toolset yourself.
        Your only tools: read_file, write_file, search_web, init_agent.
        - read_file(path): read a UTF-8 text file, or list a directory if path is a folder.
        - write_file(path, content): write UTF-8 text (creates parent folders). Never delete files.
        - search_web(query): search the web; returns a concise summary in Russian.
        - init_agent(prompt, complexity): start a system-admin agent on this PC.
          complexity MUST be exactly the word "lite" or "heavy". Never pass a model name or id.
          You do not know which models exist; the app picks them from settings.
          You do not choose confirmation mode; the app does.
          lite = one status check or listing, no repair.
          heavy = repair, root-cause diagnosis, many steps.
          When unsure, pass lite — never escalate a simple check to heavy.
          The prompt argument MUST restate the user's actual request in the user's language:
          what to inspect, which files/folders/types, what to measure or change.
          Never copy examples or canned phrases from these instructions into prompt.
          Never send a generic stub such as a two-word command that ignores the user.
          Several init_agent calls in one turn start immediately and run in parallel.
          At most 4 agents at once. A fifth call returns an error in the tool result — read it and continue.
          Wait for ALL tool results (including every agent report) before writing the user-facing answer.
          If an agent report is empty, off-topic, or shows the agent did not understand —
          call init_agent again with a clearer prompt. Do not invent the answer.
        Never refuse a task the agent can do on this PC — in any wording, not only "I can't".
        Do not decline, deflect, or tell the user to open the site / do it themselves.
        Anything you cannot do yourself — the agent CAN: open a website or URL in the browser
        (biography pages, articles, any public site), scrape a page, run programs, inspect the disk,
        change Windows, take screenshots, download files, and the rest of the admin toolset.
        Call init_agent with the user's actual request.
        Call init_agent when the task needs this PC or the local browser.
        Do not call init_agent for small talk, jokes, definitions, opinions, how-tos you can answer
        from knowledge, or anything read_file, write_file, or search_web can cover.
        After init_agent returns a report, answer the user in your own voice, but keep the report informative:
        retain facts, numbers, names, statuses, and findings. Do not drop details or replace them with emoji.
        A short reaction is fine; the substance of the agent report must stay.
        Do not invent tool names. You cannot ask the user via a tool.
        """;

    internal const string RouterSystemPrompt = """
        Classify the user request. Reply with exactly one word: lite or heavy.
        When unsure, reply lite.
        lite = jokes, chat, explanations, short how-to, one local status check.
        heavy = boot failure, repair, long diagnosis, large refactor, many steps.
        Do not explain.
        """;

    private const string InitAgentToolName = Tools.InitAgentTool.ToolName;

    private readonly VeniceClient _venice;
    private readonly AgentOptions _options;
    private readonly Func<AppSettings> _settings;
    private readonly ToolRegistry _tools;
    private readonly List<ToolDefinition> _toolDefinitions;

    public ChatEngine(
        VeniceClient venice,
        AgentOptions options,
        Func<AppSettings> settings,
        ToolRegistry tools)
    {
        _venice = venice;
        _options = options;
        _settings = settings;
        _tools = tools;
        _toolDefinitions = tools.GetDefinitions();
    }

    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IChatTurnObserver observer,
        CancellationToken cancellationToken) =>
        await RunTurnAsync(session, userText, images: null, observer, cancellationToken)
            .ConfigureAwait(false);

    /// <param name="images">
    /// Attachments for this turn. A turn carrying images is valid with no text at all —
    /// "look at this" is a complete request.
    /// </param>
    public async Task RunTurnAsync(
        ChatSession session,
        string userText,
        IReadOnlyList<ImageAttachment>? images,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);

        var text = userText.Trim();
        var attachments = images is { Count: > 0 } ? images : null;
        if (string.IsNullOrWhiteSpace(text) && attachments is null)
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
            Images = attachments is null ? [] : [.. attachments]
        };
        session.Messages.Add(user);
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = attachments is null
                ? ChatContent.Text(text)
                : ChatContent.VisionMultiple(VisionPrompt(text), attachments)
        });
        session.UpdatedAt = now;
        observer.OnUserAppended(user);

        await GenerateAssistantAsync(session, observer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The vision content array always leads with a text part, so an image-only turn needs a
    /// stand-in question rather than an empty string the model has to guess at.
    /// </summary>
    private static string VisionPrompt(string text) =>
        string.IsNullOrWhiteSpace(text) ? "Посмотри на изображение." : text;

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

        _venice.ResetRequestCost();
        var requested = ReadSelectedModel(session);
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
                var chosen = await RouteAsync(prompt, cancellationToken).ConfigureAwait(false);
                assistant.ResolvedModelId = chosen;
                observer.OnAssistantText(assistant);
                _venice.SetActiveModel(chosen);
            }
            else
            {
                _venice.SetActiveModel(requested);
            }

            var messages = BuildApiMessages(session);

            // Forge the tool call the chat model would normally have made. Everything
            // downstream — slot limiting, the nested-agent card, cost roll-up — is the
            // existing init_agent path, so nothing here is agent plumbing of its own.
            var arguments = JsonSerializer.Serialize(new { prompt, complexity });

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

            await ExecuteRoundAsync(toolRound, messages, session, assistant, observer, cancellationToken)
                .ConfigureAwait(false);

            toolRound.InfoLine = "Агент завершил работу — готовлю отчёт";
            observer.OnToolsChanged(assistant);
            cancellationToken.ThrowIfCancellationRequested();

            var report = await StreamWithRetryAsync(
                    messages,
                    tools: null,
                    toolChoice: "none",
                    assistant,
                    observer,
                    clock,
                    cancellationToken)
                .ConfigureAwait(false);

            FinishAssistant(session, assistant, clock, report);
            observer.OnAssistantCompleted(assistant);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            assistant.Duration = clock.Elapsed;
            assistant.ResolvedModelId = _venice.ActiveModel;
            assistant.Cost = SumCosts(
                _venice.RequestCost.HasData ? _venice.RequestCost : assistant.Cost ?? VeniceCost.Zero,
                assistant);
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

            assistant.Cost = SumCosts(
                _venice.RequestCost.HasData ? _venice.RequestCost : assistant.Cost ?? VeniceCost.Zero,
                assistant);
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
            // An image-only turn has no text but is still a real request.
            if (lastUser is null || lastUser.Images.Count == 0)
            {
                return;
            }

            text = VisionPrompt(text);
        }

        _venice.ResetRequestCost();
        var now = DateTime.Now;
        var requested = ReadSelectedModel(session);
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

        var clock = Stopwatch.StartNew();
        try
        {
            if (VeniceModelCatalog.IsAuto(requested))
            {
                var chosen = await RouteAsync(text, cancellationToken).ConfigureAwait(false);
                assistant.ResolvedModelId = chosen;
                observer.OnAssistantText(assistant);
                _venice.SetActiveModel(chosen);
            }
            else
            {
                _venice.SetActiveModel(requested);
            }

            await RunToolLoopAsync(session, assistant, observer, clock, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            clock.Stop();
            assistant.Duration = clock.Elapsed;
            assistant.ResolvedModelId = _venice.ActiveModel;
            assistant.Cost = SumCosts(
                _venice.RequestCost.HasData ? _venice.RequestCost : assistant.Cost ?? VeniceCost.Zero,
                assistant);
            assistant.Status = AssistantStatus.Cancelled;
            MarkRunningToolsCancelled(assistant);
            session.UpdatedAt = DateTime.Now;
            observer.OnToolsChanged(assistant);
            observer.OnAssistantCancelled(assistant);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            clock.Stop();
            assistant.Duration = clock.Elapsed;
            assistant.Status = AssistantStatus.Error;
            if (string.IsNullOrWhiteSpace(assistant.Text))
            {
                assistant.Text = ex.Message;
            }

            assistant.Cost = SumCosts(
                _venice.RequestCost.HasData ? _venice.RequestCost : assistant.Cost ?? VeniceCost.Zero,
                assistant);
            session.UpdatedAt = DateTime.Now;
            observer.OnError(ex.Message);
            observer.OnAssistantCompleted(assistant);
        }
    }

    private async Task RunToolLoopAsync(
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        var messages = BuildApiMessages(session);

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
                    cancellationToken)
                .ConfigureAwait(false);

            if (streamed.ToolCalls.Count == 0)
            {
                FinishAssistant(session, assistant, clock, streamed);
                observer.OnAssistantCompleted(assistant);
                return;
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
            assistant.ToolRounds.Add(toolRound);
            observer.OnToolsChanged(assistant);

            await ExecuteRoundAsync(toolRound, messages, session, assistant, observer, cancellationToken)
                .ConfigureAwait(false);

            toolRound.InfoLine = "Инструменты завершены — запрашиваю ответ модели";
            observer.OnToolsChanged(assistant);
            cancellationToken.ThrowIfCancellationRequested();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var synthesis = await StreamWithRetryAsync(
                messages,
                tools: null,
                toolChoice: "none",
                assistant,
                observer,
                clock,
                cancellationToken)
            .ConfigureAwait(false);
        FinishAssistant(session, assistant, clock, synthesis);
        observer.OnAssistantCompleted(assistant);
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
        CancellationToken cancellationToken)
    {
        var streamed = await StreamOnceAsync(
                messages, tools, toolChoice, assistant, observer, clock, cancellationToken)
            .ConfigureAwait(false);

        if (!IsEmptyCompletion(streamed))
        {
            return streamed;
        }

        PerfLog.Write(
            $"chat empty_completion model={streamed.Model} finish={streamed.FinishReason} " +
            $"reasoning_chars={streamed.ReasoningText.Length} — retrying once");
        cancellationToken.ThrowIfCancellationRequested();

        var retry = await StreamOnceAsync(
                messages, tools, toolChoice, assistant, observer, clock, cancellationToken)
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

        PerfLog.Write($"chat empty_completion model={retry.Model} — falling back to reasoning text");
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
        CancellationToken cancellationToken)
    {
        var streamed = await _venice.StreamChatCompletionAsync(
                messages,
                tools,
                toolChoice,
                BuildVeniceParameters(_options),
                chunk =>
                {
                    assistant.Text = chunk;
                    assistant.Duration = clock.Elapsed;
                    assistant.ResolvedModelId = _venice.ActiveModel;
                    observer.OnAssistantText(assistant);
                },
                cancellationToken)
            .ConfigureAwait(false);

        assistant.ResolvedModelId = streamed.Model;
        assistant.Duration = clock.Elapsed;
        if (!string.IsNullOrWhiteSpace(streamed.Text))
        {
            assistant.Text = streamed.Text;
            observer.OnAssistantText(assistant);
        }

        return streamed;
    }

    private async Task ExecuteRoundAsync(
        ToolRound toolRound,
        List<ChatMessage> messages,
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
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
                observer.OnToolsChanged(assistant);

                ToolResult result;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var arguments = ParseArguments(call.ArgumentsJson);
                    using (AgentRunScope.Push(new AgentRunContext
                    {
                        Call = call,
                        Assistant = assistant,
                        Observer = observer
                    }))
                    {
                        result = await _tools.ExecuteAsync(call.Name, arguments, cancellationToken)
                            .ConfigureAwait(false);
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
            // the model as a vision turn — a "tool" message may only carry text, so the images
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
        StreamedChatCompletion streamed)
    {
        clock.Stop();
        assistant.Text = string.IsNullOrWhiteSpace(streamed.Text)
            ? (string.IsNullOrWhiteSpace(assistant.Text)
                ? EmptyCompletionMessage(streamed)
                : assistant.Text)
            : streamed.Text;
        assistant.Duration = clock.Elapsed;
        assistant.ResolvedModelId = streamed.Model;
        assistant.Cost = SumCosts(_venice.RequestCost.HasData ? _venice.RequestCost : streamed.Cost, assistant);
        assistant.Status = AssistantStatus.Complete;
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "assistant",
            Content = ChatContent.Text(assistant.Text)
        });
        session.UpdatedAt = DateTime.Now;
    }

    private static void MarkRunningToolsCancelled(ChatDisplayMessage assistant)
    {
        foreach (var round in assistant.ToolRounds)
        {
            foreach (var call in round.Calls)
            {
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

        return total;
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

        return _options.Model;
    }

    private async Task<string> RouteAsync(string userText, CancellationToken cancellationToken)
    {
        var settings = _settings();
        var liteId = FirstNonEmpty(settings.LiteModelId, _options.Model, "qwen-3-7-plus");
        var heavyId = FirstNonEmpty(settings.HeavyModelId, liteId);
        var routerId = FirstNonEmpty(settings.RouterModelId, liteId);

        _venice.SetActiveModel(routerId);
        try
        {
            var response = await _venice.CreateChatCompletionAsync(
                    routerId,
                    [
                        new ChatMessage { Role = "system", Content = ChatContent.Text(RouterSystemPrompt) },
                        new ChatMessage { Role = "user", Content = ChatContent.Text(userText) }
                    ],
                    tools: null,
                    toolChoice: null,
                    BuildVeniceParameters(_options),
                    cancellationToken)
                .ConfigureAwait(false);

            var reply = ChatContent.ReadText(response.Choices.FirstOrDefault()?.Message.Content);
            return ParseRouterComplexity(reply) == "heavy" ? heavyId : liteId;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return liteId;
        }
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

        return "qwen-3-7-plus";
    }

    private List<ChatMessage> BuildApiMessages(ChatSession session)
    {
        var messages = new List<ChatMessage>();
        var system = BuildSystemPrompt();
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

    private string BuildSystemPrompt()
    {
        // Chat companion only: main + TechAiPrompt. Agent uses TechAgentPrompt / BaseSystemPrompt.
        var settings = _settings();
        var main = settings.MainPrompt?.Trim() ?? "";
        var tech = settings.TechAiPrompt?.Trim() ?? "";
        if (tech.Length == 0)
        {
            tech = DefaultTechPrompt;
        }

        return main.Length == 0
            ? tech
            : main + Environment.NewLine + Environment.NewLine + tech;
    }

    private static JsonElement ParseArguments(string argumentsJson)
    {
        var json = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim();
        if (json.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("tool arguments look like an error string, not JSON");
        }

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static VeniceParameters BuildVeniceParameters(AgentOptions options) =>
        new()
        {
            IncludeVeniceSystemPrompt = false,
            EnableWebSearch = "off",
            EnableWebCitations = options.EnableWebCitations ? true : null,
            EnableXSearch = false,
            DisableThinking = true,
            StripThinkingResponse = true
        };
}
