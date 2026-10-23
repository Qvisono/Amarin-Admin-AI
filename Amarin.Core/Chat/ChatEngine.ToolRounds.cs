using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

internal sealed partial class ChatEngine
{
    /// <summary>
    /// Ловит повторное рисование картинки, уже сделанной в этом ходе. Модель видит свой результат,
    /// решает, что он не совсем тот, и зовёт generate_image снова с перефразированным запросом —
    /// а вставляет в ответ одну из двух, хотя человек платит за обе. По-настоящему другая картинка
    /// почти не делит слов с прежней и проходит. Возвращает ссылку для повторного использования
    /// или null.
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

                // Вызовы одного раунда идут параллельно; дублем можно счесть только вызов из
                // завершённого раунда — соседний ещё ничего не нарисовал.
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

    /// <summary>Доля слов меньшего запроса, общих для обоих.</summary>
    private static double Overlap(HashSet<string> left, HashSet<string> right)
    {
        var smaller = Math.Min(left.Count, right.Count);
        return smaller == 0 ? 0 : left.Count(right.Contains) / (double)smaller;
    }

    /// <summary>Сколько прежних реплик человека идёт к последней.</summary>
    private const int EarlierWords = 2;

    /// <summary>
    /// Что человек написал в этом чате сам: последняя реплика, а перед ней — до двух прежних и
    /// начало ответа ассистента, на который он отвечает. Null — сообщений человека нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Текст, а не сообщение для модели: в нём нет цитат и вложений, только слова человека, —
    /// их и сверяет SynGuard с тем, что собрались сделать. Одной последней реплики мало: на «да,
    /// отправь» защитнику неоткуда было взять, что и кому, и он запрещал отправку, о которой
    /// человек сам попросил. Реплика без истории за ней остаётся голым текстом, как раньше.
    /// </para>
    /// <para>
    /// Сообщение отложенной задачи писал не человек, а модель чата, когда задачу ставили: слова
    /// человека у него — в метке задачи, и только если сошлась печать (1.36.0). Прежние такие
    /// сообщения в историю слов не идут вовсе.
    /// </para>
    /// </remarks>
    internal static string? HumanWordsOf(ChatSession session)
    {
        var messages = session.Messages;
        var lastIndex = messages.FindLastIndex(message => message.Role == "user");
        if (lastIndex < 0)
        {
            return null;
        }

        var last = messages[lastIndex];
        if (last.Deferred is { } mark)
        {
            return mark.HumanWords is { Length: > 0 } words ? words : null;
        }

        if (last.Text is not { Length: > 0 } latest)
        {
            return null;
        }

        var earlier = messages.Take(lastIndex)
            .Where(message => message is { Role: "user", Deferred: null, Text.Length: > 0 })
            .TakeLast(EarlierWords)
            .Select(message => "User (earlier): " + TextClip.Clip(message.Text, 1000, 0))
            .ToList();
        var answered = lastIndex > 0 && messages[lastIndex - 1] is { Role: "assistant", Text.Length: > 0 } reply
            ? "Assistant (the message the user answered): " + TextClip.Clip(reply.Text, 400, 0)
            : null;
        if (earlier.Count == 0 && answered is null)
        {
            return latest;
        }

        var lines = new List<string>(earlier);
        if (answered is not null)
        {
            lines.Add(answered);
        }

        lines.Add("User (latest): " + TextClip.Clip(latest, 1000, 0));
        return string.Join("\n", lines);
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
        string? forcedAgentTier = null,
        bool planAllowed = true)
    {
        var results = new ToolResult[toolRound.Calls.Count];
        var tasks = new Task[toolRound.Calls.Count];
        var humanWords = HumanWordsOf(session);
        var conversations = VeniceTurnScope.Current?.Agents;
        Task<GateDecision>[] decisions;
        try
        {
            decisions = await DecideRoundAsync(toolRound, messages, session, assistant, observer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Отмена во время проверки или вопроса: ответ «отменено» всё равно ляжет на каждый
            // вызов ниже — без него вызовы в истории модели остались бы без пары.
            decisions = toolRound.Calls
                .Select(_ => Task.FromCanceled<GateDecision>(
                    cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken(true)))
                .ToArray();
        }
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
                GateDecision? decision = null;
                var outcome = AuditOutcome.Refused;
                var started = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Каждый вызов ждёт только своего решения: чтения исполняются, пока запись
                    // ждёт человека.
                    decision = await decisions[index].ConfigureAwait(false);
                    var arguments = decision.Arguments;
                    if (!decision.Allowed)
                    {
                        result = ToolResult.Fail(decision.Refusal ?? ToolGate.DeniedReply);
                    }
                    else if (RedrawOf(assistant, toolRound, call) is { } already)
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
                            ChatTitle = session.Title,
                            ForcedTier = forcedAgentTier,
                            PlanAllowed = planAllowed,
                            HumanWords = humanWords,
                            Conversations = conversations,
                            TemporaryChat = session.IsTemporary
                        }))
                        {
                            started = true;
                            result = await _tools.ExecuteAsync(call.Name, arguments, cancellationToken)
                                .ConfigureAwait(false);
                            outcome = result.Success ? AuditOutcome.Ok : AuditOutcome.Failed;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    result = ToolResult.Fail("Действие отменено пользователем.");
                    outcome = AuditOutcome.Cancelled;
                }
                catch (JsonException ex)
                {
                    result = ToolResult.Fail(
                        $"Некорректные аргументы инструмента (ожидался JSON): {ex.Message}");
                    outcome = started ? AuditOutcome.Failed : AuditOutcome.Refused;
                }
                catch (Exception ex)
                {
                    result = ToolResult.Fail(ex.Message);
                    outcome = started ? AuditOutcome.Failed : AuditOutcome.Refused;
                }

                results[index] = result;
                Audit(session, call, toolRound, decision, outcome, result);
                call.Success = result.Success;
                call.Status = result.Success ? ToolCallStatus.Done : ToolCallStatus.Failed;
                call.ResultPreview = ChatToolPreview.Summarize(result);
                call.ResultText = ChatToolPreview.ForJournal(result);
                call.TruncatedForModel = ChatToolPreview.IsTruncatedForApi(result);
                call.SavedFiles = [.. result.GetFiles()];
                call.Instruction = result.Instruction;
                call.Deferred = result.Deferred;
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
            // Отдельные сбои записаны на вызовах.
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

            // Картинки остаются на записи — их нарисует лента — и уходят модели отдельным
            // сообщением с изображениями: сообщение роли «tool» несёт только текст, и иначе
            // картинки рисовались бы и выбрасывались обеими половинами программы. У каждой своя
            // ссылка, которой модель ставит её в ответ.
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

            // Картинку, которую модель прочла из файла человека, ему не нужно показывать в ответ:
            // он её и так видит. Ручка всё равно даётся — по ней картинку можно сохранить или вставить.
            var placement = call.Name.Equals("read_file", StringComparison.OrdinalIgnoreCase)
                ? $"Это картинка из прочитанного файла, она показана тебе, а не человеку. В ответ ставь её разметкой ![описание]({handles[0]}) только если человек просил её показать."
                : handles.Count == 1
                    ? $"Вставь это изображение в ответ разметкой ![описание]({handles[0]}) там, где оно уместно."
                    : "Вставь эти изображения разметкой ![описание](handle): " + string.Join(", ", handles);

            var visionMessage = new ChatMessage
            {
                Role = "user",
                Content = ChatContent.ToolImages(call.Name, placement, handled)
            };
            messages.Add(visionMessage);
            session.ApiMessages.Add(ChatMessageCloner.CloneForStorage(visionMessage));
        }

        observer.OnToolsChanged(assistant);
    }

    /// <summary>
    /// Строка журнала аудита про вызов чата. Удачные чтения журнал отбросит сам.
    /// </summary>
    /// <param name="decision">Решение шлюза; null — до него не дошло (отмена, битые аргументы).</param>
    private void Audit(
        ChatSession session,
        ToolCallRecord call,
        ToolRound round,
        GateDecision? decision,
        AuditOutcome outcome,
        ToolResult result)
    {
        if (_options.Audit is not { } audit)
        {
            return;
        }

        var approval = decision?.Approval ?? ApprovalSource.NotRequired;
        var guard = approval == ApprovalSource.SynGuardHuman || decision?.GuardFlagged == true
            ? AuditGuard.Flagged
            : round.GuardOutcome switch
            {
                null => AuditGuard.Off,
                SynGuardOutcome.Failed => AuditGuard.Failed,
                SynGuardOutcome.Unparsed => AuditGuard.Unparsed,
                _ => AuditGuard.Safe
            };

        audit.Record(
            new AuditOrigin(session.Id, session.Title, null),
            call.Id,
            call.Name,
            call.ArgumentsJson,
            decision?.Effect ?? ToolEffect.Write,
            outcome,
            approval,
            guard,
            result.Output);
    }

    /// <summary>
    /// Решения шлюза по всему раунду — до исполнения первого вызова.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь, на вызывающем потоке и по порядку: разбор аргументов, проверка шлюза, один вопрос
    /// SynGuard на раунд с записью, потом вопросы человеку — в очередь они встают синхронно, без
    /// ожидания, поэтому окна идут в порядке вызовов, а не в порядке, в каком их успели
    /// дождаться параллельные задачи.
    /// </para>
    /// <para>
    /// Не бросает, кроме отмены хода: разбор, отказ, сбой проверки — всё это решение по вызову,
    /// и ответ «tool» на каждый вызов попадает в историю модели.
    /// </para>
    /// </remarks>
    private async Task<Task<GateDecision>[]> DecideRoundAsync(
        ToolRound toolRound,
        IReadOnlyList<ChatMessage> messages,
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        var settings = _settings();
        var calls = toolRound.Calls;
        var checks = new GateCheck?[calls.Count];
        var decisions = new Task<GateDecision>[calls.Count];

        for (var i = 0; i < calls.Count; i++)
        {
            try
            {
                checks[i] = ToolGate.Check(calls[i].Name, ParseArguments(calls[i].ArgumentsJson), settings);
            }
            catch (JsonException ex)
            {
                decisions[i] = Task.FromResult(GateDecision.Refuse(
                    $"Некорректные аргументы инструмента (ожидался JSON): {ex.Message}",
                    ApprovalSource.NotRequired,
                    default,
                    ToolEffect.Write));
            }
        }

        var guard = new GuardMarks(calls.Count);
        if (checks.Any(check => check is { Refusal: null, Effect: ToolEffect.Write }))
        {
            await AskGuardAsync(toolRound, messages, session, assistant, observer, settings, checks, guard, cancellationToken)
                .ConfigureAwait(false);
        }

        var label = Loc.Get("S.Confirm.ChatLabel");
        var confirmations = _confirmations;
        for (var i = 0; i < calls.Count; i++)
        {
            if (decisions[i] is not null || checks[i] is not { } check)
            {
                continue;
            }

            if (guard.Refused[i])
            {
                decisions[i] = Task.FromResult(GateDecision.Refuse(
                    SynGuard.BlockedReply(check.ToolName), guard.Blocked[i] ? ApprovalSource.NotRequired : ApprovalSource.SynGuardHuman,
                    check.Arguments, check.Effect) with { GuardFlagged = true });
                continue;
            }

            if (check.Effect == ToolEffect.Write && confirmations is null)
            {
                decisions[i] = Task.FromResult(GateDecision.Refuse(
                    Loc.Get("S.Gate.NoConfirmation"), ApprovalSource.NotRequired, check.Arguments, check.Effect));
                continue;
            }

            // Без очереди вопросов спросить некого: и чтение, которое режим велит подтверждать,
            // отклоняется — так же, как запись строкой выше.
            decisions[i] = ToolGate.DecideAsync(
                ToolGate.ApplyNormalReview(check, settings, guard.WithoutApproval[i]),
                (info, token) => confirmations is null
                    ? Task.FromResult(new ConfirmationAnswer(false, ApprovalSource.NotRequired))
                    : confirmations.ConfirmDetailedAsync(label, info, session.Id, token),
                guard.Approved[i],
                cancellationToken);
        }

        return decisions;
    }

    /// <summary>Что SynGuard и человек решили по каждому вызову раунда.</summary>
    /// <param name="Blocked">Запрещён явным «небезопасно» Decisions, без вопроса; входит и в <c>Refused</c>.</param>
    /// <param name="WithoutApproval">Обычный режим: можно ли без вопроса; null — этого вопроса не было.</param>
    private sealed record GuardMarks(bool[] Approved, bool[] Refused, bool[] Blocked, bool?[] WithoutApproval)
    {
        public GuardMarks(int count)
            : this(new bool[count], new bool[count], new bool[count], new bool?[count])
        {
        }
    }

    /// <summary>
    /// SynGuard про раунд чата: один запрос на весь раунд, про помеченные вызовы и вызовы без
    /// вердикта — вопрос человеку. Тот же порядок, что у агента, — прежде инструменты чата
    /// SynGuard не видел.
    /// </summary>
    private async Task AskGuardAsync(
        ToolRound toolRound,
        IReadOnlyList<ChatMessage> messages,
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        AppSettings settings,
        IReadOnlyList<GateCheck?> checks,
        GuardMarks marks,
        CancellationToken cancellationToken)
    {
        // Поиск и чтение страниц защитнику не отдаются, как и у агента (SynGuard.NeverJudged).
        // judged — номера вызовов раунда, о которых спрашиваем; ответ защитника идёт по ним.
        var all = toolRound.Calls;
        var judged = Enumerable.Range(0, all.Count).Where(i => !SynGuard.NeverJudged(all[i].Name)).ToList();
        if (judged.Count == 0)
        {
            return;
        }

        using var lease = Guard is null ? SynGuardLease.Build(settings, _options, _venice.ResolveModelInfo) : null;
        var guard = Guard ?? lease?.Check;
        if (guard is null)
        {
            return;
        }

        var calls = judged.Select(i => all[i]).ToList();
        var task = HumanWordsOf(session) ?? "";
        var report = await guard(
                new SynGuardRequest(task, calls.Select(call => new SynGuardCall(call.Name, call.ArgumentsJson ?? "")).ToList(),
                    "The chat assistant proposes these tool calls to carry out the user's current request.",
                    HumanWords: task,
                    Earlier: SynGuard.EarlierCalls(messages)),
                cancellationToken)
            .ConfigureAwait(false);

        // В чате нет AgentRunScope, поэтому цену защиты кладём на ответ сами — ApplyCosts её
        // прибавит, а разбивка покажет строкой «Guard».
        if (report.Cost is { } cost)
        {
            assistant.GuardCost = (assistant.GuardCost ?? VeniceCost.Zero).Add(cost);
        }

        toolRound.GuardOutcome = report.Outcome == SynGuardOutcome.Checked && report.Safe.Count < calls.Count
            ? SynGuardOutcome.Unparsed
            : report.Outcome;
        observer.OnToolsChanged(assistant);

        for (var j = 0; j < calls.Count; j++)
        {
            var i = judged[j];
            if (report.WithoutApproval is { } approvals)
            {
                marks.WithoutApproval[i] = j < approvals.Count && approvals[j];
            }

            // Без вердикта спрашивается только запись — как у агента.
            if (report.IsSafe(j) || (report.IsUnchecked(j) && checks[i] is not { Effect: ToolEffect.Write }))
            {
                continue;
            }

            if (report.IsBlocked(j) || _confirmations is null)
            {
                marks.Refused[i] = true;
                marks.Blocked[i] = report.IsBlocked(j);
                continue;
            }

            var answer = await _confirmations.ConfirmDetailedAsync(
                    Loc.Get("S.Confirm.ChatLabel"),
                    SynGuard.DescribeBlock(calls[j].Name, calls[j].ArgumentsJson, report.IsUnchecked(j)),
                    session.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            (answer.Approved ? marks.Approved : marks.Refused)[i] = true;
        }
    }

    private static ToolRound CreateRound(IReadOnlyList<ToolCall> toolCalls)
    {
        var round = new ToolRound
        {
            InfoLine = EngineLines.RunningTools
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
}
