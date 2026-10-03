using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Точечное обновление боковой панели — без окна.</summary>
public sealed class ChatListPatchTests
{
    private static readonly DateTime Day = new(2026, 10, 3, 12, 0, 0);

    [Fact]
    public void An_unchanged_list_keeps_every_row()
    {
        var nodes = List(collapsed: true);
        var slots = Plan(nodes, List(collapsed: true));

        Assert.Equal(nodes.Count, slots.Count);
        Assert.All(slots, slot => Assert.Equal(slot.NewIndex, slot.OldIndex));
        Assert.All(slots, slot => Assert.Equal(ChatListMotion.None, slot.Motion));
    }

    [Fact]
    public void Expanding_a_folder_keeps_the_rest_and_grows_its_rows()
    {
        var before = List(collapsed: true);
        var after = List(collapsed: false);
        var slots = Plan(before, after);

        // Заголовок папки — тот же элемент: свёрнута ли она, в штамп не входит.
        var header = slots.Single(slot => after[slot.NewIndex] is ChatListFolder);
        Assert.True(header.OldIndex >= 0);

        var entered = slots.Where(slot => slot.Motion == ChatListMotion.Enter).Select(slot => Id(after[slot.NewIndex])).ToList();
        Assert.Equal(["in1", "in2"], entered);

        // Всё прочее — прежние строки.
        Assert.All(slots.Where(slot => slot.Motion == ChatListMotion.None), slot => Assert.True(slot.OldIndex >= 0));
    }

    [Fact]
    public void Collapsing_a_folder_lets_its_rows_leave_right_under_the_header()
    {
        var before = List(collapsed: false);
        var after = List(collapsed: true);
        var slots = Plan(before, after).ToList();

        var header = slots.FindIndex(slot => slot.NewIndex >= 0 && after[slot.NewIndex] is ChatListFolder);
        Assert.Equal(ChatListMotion.Leave, slots[header + 1].Motion);
        Assert.Equal(ChatListMotion.Leave, slots[header + 2].Motion);
        Assert.Equal(["in1", "in2"], slots.Where(slot => slot.Motion == ChatListMotion.Leave).Select(slot => Id(before[slot.OldIndex])));
        Assert.All(slots.Where(slot => slot.Motion == ChatListMotion.Leave), slot => Assert.Equal(-1, slot.NewIndex));
    }

    [Fact]
    public void A_renamed_chat_is_rebuilt_and_only_it()
    {
        var before = List(collapsed: true);
        var after = List(collapsed: true, rename: "loose2");
        var slots = Plan(before, after);

        var rebuilt = slots.Where(slot => slot.OldIndex < 0).Select(slot => Id(after[slot.NewIndex])).ToList();
        Assert.Equal(["loose2"], rebuilt);
        Assert.All(slots, slot => Assert.Equal(ChatListMotion.None, slot.Motion));
    }

    [Fact]
    public void A_chat_moved_into_a_folder_does_not_play_the_expand_motion()
    {
        // Папка раскрыта и там, и там: чат переехал из «Сегодня» — он просто стоит на новом месте.
        var folder = new ChatFolder { Id = "f", Name = "Work", Collapsed = false };
        List<ChatListNode> before =
        [
            new ChatListGroup("S.ChatList.Folders"),
            new ChatListFolder(folder, 0),
            new ChatListGroup("S.ChatList.Today"),
            Chat("moved")
        ];
        List<ChatListNode> after =
        [
            new ChatListGroup("S.ChatList.Folders"),
            new ChatListFolder(folder, 1),
            Chat("moved") with { Nested = true }
        ];

        var slots = Plan(before, after);
        Assert.DoesNotContain(slots, slot => slot.Motion != ChatListMotion.None);
    }

    [Fact]
    public void Archive_opens_and_closes_like_a_folder()
    {
        List<ChatListNode> closed = [new ChatListGroup("S.ChatList.Today"), Chat("a"), new ChatListArchive(1, false)];
        List<ChatListNode> open = [new ChatListGroup("S.ChatList.Today"), Chat("a"), new ChatListArchive(1, true), Chat("old") with { Nested = true }];

        Assert.Contains(Plan(closed, open), slot => slot.Motion == ChatListMotion.Enter);
        Assert.Contains(Plan(open, closed), slot => slot.Motion == ChatListMotion.Leave);
    }

    [Fact]
    public void From_nothing_everything_is_built_without_motion()
    {
        var after = List(collapsed: false);
        var slots = Plan([], after);

        Assert.Equal(after.Count, slots.Count);
        Assert.All(slots, slot => Assert.Equal(-1, slot.OldIndex));
        Assert.All(slots, slot => Assert.Equal(ChatListMotion.None, slot.Motion));
    }

    [Fact]
    public void Tags_and_cost_are_part_of_what_a_row_shows()
    {
        var plain = Chat("x");
        var tagged = new ChatListChat(plain.Entry, [new ChatTag { Id = "t", Color = "Status.Danger" }]);
        Assert.NotEqual(ChatListPatch.StampOf(plain), ChatListPatch.StampOf(tagged));

        var dearer = new ChatListChat(new ChatIndexEntry { Id = "x", Title = "x", UpdatedAt = Day, TotalCost = 1.5m }, []);
        Assert.NotEqual(ChatListPatch.StampOf(plain), ChatListPatch.StampOf(dearer));
    }

    /// <summary>
    /// Хранилище правит запись описи на месте, и прежняя раскладка держит ту же запись. Снимок
    /// обязан помнить, какой строка была нарисована, — иначе переименованная строка сошла бы за
    /// прежнюю, и на экране осталось бы старое имя.
    /// </summary>
    [Fact]
    public void A_row_whose_entry_was_changed_in_place_is_still_rebuilt()
    {
        var entry = new ChatIndexEntry { Id = "x", Title = "Old", UpdatedAt = Day };
        List<ChatListNode> nodes = [new ChatListGroup("S.ChatList.Today"), new ChatListChat(entry, [])];
        var shown = ChatListPatch.Snapshot(nodes);

        entry.Title = "New";
        var slots = ChatListPatch.Plan(shown, ChatListPatch.Snapshot(nodes));

        Assert.Equal(-1, slots.Single(slot => slot.NewIndex == 1).OldIndex);
    }

    private static IReadOnlyList<ChatListSlot> Plan(IReadOnlyList<ChatListNode> before, IReadOnlyList<ChatListNode> after) =>
        ChatListPatch.Plan(ChatListPatch.Snapshot(before), ChatListPatch.Snapshot(after));

    private static List<ChatListNode> List(bool collapsed, string? rename = null)
    {
        var folder = new ChatFolder { Id = "f", Name = "Work", Collapsed = collapsed };
        var nodes = new List<ChatListNode>
        {
            new ChatListGroup("S.ChatList.Pinned"),
            Chat("pinned"),
            new ChatListGroup("S.ChatList.Folders"),
            new ChatListFolder(folder, 2)
        };
        if (!collapsed)
        {
            nodes.Add(Chat("in1") with { Nested = true });
            nodes.Add(Chat("in2") with { Nested = true });
        }

        nodes.Add(new ChatListGroup("S.ChatList.Today"));
        nodes.Add(Chat("loose1"));
        nodes.Add(Chat("loose2", rename == "loose2" ? "Renamed" : null));
        return nodes;
    }

    private static ChatListChat Chat(string id, string? title = null) =>
        new(new ChatIndexEntry { Id = id, Title = title ?? id, UpdatedAt = Day }, []);

    private static string Id(ChatListNode node) => ((ChatListChat)node).Entry.Id;
}
