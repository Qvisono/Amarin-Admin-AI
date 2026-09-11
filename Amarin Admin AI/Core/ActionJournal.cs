namespace Amarin.Core;

/// <summary>One thing the model did to this computer.</summary>
/// <param name="ChatId">Chat the action happened in, so the journal can jump to it.</param>
/// <param name="ChatTitle">Raw stored title - still the untranslated default marker for an
/// unnamed chat, which only the UI knows how to show.</param>
/// <param name="AgentName">Display name of the nested agent that ran it; null for the chat itself.</param>
public sealed record JournalEntry(
    string ChatId,
    string ChatTitle,
    DateTime StartedAt,
    TimeSpan Duration,
    string ToolName,
    string ArgumentsJson,
    string ResultPreview,
    string ResultText,
    bool Success,
    ToolCallStatus Status,
    string? AgentName)
{
    /// <summary>True when the time came from the owning message rather than the call itself.</summary>
    public bool TimeIsApproximate { get; init; }
}

/// <summary>
/// Every tool call the model ever made, flattened out of the chats that hold them.
/// </summary>
/// <remarks>
/// There is no separate log on disk, and deliberately so: the calls already live inside the
/// conversations, and a second copy would be one more thing to keep in step and one more place a
/// deleted chat could leak out of. The cost is that "all chats" means reading every chat file,
/// which is why <see cref="Collect"/> is written to be called off the UI thread.
/// </remarks>
internal static class ActionJournal
{
    /// <summary>Actions of one loaded session, newest first.</summary>
    public static IReadOnlyList<JournalEntry> FromSession(ChatSession? session)
    {
        var entries = new List<JournalEntry>();
        if (session is not null)
        {
            Collect(session, entries);
        }

        return Sort(entries);
    }

    /// <summary>
    /// Actions of every chat on disk, newest first. Skips chats it cannot read instead of
    /// failing the lot: one corrupted file must not hide the rest of the history.
    /// </summary>
    public static IReadOnlyList<JournalEntry> Collect(ChatStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var entries = new List<JournalEntry>();
        foreach (var item in store.List())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (store.TryLoad(item.Id) is { } session)
            {
                Collect(session, entries);
            }
        }

        return Sort(entries);
    }

    private static List<JournalEntry> Sort(List<JournalEntry> entries)
    {
        entries.Sort((left, right) => right.StartedAt.CompareTo(left.StartedAt));
        return entries;
    }

    private static void Collect(ChatSession session, List<JournalEntry> entries)
    {
        var title = session.Title;
        foreach (var message in session.Messages)
        {
            foreach (var round in message.ToolRounds)
            {
                foreach (var call in round.Calls)
                {
                    Collect(session.Id, title, message, call, agentName: null, entries);
                }
            }
        }
    }

    private static void Collect(
        string chatId,
        string chatTitle,
        ChatDisplayMessage message,
        ToolCallRecord call,
        string? agentName,
        List<JournalEntry> entries)
    {
        // Calls recorded before the field existed have no clock of their own. The message they
        // belong to does, and it is right to within one turn — good enough to keep the order.
        var approximate = call.StartedAt == default;
        entries.Add(new JournalEntry(
            chatId,
            chatTitle,
            approximate ? message.CreatedAt : call.StartedAt,
            call.Duration,
            call.Name,
            call.ArgumentsJson,
            call.ResultPreview,
            string.IsNullOrWhiteSpace(call.ResultText) ? call.ResultPreview : call.ResultText,
            call.Success,
            call.Status,
            agentName)
        {
            TimeIsApproximate = approximate
        });

        if (call.NestedAgent is not { } agent)
        {
            return;
        }

        // An agent's own tool calls are the ones that touched the machine; the init_agent call
        // that spawned it did nothing but delegate. Both are listed, the nested ones labelled.
        var name = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.ModelId : agent.DisplayName;
        foreach (var round in agent.ToolRounds)
        {
            foreach (var nested in round.Calls)
            {
                Collect(chatId, chatTitle, message, nested, name, entries);
            }
        }
    }
}
