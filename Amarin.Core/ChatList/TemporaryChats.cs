namespace Amarin.Core;

/// <summary>
/// Временные чаты профиля (1.37.0): живут только в памяти этого окна.
/// </summary>
/// <remarks>
/// Хранилище их не видит вовсе (<see cref="ChatStore.Save"/> пропускает временные), поэтому и
/// строки списка, и открытие чата по id берутся отсюда. Сколько они живут, решает окно по
/// настройке <see cref="AppSettings.TemporaryChatsUntilClose"/>; закрытие программы и смена
/// профиля их не переживают. Список трогает только поток окна.
/// </remarks>
internal sealed class TemporaryChats
{
    private readonly Dictionary<string, ChatSession> _chats = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Ids => [.. _chats.Keys];

    /// <summary>Запоминает временный чат; обычный сюда не попадает.</summary>
    public void Keep(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.IsTemporary && !string.IsNullOrEmpty(session.Id))
        {
            _chats[session.Id] = session;
        }
    }

    public ChatSession? Find(string? id) =>
        !string.IsNullOrEmpty(id) && _chats.TryGetValue(id, out var session) ? session : null;

    public bool Contains(string? id) => !string.IsNullOrEmpty(id) && _chats.ContainsKey(id);

    public bool Remove(string? id) => !string.IsNullOrEmpty(id) && _chats.Remove(id);

    public void Clear() => _chats.Clear();

    /// <summary>
    /// Строки для списка чатов. Только с сообщениями: пустой временный чат в списке не нужен, как
    /// не нужен там и обычный новый, пока в нём ничего не написано.
    /// </summary>
    public IReadOnlyList<ChatIndexEntry> Entries() =>
    [
        .. _chats.Values
            .Where(session => session.Messages.Count > 0)
            .Select(session => new ChatIndexEntry
            {
                Id = session.Id,
                Title = session.Title,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                TotalCost = ChatCost.Total(session),
                IsTemporary = true
            })
    ];
}
