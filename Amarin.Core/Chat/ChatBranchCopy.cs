using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Ветка в новый чат (1.37.0): переписка до выбранного ответа включительно — отдельным чатом.
/// </summary>
/// <remarks>
/// Не <see cref="ChatBranches.Fork"/>: тот прячет вариант внутри того же чата, а здесь рождается
/// новый. Копия — через тот же JSON, которым чат ложится на диск, как у переноса в другой профиль
/// (<c>ChatTransfer.Snapshot</c>): ручное копирование полей забыло бы новое поле, а общие объекты
/// сообщений поздняя запись цены правила бы в обоих чатах сразу. Что остаётся у исходного чата:
/// <list type="bullet">
/// <item>спрятанные варианты — ветка несёт только показанную линию;</item>
/// <item>цены — деньги уже записаны на исходный чат, и вторая копия удвоила бы его стоимость в
/// списке;</item>
/// <item>отметки отложенных задач у вызовов — задача пишет итог в свой чат;</item>
/// <item>разговоры с агентами, сводка и задача расписания.</item>
/// </list>
/// </remarks>
internal static class ChatBranchCopy
{
    /// <returns>Новый чат; <c>null</c> — такого ответа нет или копию не прочесть.</returns>
    public static ChatSession? FromAnswer(ChatSession source, string answerId, string title, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(source);

        string json;
        int index;
        lock (source.Gate)
        {
            index = source.Messages.FindIndex(message => message.Id == answerId);
            if (index < 0 || !source.Messages[index].Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            json = JsonSerializer.Serialize(source, AppJson.Options);
        }

        ChatSession? copy;
        try
        {
            copy = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (copy is null || index >= copy.Messages.Count)
        {
            return null;
        }

        // Граница истории модели — по ленте до обреза: ApiCut считает ходы по всей ленте.
        var keep = index + 1;
        var apiCut = ChatSessionEdit.ApiCut(copy, keep);
        copy.Messages.RemoveRange(keep, copy.Messages.Count - keep);
        copy.ApiMessages.RemoveRange(apiCut, copy.ApiMessages.Count - apiCut);

        foreach (var message in copy.Messages)
        {
            message.Variants = null;
            message.VariantIndex = 0;
            ForgetMoney(message);
            foreach (var call in message.ToolRounds.SelectMany(round => round.Calls))
            {
                call.Deferred = null;
            }
        }

        copy.Id = Guid.NewGuid().ToString("N");
        copy.Title = title;
        copy.CreatedAt = now;
        copy.UpdatedAt = now;
        copy.ScheduleJobId = null;
        copy.Summary = null;
        copy.SummaryStale = false;
        copy.TitleCost = null;
        copy.Agents = null;
        copy.IsTemporary = source.IsTemporary;

        if (copy.LastPromptTokensApiIndex > apiCut)
        {
            copy.LastPromptTokens = 0;
            copy.LastPromptTokensApiIndex = 0;
        }

        // Сводка сжатия описывает начало истории; кончается оно позже обреза — описывает уже
        // не то, и история уходит модели целиком (как после «Вернуть историю»).
        if (copy.CompactedThrough > apiCut)
        {
            copy.CompactedThrough = 0;
            copy.CompactSummary = null;
            copy.CompactFingerprint = null;
            copy.CompactedAfterMessageId = null;
        }

        _ = ChatEngine.CloseInterruptedReplies(copy);
        return copy;
    }

    private static void ForgetMoney(ChatDisplayMessage message)
    {
        message.Cost = null;
        message.ModelCost = null;
        message.RouterCost = null;
        message.TitleCost = null;
        message.SummaryCost = null;
        message.GuardCost = null;
        message.CompactCost = null;
    }
}
