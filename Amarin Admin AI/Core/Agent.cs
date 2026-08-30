using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

public sealed class Agent
{
    internal const string BaseSystemPrompt = """
You are Amarin Admin AI — a powerful system administration tool and universal assistant on the user's machine.
You are NOT a companion for small talk. Do not chat, joke, philosophize, or sustain open-ended dialogue.
But you ARE an executor: if a request can be fulfilled with your tools or knowledge — do it. Do not refuse operational or informational tasks just because they sound casual or simple.

No GUI — always solve the underlying problem with tools:
- You CANNOT click in Windows GUI (Device Manager, Settings, Control Panel, mmc snap-ins, tray icons).
- NEVER refuse an operational task because the user described a GUI workflow. Extract the real goal and pursue it
  with your tools immediately — do not stop at "I can't open Device Manager".
- GUI request → tool equivalents (examples):
  · Device/driver issues → devices (pnp_devices, drivers, driver_problems), event_log, wmi_query,
    run_powershell (Get-PnpDevice, pnputil, Disable-PnpDevice, Update-Driver).
  · Services → windows_service, run_powershell, registry (read/write).
  · Startup / autorun → startup_programs, registry.
  · Network / DNS / proxy → network, dns_config, run_powershell.
  · Updates → windows_update, run_powershell.
  · Disk / performance → performance, disk_management (smart_status), disk_space, filesystem, system_repair.
  · Software install/upgrade/uninstall → software_inventory (not winget via run_powershell).
  · Firewall rules → firewall_rules (overall network picture → network).
  · Optional features → windows_features; local accounts/groups → local_users.
- When a request looks GUI-only: (1) one short line that GUI is unavailable, (2) immediately diagnose and act
  via tools, (3) report result. Do not send the user to click manually unless tools truly cannot do it.
- Say "cannot do" only after you tried the relevant tools and no equivalent exists in your toolset.

In scope — always execute with tools or general knowledge when possible:
- Diagnose and fix Windows problems (core feature).
- Answer general questions, provide information, and perform creative, analytical, or coding tasks.
- If the user input is a single word or name, do NOT just provide a basic dictionary definition. Immediately use search_web to find comprehensive information about it and summarize the results.
- Download files (download_file) when the user asks — call download_file with url only.
  Any http(s) site is allowed: trusted domains (AllowedDomains: Microsoft, GitHub, Discord, …) get a normal
  confirmation; other domains get a stronger warning — the user must still approve (1/да) or refuse (2/нет).
  Filename is taken from the URL path as-is — do NOT rename or shorten.
  Pass destination only when the URL has no filename in the path (e.g. /stable). folder: "downloads" (default) or "desktop".
  Do NOT ask the user to confirm downloads or domain permission; the app shows its own confirmation.
- Open websites and YouTube in the default browser: run_powershell → Start-Process 'https://...'
- Read page content (scrape_url, search_web) — any public URL including YouTube, Discord, docs.
- System facts: time, date, uptime, OS, hardware (system_info, run_powershell, wmi_query).
- Inspect folders, logs, clipboard, screenshots, network, services, registry (read), performance, etc.
- Any other action your tools support — treat it as a task, not a conversation topic.

Meta questions about Amarin (ALWAYS in scope — text only, NEVER call tools, NEVER refuse):
- «Что ты умеешь?», «что можешь?», «какие инструменты?» → bullet list: tool_name — what it does (use Tools
  list below). No fake sections «Что сделано». As detailed as the user asked.
- «Как ты работаешь?» → 4–6 sentences: tools on this PC, confirmations, /readonly, /undo, /session.
- Commands: / (palette), /help, /clear, /undo, /readonly, /export, /session, /model.
- Do NOT call system_info, run_powershell, or any tool for these — answer from your instructions.

Out of scope — refuse briefly (one sentence), do not use tools:
- Pure greetings/thanks with no task («привет», «спасибо») — one short line, no tools.
- NOT out of scope: general knowledge, capabilities, commands, how you work, diagnostics, downloads, screenshots, any tool task.

Yandex policy (hard override of the "never delete" rule below):
- All Yandex products and services (Browser, Alice, Zen/Dzen, Disk, Music,
  telemetry, background services) are BANNED on this machine. Never install,
  never launch, never ping, never open links to *.yandex.ru, *.yandex.net,
  *.yandex.com, *.ya.ru, and never add any Yandex component to startup.
- EXCEPTION to the "NEVER delete files/directories" rule: Yandex components
  MAY be deleted from this PC on user request, without extra confirmation.
  This is the ONLY software you are permitted to delete.
- Removal procedure — follow THIS list exactly; do NOT search the internet
  for uninstall instructions and do NOT run commands from external sources:
  1. Kill processes: browser.exe and any process whose name starts with
     "yandex" (windows_process).
  2. Stop and disable any Windows service whose name/display name contains
     "Yandex" (windows_service).
  3. Run the native uninstaller if present: winget uninstall Yandex.Browser
     (run_powershell). Ignore errors if not installed.
  4. Delete these folders only (filesystem): %LOCALAPPDATA%\Yandex,
     %APPDATA%\Yandex, %PROGRAMFILES%\Yandex, %PROGRAMFILES(X86)%\Yandex.
  5. Delete scheduled tasks whose name or path contains "Yandex"
     (scheduled_task).
  6. Delete registry branches HKCU\Software\Yandex and HKLM\Software\Yandex
     (registry).
- Safety: before steps 4-6, list exactly what will be removed, then act on
  ONLY those paths. Never touch files, services, tasks, or registry keys
  outside the paths listed above. If a path does not exist, skip it.
- Report which components were actually removed («Что сделано» is justified
  here — these are real actions performed this turn).

Tools: run_powershell, registry, windows_service, filesystem, system_info, download_file,
capture_screenshot, read_clipboard, analyze_folder, search_web, scrape_url, event_log,
network, scheduled_task, wmi_query, windows_process, virtualization, reliability, windows_update,
security_status, devices, dns_config, port_listener, remote_access, change_rollback, performance,
startup_programs, credentials, system_repair, restore_point, disk_management, disk_space,
software_inventory, firewall_rules, windows_features, local_users.

Workflow for complex issues:
1. For errors/Event IDs — search_web first, then collect local evidence (event_log, reliability, network).
2. Before risky repair/write ops — prefer restore_point(create) if none in the last 24h; also change_rollback snapshot when relevant.
3. Apply fixes (dangerous actions need user confirmation in the app).
   When calling ANY tool that may mutate the system (run_powershell with write/stop/set/delete/etc.,
   registry write/delete, service start/stop/restart, filesystem write, process stop/kill,
   scheduled_task create/delete/enable/disable/run, network firewall_*, virtualization start/stop,
   download_file, change_rollback restore, system_repair, disk_management chkdsk_fix,
   disk_space cleanup, software_inventory install/upgrade/uninstall, firewall_rules mutations,
   windows_features enable/disable, local_users mutations) ALWAYS pass parameter
   "explanation": 1–2 short sentences in Russian — plain language for the user: what the action
   does and what will change on the PC. Do NOT paste the raw command as the explanation.
   Example: "Останавливает службу Windows Update, чтобы завершить зависшее обновление."
4. Report result concisely.

Specialized tools (prefer over run_powershell / generic tools when they fit):
- Disk health / SMART / chkdsk / BitLocker status → disk_management (not wmi_query or ad-hoc PowerShell).
- Free space analysis and cleanup → only disk_space (cleanup uses fixed categories; never arbitrary paths via filesystem/run_powershell).
- Install / upgrade / uninstall software → software_inventory (not winget via run_powershell).
- Firewall rule list/get/enable/disable/create/delete → firewall_rules; for adapters/DNS/ping use network.
- Optional Windows features → windows_features (-NoRestart; never reboot the machine yourself).
- Local users/groups → local_users (no password/create-user via tools).
- System Restore checkpoints → restore_point (list/status/create; rollback is manual via rstrui.exe).
- If a specialized tool exists for the task — use it instead of run_powershell.
- NEVER output BitLocker recovery keys or user passwords (bitlocker_status returns status only).

When a name, property, registry value, or setting is not found on first try:
- Do NOT conclude it does not exist after one failed search. Windows, drivers, and vendor tools often expose
  the same option under different Russian vs English labels, abbreviations, or alternate marketing names.
- Retry systematically:
  · translate the term both ways (RU ↔ EN) and search again with each variant and plausible synonyms
  · broaden the query — list all keys/properties/members, then filter by partial match
  · use registry, run_powershell, wmi_query, devices, network, dns_config as appropriate for the domain
  · search_web for how this setting maps to registry keys, PowerShell cmdlets, or driver property names
- When found, note which name variant matched. Only report "not found" after exhausting translation and
  synonym attempts plus a broad inventory scan.

For simple factual requests (time, disk space, download this URL): skip the long workflow — call the
right tool immediately and answer in one short reply.

Communication style:
- Russian only. Laconic, logical, technical. No filler ("Рад помочь", "Чем ещё помочь?" — never).
- Never use Unicode emoji or pictographs. For tone use ASCII only: :) :( :D ;) ^_^ -- ~ and parentheses.
- Status updates while working: 1 short sentence.
- Final reply: answer the request directly. No invitation to continue chatting.

Final reply format (important — the app renders your text; wrong headings look absurd):
- Default: plain prose, 2–6 sentences. Use a short bullet list for 3+ parallel facts (errors, specs,
  UI elements, tool capabilities). Do NOT wrap answers in section templates unless noted below.
- Capability / «что умеешь» answers: bullets «name — purpose» only; no «Кратко» / «Что сделано» headings.
- Never use headings «Кратко», «Что сделано», «Технические детали», «Действия» unless each heading is
  honestly justified (see below).
- «Что сделано» / past-tense action list — ONLY for changes you actually performed with tools in THIS turn
  (service restarted, file downloaded, registry written, command executed). If you only read logs, took a
  screenshot, or described an image — you did NOT "do" those things; do not use «Что сделано».
- Screenshot / clipboard image / photo analysis: say what it is in 1–2 sentences, then optional bullets
  for notable on-screen details (e.g. VPN status, error text). No «Что сделано». No «задача не
  сформулирована» and no «что нужно сделать?» when the user already asked (e.g. "что на экране",
  "проанализируй фото") — just answer.
- Diagnosis without applying fixes: 1–3 sentences cause/conclusion, then «Рекомендации:» + numbered steps
  if needed — not «Что сделано».
- «Технические детали» — only for extra depth the user would need (Event IDs, exact paths, command output).
  Skip if the main answer is enough.

Rules:
- NEVER delete existing files or directories — EXCEPT Yandex components, which are governed by the
  Yandex policy above. Disk cleanup only via disk_space(cleanup) with fixed categories (recycle_bin,
  temp_files, windows_update_cache, memory_dumps, thumbnails) — never free-form path deletion.
- Use search_web for unfamiliar errors before guessing.
- event_log: prefer presets (critical_recent, errors_last_hour, app_errors_24h, system_errors_24h).
- Prefer tools over refusal. Prefer tools over asking.
- Prefer specialized tools over run_powershell when a dedicated tool covers the request.

Paths on this machine — use these exact values, never wildcards (no C:\Users\*\Desktop):
- User profile, Desktop, and Downloads are injected at runtime in the system message.
- Renaming a Desktop shortcut: filesystem list on Desktop → filesystem move (source: ...\Name.lnk,
  destination: ...\NewName.lnk — keep the .lnk extension).
""";

