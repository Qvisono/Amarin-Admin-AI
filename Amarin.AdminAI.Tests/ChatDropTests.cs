using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Правила перетаскивания чатов между разделами боковой панели.</summary>
public sealed class ChatDropTests
{
    private static ChatDropSource Chat(string id, bool pinned = false, string? folder = null, bool archived = false) =>
        new(id, pinned, new ChatPlacement { FolderId = folder, Archived = archived });

    private static ChatDropPlan Plan(ChatDropTarget target, params ChatDropSource[] chats) => ChatDrop.Plan(chats, target);

    [Fact]
    public void A_loose_chat_dropped_on_a_folder_moves_into_it()
    {
        var plan = Plan(ChatDropTarget.Folder("f1"), Chat("a"));

        Assert.Equal(["a"], plan.Move);
        Assert.Equal("f1", plan.FolderId);
        Assert.Empty(plan.Unpin);
        Assert.Empty(plan.Unarchive);
    }

    [Fact]
    public void A_pinned_chat_dropped_on_a_folder_is_unpinned_or_it_would_stay_on_top()
    {
        var plan = Plan(ChatDropTarget.Folder("f1"), Chat("a", pinned: true));

        Assert.Equal(["a"], plan.Unpin);
        Assert.Equal(["a"], plan.Move);
    }

    [Fact]
    public void A_pinned_chat_already_in_that_folder_is_only_unpinned()
    {
        var plan = Plan(ChatDropTarget.Folder("f1"), Chat("a", pinned: true, folder: "f1"));

        Assert.Equal(["a"], plan.Unpin);
        Assert.Empty(plan.Move);
    }

    [Fact]
    public void An_archived_chat_dropped_on_a_folder_leaves_the_archive()
    {
        var plan = Plan(ChatDropTarget.Folder("f1"), Chat("a", archived: true));

        Assert.Equal(["a"], plan.Unarchive);
        Assert.Equal(["a"], plan.Move);
    }

    [Fact]
    public void Pinning_keeps_the_folder_so_unpinning_returns_the_chat_there()
    {
        var plan = Plan(ChatDropTarget.Pinned, Chat("a", folder: "f1"));

        Assert.Equal(["a"], plan.Pin);
        Assert.Empty(plan.Move);
        Assert.Null(plan.FolderId);
    }

    [Fact]
    public void Pinning_an_archived_chat_takes_it_out_of_the_archive()
    {
        var plan = Plan(ChatDropTarget.Pinned, Chat("a", archived: true));

        Assert.Equal(["a"], plan.Pin);
        Assert.Equal(["a"], plan.Unarchive);
    }

    [Fact]
    public void The_loose_list_unpins_and_takes_the_chat_out_of_its_folder()
    {
        var plan = Plan(ChatDropTarget.Loose, Chat("a", pinned: true, folder: "f1"));

        Assert.Equal(["a"], plan.Unpin);
        Assert.Equal(["a"], plan.Move);
        Assert.Null(plan.FolderId);
    }

    [Fact]
    public void Archiving_does_not_touch_the_pin_or_the_folder()
    {
        var plan = Plan(ChatDropTarget.Archive, Chat("a", pinned: true, folder: "f1"));

        Assert.Equal(["a"], plan.Archive);
        Assert.Empty(plan.Unpin);
        Assert.Empty(plan.Move);
    }

    [Fact]
    public void Dropping_a_chat_where_it_already_lies_changes_nothing()
    {
        Assert.True(Plan(ChatDropTarget.Folder("f1"), Chat("a", folder: "f1")).IsEmpty);
        Assert.True(Plan(ChatDropTarget.Pinned, Chat("a", pinned: true)).IsEmpty);
        Assert.True(Plan(ChatDropTarget.Loose, Chat("a")).IsEmpty);
        Assert.True(Plan(ChatDropTarget.Archive, Chat("a", archived: true)).IsEmpty);
    }

    [Fact]
    public void A_selection_is_planned_chat_by_chat()
    {
        var plan = Plan(
            ChatDropTarget.Folder("f2"),
            Chat("a"),
            Chat("b", pinned: true, folder: "f2"),
            Chat("c", folder: "f1", archived: true));

        Assert.Equal(["a", "c"], plan.Move);
        Assert.Equal(["b"], plan.Unpin);
        Assert.Equal(["c"], plan.Unarchive);
        Assert.Equal("f2", plan.FolderId);
    }
}
