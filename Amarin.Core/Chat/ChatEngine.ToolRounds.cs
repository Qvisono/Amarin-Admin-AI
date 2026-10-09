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
        Task<GateDecision>[] decisions;
        try
        {
            decisions = await DecideRoundAsync(toolRound, session, assistant, observer, cancellationToken)
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
                            ForcedTier = forcedAgentTier
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
        var guard = approval == ApprovalSource.SynGuardHuman
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

        var guardApproved = new bool[calls.Count];
        var guardRefused = new bool[calls.Count];
        if (checks.Any(check => check is { Refusal: null, Effect: ToolEffect.Write }))
        {
            await AskGuardAsync(toolRound, session, assistant, observer, settings, guardApproved, guardRefused,
                cancellationToken).ConfigureAwait(false);
        }

        var label = Loc.Get("S.Confirm.ChatLabel");
        var confirmations = _confirmations;
        for (var i = 0; i < calls.Count; i++)
        {
            if (decisions[i] is not null || checks[i] is not { } check)
            {
                continue;
            }

            if (guardRefused[i])
            {
                decisions[i] = Task.FromResult(GateDecision.Refuse(
                    SynGuard.BlockedReply(check.ToolName), ApprovalSource.SynGuardHuman, check.Arguments, check.Effect));
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
                check,
                (info, token) => confirmations is null
                    ? Task.FromResult(new ConfirmationAnswer(false, ApprovalSource.NotRequired))
                    : confirmations.ConfirmDetailedAsync(label, info, session.Id, token),
                guardApproved[i],
                cancellationToken);
        }

        return decisions;
    }

    /// <summary>
    /// SynGuard про раунд чата: один запрос на весь раунд, про помеченные вызовы — вопрос
    /// человеку. Тот же порядок, что у агента, — прежде инструменты чата SynGuard не видел.
    /// </summary>
    private async Task AskGuardAsync(
        ToolRound toolRound,
        ChatSession session,
        ChatDisplayMessage assistant,
        IChatTurnObserver observer,
        AppSettings settings,
        bool[] approved,
        bool[] refused,
        CancellationToken cancellationToken)
    {
        using var lease = Guard is null ? SynGuardLease.Build(settings, _options, _venice.ResolveModelInfo) : null;
        var guard = Guard ?? lease?.Check;
        if (guard is null)
        {
            return;
        }

        var calls = toolRound.Calls;
        var task = session.Messages.LastOrDefault(message => message.Role == "user")?.Text ?? "";
        var report = await guard(
                new SynGuardRequest(task, calls.Select(call => new SynGuardCall(call.Name, call.ArgumentsJson ?? "")).ToList()),
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

        for (var i = 0; i < calls.Count; i++)
        {
            if (report.IsSafe(i))
            {
                continue;
            }

            if (_confirmations is null)
            {
                refused[i] = true;
                continue;
            }

            var answer = await _confirmations.ConfirmDetailedAsync(
                    Loc.Get("S.Confirm.ChatLabel"),
                    SynGuard.DescribeBlock(calls[i].Name, calls[i].ArgumentsJson),
                    session.Id,
                    cancellationToken)
                .ConfigureAwait(false);
            (answer.Approved ? approved : refused)[i] = true;
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