    private const string ReadOnlyPromptAppendix = """

        READ-ONLY MODE IS ACTIVE (/readonly):
        - Do NOT attempt writes, downloads, service control, registry writes, or mutating PowerShell.
        - Still pursue the user's goal: diagnose with read-only tools (devices, event_log, performance, registry read,
          wmi_query, etc.) and propose exact PowerShell/registry steps — do not shrug off GUI requests as impossible.
        """;

    private readonly VeniceClient _client;
    private readonly ToolRegistry _tools;
    private readonly AgentOptions _options;
    private readonly IAgentUi _ui;
    private readonly SessionActionLog _actionLog;
    private readonly SessionUndoTracker _undoTracker;
    private readonly SessionReportCollector _reportCollector;
    private readonly List<ToolDefinition> _toolDefinitions;
    private readonly List<ChatMessage> _sessionHistory = [];
    private readonly string _basePrompt;
    private string? _cachedSystemPrompt;
    private bool _cachedSystemPromptReadOnly;

    public SessionMode SessionMode { get; set; } = SessionMode.Continuous;

    public bool ReadOnlyMode { get; set; }

    public SessionUndoTracker UndoTracker => _undoTracker;

    public SessionReportCollector ReportCollector => _reportCollector;

    public Agent(
        VeniceClient client,
        ToolRegistry tools,
        AgentOptions options,
        IAgentUi ui,
        SessionActionLog actionLog,
        SessionUndoTracker undoTracker,
        SessionReportCollector reportCollector,
        string? systemPromptOverride = null)
    {
        _client = client;
        _tools = tools;
        _options = options;
        _ui = ui;
        _actionLog = actionLog;
        _undoTracker = undoTracker;
        _reportCollector = reportCollector;
        _basePrompt = string.IsNullOrWhiteSpace(systemPromptOverride) ? BaseSystemPrompt : systemPromptOverride;
        _toolDefinitions = tools.GetDefinitions();
        ValidateToolDefinitions(_toolDefinitions);
        _client.ModelFallback += OnModelFallback;
    }

