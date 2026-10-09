using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Отложенные задачи в чате: поручение агенту и запись прогона команды.</summary>
internal sealed partial class ChatEngine
{
    /// <summary>
    /// Поручение агенту от отложенной задачи — в её чате: сообщение с меткой задачи вместо
    /// человека, дальше — как команда <c>/agent</c>: агент делает, модель чата пишет отчёт.
    /// </summary>
    /// <remarks>
    /// Без плана: план ждёт одобрения человека, а прогон по сроку идёт, когда его может и не быть
    /// рядом, — агент с планом ждал бы вечно. Запись в систему по-прежнему спрашивается через шлюз:
    /// вопрос встанет в окне, а не одобрится сам.
    /// </remarks>
    /// <returns>Итог по ответу, которым ход закончился: его статус и цена.</returns>
    public async Task<DeferredOutcome> RunDeferredAgentAsync(
        ChatSession session,
        DeferredTask task,
        TimeSpan? late,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(task);
        var briefing = DeferredChat.Briefing(task, late);
        var user = DeferredChat.Placed(session, task, late, task.Text, briefing, observer);
        await RunAgentCommandAsync(session, task.Text, briefing, complexity: "", observer, cancellationToken, placed: user, planAllowed: false)
            .ConfigureAwait(false);
        return DeferredChat.OutcomeAfter(session, user);
    }
}

/// <summary>Прогоны отложенных задач в переписке: сообщение с меткой задачи и ответ с итогом.</summary>
internal static class DeferredChat
{
    /// <summary>Что модель узнаёт о задаче: что поставили, когда, насколько опоздало и что сделать.</summary>
    public static string Briefing(DeferredTask task, TimeSpan? late)
    {
        var text = new StringBuilder()
            .Append("[Scheduled task \"").Append(task.Title).Append("\" set in this chat on ")
            .Append(task.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" has come due");
        if (late is { } lateness)
        {
            text.Append(", ").Append(DeferredTimeParser.Span(lateness)).Append(" late because the PC or the program was off");
        }

        return text.Append(". Do it now; nobody may be watching, so report plainly what was done and what was not.]\n")
            .Append(task.Text)
            .ToString();
    }

    /// <summary>Ставит в переписку сообщение задачи и её запись в историю модели.</summary>
    public static ChatDisplayMessage Placed(
        ChatSession session,
        DeferredTask task,
        TimeSpan? late,
        string display,
        string forModel,
        IChatTurnObserver observer)
    {
        var now = DateTime.Now;
        var user = new ChatDisplayMessage
        {
            Role = "user",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            Text = display,
            Deferred = new DeferredMark(task.Id, task.Title, task.SnoozedUntilUtc ?? task.NextDueUtc ?? DateTime.UtcNow, late)
        };

        lock (session.Gate)
        {
            session.Messages.Add(user);
            session.ApiMessages.Add(ChatEngine.AgentCommandTurn(forModel));
            session.UpdatedAt = now;
        }

        observer.OnUserAppended(user);
        return user;
    }

    /// <summary>
    /// Итог хода по ответу после сообщения задачи: удался ли он, сколько стоил, начало отчёта.
    /// </summary>
    /// <remarks>
    /// Ответа нет или он не закрылся — неудача: ход оборвался раньше, чем модель что-то сказала.
    /// </remarks>
    internal static DeferredOutcome OutcomeAfter(ChatSession session, ChatDisplayMessage user)
    {
        ChatDisplayMessage? answer;
        lock (session.Gate)
        {
            var index = session.Messages.IndexOf(user);
            answer = index < 0 ? null : session.Messages.Skip(index + 1).LastOrDefault(message => message.Role == "assistant");
        }

        if (answer is null)
        {
            return new DeferredOutcome(false, Loc.Get("S.Deferred.Result.NoReply"), 0m, session.Id);
        }

        var cost = answer.Cost?.Usd ?? 0m;
        return answer.Status == AssistantStatus.Complete
            ? new DeferredOutcome(true, null, cost, session.Id, Head(answer.Text))
            : new DeferredOutcome(false, Head(answer.Text) is { Length: > 0 } error ? error : Loc.Get("S.Deferred.Result.NoReply"), cost, session.Id);
    }

    /// <summary>Начало отчёта для карточки и списка задач: первая непустая строка, до 200 знаков.</summary>
    internal static string Head(string? text)
    {
        var line = (text ?? "").Split('\n').Select(part => part.Trim()).FirstOrDefault(part => part.Length > 0) ?? "";
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    /// <summary>
    /// Прогон без модели — команда или возврат программ: сообщение задачи, ответ с блоком
    /// инструмента и итогом, и пара записей в истории модели, чтобы продолживший разговор знал,
    /// что произошло.
    /// </summary>
    /// <param name="tool">Имя в блоке инструмента: <c>run_powershell</c> у команды.</param>
    /// <param name="arguments">Что показать в блоке: скрипт команды.</param>
    public static async Task<DeferredOutcome> RecordAsync(
        ChatSession session,
        DeferredTask task,
        TimeSpan? late,
        string tool,
        object arguments,
        Func<CancellationToken, Task<DeferredOutcome>> execute,
        Func<DeferredOutcome, string> summary,
        IChatTurnObserver observer,
        CancellationToken cancellationToken)
    {
        Placed(session, task, late, task.Title, Briefing(task, late), observer);
        var call = new ToolCallRecord
        {
            Id = "deferred_" + Guid.NewGuid().ToString("N")[..8],
            Name = tool,
            ArgumentsJson = JsonSerializer.Serialize(arguments),
            Status = ToolCallStatus.Running,
            StartedAt = DateTime.Now
        };
        var assistant = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.Now,
            Status = AssistantStatus.Streaming,
            ToolRounds = [new ToolRound { Calls = [call] }]
        };
        lock (session.Gate)
        {
            session.Messages.Add(assistant);
        }

        observer.OnAssistantStarted(assistant);
        observer.OnToolsChanged(assistant);

        DeferredOutcome outcome;
        try
        {
            outcome = await execute(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            call.Status = ToolCallStatus.Failed;
            assistant.Status = AssistantStatus.Cancelled;
            observer.OnToolsChanged(assistant);
            observer.OnAssistantCancelled(assistant);
            throw;
        }

        call.Status = outcome.Success ? ToolCallStatus.Done : ToolCallStatus.Failed;
        call.Success = outcome.Success;
        call.ResultPreview = outcome.Success ? (outcome.Output ?? "").Split('\n')[0] : outcome.Error ?? "";
        call.ResultText = outcome.Output ?? outcome.Error ?? "";
        call.Duration = DateTime.Now - call.StartedAt;

        var text = summary(outcome);
        lock (session.Gate)
        {
            assistant.Text = text;
            assistant.Status = outcome.Success ? AssistantStatus.Complete : AssistantStatus.Error;
            session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text(text) });
            session.UpdatedAt = DateTime.Now;
        }

        observer.OnToolsChanged(assistant);
        observer.OnAssistantText(assistant);
        observer.OnAssistantCompleted(assistant);
        return outcome with { ChatId = session.Id };
    }
}
