using System.Collections.Concurrent;

namespace Amarin.Core;

/// <summary>
/// Агент, которому чат может написать после его отчёта (1.36.0).
/// </summary>
internal sealed class AgentConversation
{
    private int _busy;

    /// <summary>Короткий id, тот же, что у записи в <see cref="AgentRegistry"/>: по нему агента называет модель.</summary>
    public required string Id { get; init; }

    /// <summary>Задание, с которым агента запустили: по нему судит защитник и в продолжении.</summary>
    public required string Prompt { get; init; }

    /// <summary>Уровень, на котором агент ответил последним: <c>fast</c>, <c>lite</c>, <c>heavy</c> или <c>bypass</c>.</summary>
    public required string Tier { get; set; }

    /// <summary>Переписка агента без системного промпта, сжатая, как у продолжаемого сеанса.</summary>
    public required IReadOnlyList<ChatMessage> History { get; set; }

    /// <summary>Когда агент отвечал последним (1.37.0): в файле чата остаются самые свежие.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Занят ли агент: два вызова к одному агенту в одном раунде шли бы по одной истории и
    /// затёрли бы друг друга.
    /// </summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _busy, 0);

    /// <summary>
    /// История без отказов — с чего продолжает модель сильнее (<c>agent_refused</c>): без последнего
    /// ответа агента и без каждого ответа с <c>i_refuse</c>, вместе с ответами на их вызовы.
    /// </summary>
    /// <remarks>
    /// Все отказы, а не только последний. После разговора с чатом (<c>message_agent</c>) в истории
    /// лежал и первый отказ: модель сильнее видела чужое «задача не выполняется», а защитник читал
    /// его среди уже сделанного и запрещал её первый же шаг. Последний ответ уходит целиком, даже если
    /// отказ в нём написан словами, а не вызовом: из-за него задачу и отдают модели сильнее.
    /// </remarks>
    public IReadOnlyList<ChatMessage> WithoutRefusals()
    {
        var lastReply = History.Count;
        for (var i = History.Count - 1; i >= 0; i--)
        {
            if (string.Equals(History[i].Role, "assistant", StringComparison.Ordinal))
            {
                lastReply = i;
                break;
            }
        }

        // Вызовы отказных ответов: ответы на них уходят вместе с ними, иначе API отверг бы ответ
        // инструмента без своего вызова.
        var refusedCalls = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<ChatMessage>(lastReply);
        foreach (var message in History.Take(lastReply))
        {
            if (message is { Role: "assistant", ToolCalls: { } calls } && Refusals.TryFind(calls, out _))
            {
                refusedCalls.UnionWith(calls.Select(call => call.Id));
                continue;
            }

            if (message is { Role: "tool", ToolCallId: { } id } && refusedCalls.Contains(id))
            {
                continue;
            }

            kept.Add(message);
        }

        return kept;
    }
}

/// <summary>
/// Агенты, которым чат может написать после их отчёта.
/// </summary>
/// <remarks>
/// Висит на ходе (<see cref="VeniceTurnContext.Agents"/>), а откуда он взят, решает настройка
/// <see cref="AgentMemory"/> (1.37.0, <see cref="AgentMemoryBook"/>): новый на каждый ответ, как
/// было в 1.36.0, общий на чат, пока программа открыта, или поднятый из файла чата.
/// </remarks>
internal sealed class AgentConversations
{
    private readonly ConcurrentDictionary<string, AgentConversation> _byId = new(StringComparer.OrdinalIgnoreCase);
    private int _counter;

    /// <summary>
    /// Зовётся после каждого запомненного ответа агента — книга переносит разговоры в файл чата.
    /// </summary>
    public Action? Changed { get; set; }

    public IReadOnlyCollection<AgentConversation> All => _byId.Values.ToList();

    public void Keep(AgentConversation conversation)
    {
        _byId[conversation.Id] = conversation;
        Changed?.Invoke();
    }

    /// <summary>Агент ответил ещё раз: уровень и переписка — свежие.</summary>
    public void Update(AgentConversation conversation, string tier, IReadOnlyList<ChatMessage> history)
    {
        conversation.Tier = tier;
        conversation.History = history;
        conversation.UpdatedAt = DateTime.UtcNow;
        Changed?.Invoke();
    }

    public AgentConversation? Find(string? id) =>
        !string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id.Trim(), out var conversation) ? conversation : null;

    /// <summary>Id, когда реестра агентов нет (в тестах): свой счёт.</summary>
    public string NextId() => "a" + Interlocked.Increment(ref _counter);

    /// <summary>
    /// Id нового агента: следующий номер счётчика, который в этом чате ещё не занят.
    /// </summary>
    /// <remarks>
    /// Счётчик реестра идёт с <c>a1</c> на каждом запуске, а разговоры из файла чата (1.37.0) помнят
    /// прежние номера: новый агент с тем же id затёр бы чужую переписку, и чат писал бы не тому.
    /// </remarks>
    public string Allocate(Func<string> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        while (true)
        {
            var id = next();
            if (!_byId.ContainsKey(id))
            {
                return id;
            }
        }
    }
}

/// <summary>
/// Разговор с агентом в файле чата (1.37.0, режим <see cref="AgentMemory.Chat"/>).
/// </summary>
/// <remarks>
/// Отдельной записью, а не <see cref="AgentConversation"/>: у того замок занятости и правила
/// продолжения, а в файл ложатся одни данные.
/// </remarks>
public sealed class StoredAgentConversation
{
    public string Id { get; set; } = "";

    public string Prompt { get; set; } = "";

    public string Tier { get; set; } = "";

    public List<ChatMessage> History { get; set; } = [];

    public DateTime UpdatedAt { get; set; }
}