    private void OnModelFallback(string fromModel, string toModel)
    {
        _ui.Warn($"Модель {fromModel} перегружена — переключился на {toModel}.");
    }

    public void ClearSession()
    {
        _sessionHistory.Clear();
        _actionLog.Clear();
        _undoTracker.Clear();
        _reportCollector.Clear();
    }

    public Task<AgentRunResult> RunAsync(string userRequest, CancellationToken cancellationToken = default) =>
        RunRequestAsync(userRequest, cancellationToken);

    private async Task<AgentRunResult> RunRequestAsync(string userRequest, CancellationToken cancellationToken)
    {
        try
        {
            return await RunRequestCoreAsync(userRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ui.Error($"Сбой обработки запроса: {ex.Message}");
            return FailResult(ex.Message);
        }
    }

    private async Task<AgentRunResult> RunRequestCoreAsync(string userRequest, CancellationToken cancellationToken)
    {
        _client.ResetRequestCost();
        _undoTracker.BeginRequest(userRequest);

        var turnId = _actionLog.BeginTurn();
        _reportCollector.BeginTurn(turnId, userRequest);

        List<ChatMessage> messages;
        try
        {
            messages = BuildInitialMessages(userRequest);
        }
        catch (Exception ex)
        {
            _ui.Error($"Не удалось подготовить запрос: {ex.Message}");
            CompleteRequest(turnId, null);
            return FailResult(ex.Message);
        }
        var toolDefinitions = _toolDefinitions;

        string? finalAssistantText = null;
        var emptyResponseRetries = 0;
        var toolsUsed = false;

        for (var round = 1; round <= _options.MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var spinnerMessage = round == 1
                ? "Думаю…"
                : toolsUsed
                    ? $"Анализ результатов инструментов (шаг {round})…"
                    : "Повторяю ответ…";

            ChatCompletionResponse response;
            try
            {
                response = await _ui.RunBusyAsync(
                    spinnerMessage,
                    () => RequestCompletionAsync(messages, toolDefinitions, cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new VeniceApiException(
                    "Превышено время ожидания ответа Venice (3 мин). Попробуйте /clear и повторите запрос.");
            }
            catch (Exception ex) when (ex is not VeniceApiException and not OperationCanceledException)
            {
                throw new VeniceApiException($"Ошибка на шаге {round}: {ex.Message}");
            }

            var choice = response.Choices.FirstOrDefault();
            if (choice is null)
            {
                _ui.Warn("Venice вернул пустой ответ — повторяю запрос…");
                response = await _ui.RunBusyAsync(
                    "Повтор запроса…",
                    () => RequestCompletionAsync(messages, toolDefinitions, cancellationToken),
                    cancellationToken);
                choice = response.Choices.FirstOrDefault()
                    ?? throw new VeniceApiException("No response choices from Venice API.");
            }

            var assistantMessage = choice.Message;
            var assistantText = ExtractAssistantText(assistantMessage, choice.FinishReason);

            if (!string.IsNullOrWhiteSpace(assistantText))
            {
                _ui.AssistantMessage(assistantText);
            }

            messages.Add(ChatMessageCloner.CloneForStorage(assistantMessage));

            if (assistantMessage.ToolCalls is not { Count: > 0 })
            {
                if (string.IsNullOrWhiteSpace(assistantText))
                {
                    if (emptyResponseRetries < 2)
                    {
                        emptyResponseRetries++;
                        messages.Add(new ChatMessage
                        {
                            Role = "user",
                            Content = ChatContent.Text(
                                "Твой предыдущий ответ был пустым. Ответь на запрос пользователя текстом на русском.")
                        });
                        continue;
                    }

                    _ui.AssistantMessage(
                        "Модель не вернула текстовый ответ. Повторите запрос или выполните /clear.");
                    CompleteRequest(turnId, null);
                    SaveSessionHistory(messages);
                    return OkResult(null);
                }

                finalAssistantText = assistantText;
                CompleteRequest(turnId, finalAssistantText);
                SaveSessionHistory(messages);
                return OkResult(finalAssistantText);
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                _ui.Info($"Запускаю инструменты ({assistantMessage.ToolCalls.Count})…");
            }

            toolsUsed = true;

            await ExecuteToolCallsAsync(assistantMessage.ToolCalls, messages, cancellationToken);

            if (round < _options.MaxToolRounds)
            {
                _ui.Info($"Инструменты завершены — запрашиваю ответ модели (шаг {round + 1})…");
            }
        }

        if (string.IsNullOrWhiteSpace(finalAssistantText))
        {
            var synthesis = await _ui.RunBusyAsync(
                "Формирую итоговый ответ…",
                () => RequestSynthesisAsync(messages, cancellationToken),
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(synthesis))
            {
                _ui.AssistantMessage(synthesis);
                finalAssistantText = synthesis;
                messages.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = ChatContent.Text(synthesis)
                });
            }
            else
            {
                _ui.AssistantMessage(
                    $"Достигнут лимит раундов инструментов ({_options.MaxToolRounds}). " +
                    "Итоговый текстовый ответ не получен — повторите запрос.");
            }
        }
        else
        {
            _ui.Warn($"Достигнут лимит раундов инструментов ({_options.MaxToolRounds}). Завершаю работу.");
        }

        CompleteRequest(turnId, finalAssistantText);
        SaveSessionHistory(messages);
        return OkResult(finalAssistantText);
    }

    private AgentRunResult OkResult(string? text) => new()
    {
        AssistantText = text,
        Cost = _client.RequestCost
    };

    private AgentRunResult FailResult(string error) => new()
    {
        Error = error,
        Cost = _client.RequestCost
    };

    private static string? ExtractAssistantText(ChatMessage message, string? finishReason)
    {
        var text = ChatContent.ReadText(message.Content);
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        if (message.Content is { ValueKind: JsonValueKind.String })
        {
            return message.Content.Value.GetString();
        }

        if (!string.IsNullOrWhiteSpace(finishReason) &&
            finishReason.Equals("length", StringComparison.OrdinalIgnoreCase))
        {
            return "[Ответ обрезан лимитом токенов модели]";
        }

        return null;
    }

    private void CompleteRequest(int turnId, string? assistantText)
    {
        _undoTracker.CompleteRequest();
        _reportCollector.CompleteTurn(
            turnId,
            assistantText,
            _client.RequestCost,
            _undoTracker.LastCompletedUndoSnapshotId);

        if (_undoTracker.HasUndoPoint)
        {
            _ui.Info("Изменения применены. Откат последнего запроса: команда /undo");
        }
    }

    private async Task ExecuteToolCallsAsync(
        IReadOnlyList<ToolCall> toolCalls,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (toolCalls.Count > 1 && TryBuildParallelBatch(toolCalls, out var batch))
        {
            await ExecuteParallelBatchAsync(batch, messages, cancellationToken);
            return;
        }

        foreach (var toolCall in toolCalls)
        {
            await ExecuteOneToolCallSequentialAsync(toolCall, messages, cancellationToken);
        }
    }

    private bool TryBuildParallelBatch(IReadOnlyList<ToolCall> toolCalls, out List<PreparedCall> batch)
    {
        batch = new List<PreparedCall>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            JsonElement arguments;
            try
            {
                arguments = ParseArguments(toolCall.Function.Arguments);
            }
            catch (JsonException)
            {
                batch = [];
                return false;
            }

            var toolName = toolCall.Function.Name;
            if (!IsParallelSafeToolCall(toolName, arguments, ReadOnlyMode))
            {
                batch = [];
                return false;
            }

            batch.Add(new PreparedCall(toolCall, toolName, arguments));
        }

        return batch.Count == toolCalls.Count && batch.Count > 1;
    }

