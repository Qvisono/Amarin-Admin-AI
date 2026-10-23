using System.Collections.Concurrent;

namespace Amarin.Core;

/// <summary>
/// Откуда ход берёт агентов, которым чат может написать (1.37.0): по настройке
/// <see cref="AppSettings.AgentMemory"/>.
/// </summary>
/// <remarks>
/// Одна на программу, из корня композиции. В режиме «до конца ответа» каждый ход получает новый
/// список, как в 1.36.0. В остальных список общий на чат и живёт здесь, по id чата: объект
/// <see cref="ChatSession"/> заново читается с диска при каждом открытии чата, и список на нём
/// пропал бы при первом же переходе в другой чат. В режиме «сохранять в чате» список ещё и
/// переносится в <see cref="ChatSession.Agents"/> после каждого ответа агента и поднимается оттуда
/// после перезапуска.
/// </remarks>
internal sealed class AgentMemoryBook
{
    /// <summary>Сколько разговоров держит файл чата: самые свежие. Каждый — сжатая переписка.</summary>
    public const int Limit = 20;

    private readonly ConcurrentDictionary<string, AgentConversations> _chats = new(StringComparer.Ordinal);

    /// <summary>Агенты для нового хода в этом чате.</summary>
    public AgentConversations For(ChatSession session, AgentMemory mode)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (mode == AgentMemory.Answer || string.IsNullOrEmpty(session.Id))
        {
            Forget(session.Id);
            DropStored(session);
            return new AgentConversations();
        }

        var conversations = _chats.GetOrAdd(session.Id, _ => mode == AgentMemory.Chat ? Restore(session) : new AgentConversations());
        if (mode == AgentMemory.Chat)
        {
            // Объект чата у каждого хода свой (прочитан с диска заново), поэтому и запись — в него.
            conversations.Changed = () => Store(session, conversations);
        }
        else
        {
            conversations.Changed = null;
            DropStored(session);
        }

        return conversations;
    }

    /// <summary>Чат удалён: его агентов больше никто не позовёт.</summary>
    public void Forget(string? chatId)
    {
        if (!string.IsNullOrEmpty(chatId))
        {
            _chats.TryRemove(chatId, out _);
        }
    }

    /// <summary>Другой профиль — другие чаты.</summary>
    public void Clear() => _chats.Clear();

    private static AgentConversations Restore(ChatSession session)
    {
        var conversations = new AgentConversations();
        List<StoredAgentConversation> stored;
        lock (session.Gate)
        {
            stored = session.Agents is { } agents ? [.. agents] : [];
        }

        foreach (var agent in stored.Where(agent => !string.IsNullOrWhiteSpace(agent.Id)))
        {
            conversations.Keep(new AgentConversation
            {
                Id = agent.Id,
                Prompt = agent.Prompt,
                Tier = agent.Tier,
                History = agent.History,
                UpdatedAt = agent.UpdatedAt
            });
        }

        return conversations;
    }

    private static void Store(ChatSession session, AgentConversations conversations)
    {
        var latest = conversations.All
            .OrderByDescending(agent => agent.UpdatedAt)
            .Take(Limit)
            .Select(agent => new StoredAgentConversation
            {
                Id = agent.Id,
                Prompt = agent.Prompt,
                Tier = agent.Tier,
                History = [.. agent.History],
                UpdatedAt = agent.UpdatedAt
            })
            .ToList();

        // Под замком чата: файл пишет фоновая очередь, и она не должна застать список посередине.
        lock (session.Gate)
        {
            session.Agents = latest;
        }
    }

    /// <summary>
    /// Режим не «сохранять в чате»: прежние разговоры из файла снимаются и при следующей записи
    /// чата уходят с диска — человек выбрал, чтобы они там не лежали.
    /// </summary>
    private static void DropStored(ChatSession session)
    {
        lock (session.Gate)
        {
            session.Agents = null;
        }
    }
}
