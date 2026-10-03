namespace Amarin.Core;

/// <summary>Раздел боковой панели, куда бросили перетаскиваемые чаты.</summary>
internal enum ChatDropKind
{
    Pinned,
    Folder,

    /// <summary>Общий список: группы по датам или «Все».</summary>
    Loose,
    Archive
}

internal readonly record struct ChatDropTarget(ChatDropKind Kind, string? FolderId = null)
{
    public static ChatDropTarget Pinned => new(ChatDropKind.Pinned);

    public static ChatDropTarget Loose => new(ChatDropKind.Loose);

    public static ChatDropTarget Archive => new(ChatDropKind.Archive);

    public static ChatDropTarget Folder(string id) => new(ChatDropKind.Folder, id);
}

/// <summary>Перетаскиваемый чат: где он лежит сейчас.</summary>
internal readonly record struct ChatDropSource(string Id, bool Pinned, ChatPlacement Placement);

/// <summary>Что поменять, чтобы чаты оказались в цели. Пустые списки — ничего.</summary>
internal sealed record ChatDropPlan(
    IReadOnlyList<string> Pin,
    IReadOnlyList<string> Unpin,
    IReadOnlyList<string> Move,
    string? FolderId,
    IReadOnlyList<string> Archive,
    IReadOnlyList<string> Unarchive)
{
    public bool IsEmpty =>
        Pin.Count == 0 && Unpin.Count == 0 && Move.Count == 0 && Archive.Count == 0 && Unarchive.Count == 0;
}

/// <summary>
/// Правила перетаскивания чатов между разделами боковой панели.
/// </summary>
/// <remarks>
/// Раскладка (<see cref="ChatListLayout.Build"/>) показывает закреплённый чат наверху, даже если
/// он в папке, а архивный — только в архиве. Поэтому бросок обязан снять то, что перебило бы
/// цель: чат, брошенный в папку, но оставшийся закреплённым, в папке не появился бы. Папка
/// закреплённого чата, наоборот, сохраняется — открепив его, человек найдёт чат там же, где оставил.
/// </remarks>
internal static class ChatDrop
{
    public static ChatDropPlan Plan(IEnumerable<ChatDropSource> chats, ChatDropTarget target)
    {
        var pin = new List<string>();
        var unpin = new List<string>();
        var move = new List<string>();
        var archive = new List<string>();
        var unarchive = new List<string>();

        foreach (var chat in chats)
        {
            var placement = chat.Placement;
            switch (target.Kind)
            {
                case ChatDropKind.Pinned:
                    AddIf(pin, chat.Id, !chat.Pinned);
                    AddIf(unarchive, chat.Id, placement.Archived);
                    break;

                case ChatDropKind.Folder:
                    AddIf(unpin, chat.Id, chat.Pinned);
                    AddIf(move, chat.Id, placement.FolderId != target.FolderId);
                    AddIf(unarchive, chat.Id, placement.Archived);
                    break;

                case ChatDropKind.Loose:
                    AddIf(unpin, chat.Id, chat.Pinned);
                    AddIf(move, chat.Id, placement.FolderId is not null);
                    AddIf(unarchive, chat.Id, placement.Archived);
                    break;

                case ChatDropKind.Archive:
                    AddIf(archive, chat.Id, !placement.Archived);
                    break;
            }
        }

        var folderId = target.Kind == ChatDropKind.Folder ? target.FolderId : null;
        return new ChatDropPlan(pin, unpin, move, folderId, archive, unarchive);
    }

    /// <summary>
    /// Разложить брошенные чаты: закрепление в описи, папка и архив в раскладке.
    /// </summary>
    /// <returns><c>false</c> — бросок ничего не меняет (чат уже там), и список трогать незачем.</returns>
    /// <remarks>
    /// До 1.30.0 это делал обработчик окна. Брошенное в свёрнутую папку пропало бы из вида, поэтому
    /// папка раскрывается и показывает его.
    /// </remarks>
    public static bool Apply(ChatStore store, ChatOrganizer organizer, IReadOnlyList<string> ids, ChatDropTarget target)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(organizer);
        ArgumentNullException.ThrowIfNull(ids);

        var pinned = store.List()
            .Where(entry => entry.IsPinned)
            .Select(entry => entry.Id)
            .ToHashSet(StringComparer.Ordinal);
        var plan = Plan(ids.Select(id => new ChatDropSource(id, pinned.Contains(id), organizer.PlacementOf(id))), target);
        if (plan.IsEmpty)
        {
            return false;
        }

        foreach (var id in plan.Unpin)
        {
            store.SetPinned(id, false);
        }

        foreach (var id in plan.Pin)
        {
            store.SetPinned(id, true);
        }

        if (plan.Unarchive.Count > 0)
        {
            organizer.SetArchived(plan.Unarchive, false);
        }

        if (plan.Archive.Count > 0)
        {
            organizer.SetArchived(plan.Archive, true);
        }

        if (plan.Move.Count > 0)
        {
            organizer.MoveToFolder(plan.Move, plan.FolderId);
        }

        if (plan.FolderId is { } folder)
        {
            organizer.SetCollapsed(folder, false);
        }

        return true;
    }

    private static void AddIf(List<string> list, string id, bool condition)
    {
        if (condition)
        {
            list.Add(id);
        }
    }
}