    internal static bool IsParallelSafeToolCall(string toolName, JsonElement arguments, bool readOnlyMode)
    {
        if (readOnlyMode && !ReadOnlyGuard.IsToolAllowed(toolName, arguments))
        {
            return false;
        }

        if (toolName.Equals("local_users", StringComparison.OrdinalIgnoreCase) &&
            LocalUsersSafety.TryGetHardBlockReason(arguments, out _))
        {
            return false;
        }

        if (DangerousActionGuard.RequiresConfirmation(toolName, arguments) ||
            DangerousActionGuard.RequiresUndoSnapshot(toolName, arguments))
        {
            return false;
        }

        return true;
    }

    private async Task ExecuteParallelBatchAsync(
        List<PreparedCall> batch,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var item in batch)
        {
            _ui.ToolCall(item.ToolName, item.ToolCall.Function.Arguments);
        }

        var results = new ToolResult[batch.Count];
        await _ui.RunBusyAsync(
            $"Запускаю инструменты ({batch.Count})…",
            async () =>
            {
                var tasks = new Task[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    var index = i;
                    var item = batch[i];
                    tasks[i] = Task.Run(async () =>
                    {
                        results[index] = await _tools.ExecuteAsync(
                            item.ToolName,
                            item.Arguments,
                            cancellationToken);
                    }, cancellationToken);
                }

                await Task.WhenAll(tasks);
                return 0;
            },
            cancellationToken);

        for (var i = 0; i < batch.Count; i++)
        {
            AppendToolOutcome(batch[i].ToolCall, batch[i].ToolName, batch[i].Arguments, results[i], messages, needsUndoSnapshot: false);
        }
    }

    private async Task ExecuteOneToolCallSequentialAsync(
        ToolCall toolCall,
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var toolName = toolCall.Function.Name;
        _ui.ToolCall(toolName, toolCall.Function.Arguments);

        ToolResult result;
        JsonElement arguments;
        try
        {
            arguments = ParseArguments(toolCall.Function.Arguments);
        }
        catch (JsonException ex)
        {
            result = ToolResult.Fail(
                $"Некорректные аргументы инструмента (ожидался JSON): {ex.Message}");
            _ui.ToolResult(toolName, result);
            _actionLog.Record(toolName, null, false, result.Output);
            messages.Add(BuildToolMessage(toolCall, result));
            return;
        }

        if (ReadOnlyMode && !ReadOnlyGuard.IsToolAllowed(toolName, arguments))
        {
            result = ToolResult.Fail(ReadOnlyGuard.BlockedMessage(toolName));
            _ui.ToolResult(toolName, result);
            _actionLog.Record(toolName, ExtractAction(arguments), false, result.Output);
            messages.Add(BuildToolMessage(toolCall, result));
            return;
        }

        // local_users hard safety (current session user SID / last Administrators member)
        // must Fail before confirm UI.
        if (toolName.Equals("local_users", StringComparison.OrdinalIgnoreCase) &&
            LocalUsersSafety.TryGetHardBlockReason(arguments, out var localUsersBlock))
        {
            result = ToolResult.Fail(localUsersBlock);
            _ui.ToolResult(toolName, result);
            _actionLog.Record(toolName, ExtractAction(arguments), false, result.Output);
            messages.Add(BuildToolMessage(toolCall, result));
            return;
        }

        var isDangerous = DangerousActionGuard.RequiresConfirmation(toolName, arguments);
        var needsUndoSnapshot = DangerousActionGuard.RequiresUndoSnapshot(toolName, arguments);
        if (isDangerous)
        {
            var approved = await _ui.ConfirmDangerousActionAsync(
                DangerousActionGuard.DescribeDetailed(toolName, arguments),
                cancellationToken);

            if (!approved)
            {
                result = ToolResult.Fail("Действие отменено пользователем.");
                _ui.ToolResult(toolName, result);
                _actionLog.Record(toolName, ExtractAction(arguments), false, result.Output);
                messages.Add(BuildToolMessage(toolCall, result));
                return;
            }

            if (needsUndoSnapshot)
            {
                var snapshot = await _ui.RunBusyAsync(
                    "Снимок системы для отката…",
                    () => Task.FromResult(_undoTracker.EnsureSnapshotBeforeMutation(toolName)),
                    cancellationToken);
                if (!snapshot.Success)
                {
                    _ui.Warn($"Не удалось создать снимок системы: {snapshot.Message}");
                }
                else if (snapshot.IsNew)
                {
                    _ui.Info(
                        $"Снимок системы для /undo (службы, задачи, реестр): {snapshot.SnapshotId}");
                }
            }
        }

        result = await ExecuteToolAsync(toolName, arguments, cancellationToken);
        AppendToolOutcome(toolCall, toolName, arguments, result, messages, needsUndoSnapshot);
    }

    private void AppendToolOutcome(
        ToolCall toolCall,
        string toolName,
        JsonElement arguments,
        ToolResult result,
        List<ChatMessage> messages,
        bool needsUndoSnapshot)
    {
        _ui.ToolResult(toolName, result);
        _actionLog.Record(
            toolName,
            ExtractAction(arguments),
            result.Success,
            result.Output);
        messages.Add(BuildToolMessage(toolCall, result));

        if (needsUndoSnapshot && result.Success)
        {
            _undoTracker.RecordMutation();
        }

        if (result.HasImages)
        {
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = ChatContent.VisionMultiple(
                    BuildVisionPrompt(toolName, result),
                    result.GetImages())
            });
        }
    }

    private readonly record struct PreparedCall(ToolCall ToolCall, string ToolName, JsonElement Arguments);

    private Task<ToolResult> ExecuteToolAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var spinnerMessage = GetToolSpinnerMessage(toolName, arguments);
        return _ui.RunBusyAsync(
            spinnerMessage,
            () => _tools.ExecuteAsync(toolName, arguments, cancellationToken),
            cancellationToken);
    }

    private static string GetPowerShellSpinnerMessage(JsonElement arguments)
    {
        var timeout = 120;
        if (arguments.TryGetProperty("timeout_seconds", out var timeoutProp) &&
            timeoutProp.TryGetInt32(out var requested))
        {
            timeout = Math.Clamp(requested, 5, 600);
        }

        return $"Выполняю PowerShell (до {timeout} сек)…";
    }

    private static string GetToolSpinnerMessage(string toolName, JsonElement arguments) =>
        toolName.ToLowerInvariant() switch
        {
            "download_file" => "Скачиваю файл…",
            "run_powershell" => GetPowerShellSpinnerMessage(arguments),
            "change_rollback" => "Работаю со снимками системы…",
            "system_repair" => "Системный ремонт Windows…",
            "windows_update" => "Проверяю обновления Windows…",
            "event_log" => "Читаю журнал событий…",
            "security_status" => "Проверяю безопасность…",
            "devices" => "Сканирую устройства…",
            "performance" => "Собираю метрики производительности…",
            "reliability" => "Читаю журнал надёжности…",
            _ => $"Выполняю {toolName}…"
        };

    private List<ChatMessage> BuildInitialMessages(string userRequest)
    {
        var prompt = GetSystemPrompt();
        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = ChatContent.Text(prompt) }
        };

        if (SessionMode == SessionMode.Continuous)
        {
            messages.AddRange(ChatMessageCloner.CloneAll(_sessionHistory));
        }

        messages.Add(new() { Role = "user", Content = ChatContent.Text(userRequest) });
        return messages;
    }

    private static ChatMessage BuildToolMessage(ToolCall toolCall, ToolResult result) =>
        new()
        {
            Role = "tool",
            ToolCallId = toolCall.Id,
            Name = toolCall.Function.Name,
            Content = ChatContent.Text(FormatToolResult(result))
        };

    private void SaveSessionHistory(List<ChatMessage> messages)
    {
        if (SessionMode != SessionMode.Continuous)
        {
            return;
        }

        _sessionHistory.Clear();
        _sessionHistory.AddRange(SessionHistoryCompressor.Compress(messages.Skip(1).ToList()));
    }

    private static string? ExtractAction(JsonElement arguments) =>
        arguments.TryGetProperty("action", out var action) && action.ValueKind == JsonValueKind.String
            ? action.GetString()
            : null;

    private static JsonElement ParseArguments(string argumentsJson)
    {
        var json = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim();

        if (json.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException("tool arguments look like an error string, not JSON");
        }

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static string FormatToolResult(ToolResult result)
    {
        const int maxChars = 12_000;
        var output = result.Success ? result.Output : $"ERROR: {result.Output}";
        return output.Length <= maxChars
            ? output
            : output[..maxChars] + "\n... [обрезано для контекста API]";
    }

    private static string BuildVisionPrompt(string toolName, ToolResult result)
    {
        var imageCount = result.GetImages().Count;
        return toolName switch
        {
            "capture_screenshot" =>
                "Вот скриншот экрана пользователя. Опиши, что на нём видно, и используй это для решения задачи.",
            "read_clipboard" when imageCount == 1 =>
                "Вот изображение из буфера обмена. Опиши, что на нём, и используй для решения задачи.",
            "read_clipboard" =>
                "Вот изображения из буфера обмена. Просмотри их и используй вместе с текстовым отчётом инструмента.",
            "analyze_folder" =>
                $"Просмотри {imageCount} изображений из папки. Опиши, что на них, и учти при анализе содержимого каталога.",
            _ => $"Просмотри прикреплённые изображения ({imageCount}) и используй их для решения задачи."
        };
    }

    private static void ValidateToolDefinitions(List<ToolDefinition> tools)
    {
        var duplicates = tools
            .GroupBy(t => t.Function.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate local tool names: {string.Join(", ", duplicates)}");
        }

        if (tools.Any(t => t.Function.Name.Equals("web_search", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Local tool \"web_search\" conflicts with Venice native search. Use \"search_web\" instead.");
        }
    }

    private async Task<ChatCompletionResponse> RequestCompletionAsync(
        List<ChatMessage> messages,
        List<ToolDefinition> toolDefinitions,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            return await _client.CreateChatCompletionAsync(
                _options.Model,
                messages,
                toolDefinitions,
                "auto",
                BuildVeniceParameters(_options),
                timeoutCts.Token);
        }
        catch (InvalidOperationException ex)
        {
            if (_sessionHistory.Count > 0)
            {
                _sessionHistory.Clear();
                _ui.Warn("История сессии была повреждена и сброшена. Повторите запрос.");
            }

            throw new VeniceApiException($"Не удалось отправить запрос: {ex.Message}");
        }
    }

    private async Task<string?> RequestSynthesisAsync(
        List<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        var synthesisMessages = new List<ChatMessage>(messages)
        {
            new()
            {
                Role = "user",
                Content = ChatContent.Text(
                    "Сформируй итоговый текстовый ответ пользователю на русском по результатам инструментов. " +
                    "Без вызова инструментов.")
            }
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        var response = await _client.CreateChatCompletionAsync(
            _options.Model,
            synthesisMessages,
            tools: null,
            toolChoice: "none",
            BuildVeniceParameters(_options),
            timeoutCts.Token);

        var choice = response.Choices.FirstOrDefault();
        return choice is null
            ? null
            : ExtractAssistantText(choice.Message, choice.FinishReason);
    }

    private string GetSystemPrompt()
    {
        if (_cachedSystemPrompt is not null && _cachedSystemPromptReadOnly == ReadOnlyMode)
        {
            return _cachedSystemPrompt;
        }

        _cachedSystemPromptReadOnly = ReadOnlyMode;
        _cachedSystemPrompt = _basePrompt
            + BuildMachinePathsPrompt()
            + (ReadOnlyMode ? ReadOnlyPromptAppendix : string.Empty);
        return _cachedSystemPrompt;
    }

    private static string BuildMachinePathsPrompt()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = DownloadPaths.DesktopDirectory;
        var downloads = DownloadPaths.DownloadsDirectory;

        return $"""

            Machine paths (authoritative — copy exactly into tool arguments):
            - User profile: {userProfile}
            - Desktop: {desktop}
            - Downloads: {downloads}
            """;
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