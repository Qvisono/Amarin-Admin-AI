using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Упорядочивание чатов (D5): файл раскладки, раскладка списка и новые поля описи.
/// </summary>
public sealed class ChatOrganizerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-organize-" + Guid.NewGuid().ToString("N"));

    public ChatOrganizerTests() => Directory.CreateDirectory(Path.Combine(_root, "chats"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly DateTime Today = new(2026, 9, 30);

    private static ChatIndexEntry Entry(string id, DateTime updated, bool pinned = false, string? title = null, decimal cost = 0) => new()
    {
        Id = id,
        Title = title ?? id,
        UpdatedAt = updated,
        CreatedAt = updated.AddDays(-1),
        IsPinned = pinned,
        TotalCost = cost
    };

    private static List<string> Rows(IEnumerable<ChatListNode> nodes) =>
        nodes.OfType<ChatListChat>().Select(chat => chat.Entry.Id).ToList();

    private static List<string> Shape(IEnumerable<ChatListNode> nodes) => nodes.Select(node => node switch
    {
        ChatListGroup group => "#" + group.TitleKey,
        ChatListFolder folder => "folder:" + folder.Folder.Name + "/" + folder.Count,
        ChatListArchive archive => "archive/" + archive.Count,
        ChatListChat chat => chat.Entry.Id,
        _ => "?"
    }).ToList();

    // ───────────────────────── файл раскладки ─────────────────────────

    [Fact]
    public void Folders_tags_and_placements_survive_a_restart()
    {
        var organizer = new ChatOrganizer(_root);
        var folder = organizer.CreateFolder("  Work ");
        var tag = organizer.CreateTag("urgent", "Status.Danger");
        organizer.MoveToFolder(["a", "b"], folder.Id);
        organizer.ToggleTag(["a"], tag.Id);
        organizer.SetArchived(["c"], true);
        organizer.SetCollapsed(folder.Id, true);

        var reread = new ChatOrganizer(_root).Snapshot();

        var stored = Assert.Single(reread.Folders);
        Assert.Equal("Work", stored.Name);
        Assert.True(stored.Collapsed);
        Assert.Equal("Status.Danger", Assert.Single(reread.Tags).Color);
        Assert.Equal(folder.Id, reread.Chats["a"].FolderId);
        Assert.Equal([tag.Id], reread.Chats["a"].Tags);
        Assert.True(reread.Chats["c"].Archived);
    }

    [Fact]
    public void An_unknown_colour_falls_back_to_the_palette()
    {
        var tag = new ChatOrganizer(_root).CreateTag("x", "#FF0000");

        Assert.Contains(tag.Color, ChatOrganizer.TagColors);
    }

    [Fact]
    public void Deleting_a_folder_keeps_its_chats_without_a_folder()
    {
        var organizer = new ChatOrganizer(_root);
        var folder = organizer.CreateFolder("Old");
        organizer.MoveToFolder(["a"], folder.Id);
        organizer.ToggleTag(["b"], organizer.CreateTag("t", "Status.Success").Id);
        organizer.MoveToFolder(["b"], folder.Id);

        organizer.DeleteFolder(folder.Id);

        var state = organizer.Snapshot();
        Assert.Empty(state.Folders);
        // Пустое назначение не хранится, а тег у «b» остался.
        Assert.False(state.Chats.ContainsKey("a"));
        Assert.Null(state.Chats["b"].FolderId);
        Assert.Single(state.Chats["b"].Tags);
    }

    [Fact]
    public void A_tag_toggles_for_the_whole_selection_at_once()
    {
        var organizer = new ChatOrganizer(_root);
        var tag = organizer.CreateTag("t", "Status.Success").Id;
        organizer.ToggleTag(["a"], tag);

        // Был не у всех — ставится всем.
        organizer.ToggleTag(["a", "b"], tag);
        Assert.All(new[] { "a", "b" }, id => Assert.Contains(tag, organizer.PlacementOf(id).Tags));

        // Теперь у всех — снимается со всех.
        organizer.ToggleTag(["a", "b"], tag);
        Assert.Empty(organizer.Snapshot().Chats);
    }

    [Fact]
    public void Deleting_a_tag_removes_it_from_every_chat()
    {
        var organizer = new ChatOrganizer(_root);
        var tag = organizer.CreateTag("t", "Status.Success").Id;
        organizer.ToggleTag(["a", "b"], tag);

        organizer.DeleteTag(tag);

        Assert.Empty(organizer.Snapshot().Tags);
        Assert.Empty(organizer.Snapshot().Chats);
    }

    [Fact]
    public void Moving_to_a_folder_that_does_not_exist_means_no_folder()
    {
        var organizer = new ChatOrganizer(_root);
        organizer.MoveToFolder(["a"], "ghost");

        Assert.Null(organizer.PlacementOf("a").FolderId);
    }

    [Fact]
    public void A_deleted_chat_is_forgotten_by_the_store_event()
    {
        var store = new ChatStore(_root);
        var organizer = new ChatOrganizer(_root);
        store.Deleted += organizer.Forget;
        var session = new ChatSession { Id = "gone", Title = "x", CreatedAt = Today, UpdatedAt = Today };
        store.Save(session);
        store.Flush();
        organizer.SetArchived(["gone"], true);

        store.Delete("gone");

        Assert.False(organizer.Snapshot().Chats.ContainsKey("gone"));
    }

    [Fact]
    public void A_broken_file_reads_as_empty()
    {
        File.WriteAllText(Path.Combine(_root, "chats", ChatOrganizer.FileName), "{ not json");

        var state = new ChatOrganizer(_root).Snapshot();

        Assert.Empty(state.Folders);
        Assert.Empty(state.Chats);
    }

    [Fact]
    public void Snapshots_are_copies()
    {
        var organizer = new ChatOrganizer(_root);
        organizer.CreateFolder("f");
        var snapshot = organizer.Snapshot();

        snapshot.Folders[0].Name = "changed";

        Assert.Equal("f", organizer.Snapshot().Folders[0].Name);
    }

    // ───────────────────────── раскладка списка ─────────────────────────

    [Fact]
    public void Without_folders_the_list_is_grouped_by_date_as_before()
    {
        var items = new[]
        {
            Entry("pin", Today.AddDays(-9), pinned: true),
            Entry("t", Today.AddHours(10)),
            Entry("y", Today.AddDays(-1).AddHours(3)),
            Entry("e", Today.AddDays(-5))
        };

        var nodes = ChatListLayout.Build(items, new ChatOrganizer.State(), ChatSort.Updated, null, false, Today);

        Assert.Equal(
            ["#S.ChatList.Pinned", "pin", "#S.ChatList.Today", "t", "#S.ChatList.Yesterday", "y", "#S.ChatList.Earlier", "e"],
            Shape(nodes));
    }

    [Fact]
    public void Folders_come_after_pinned_and_a_collapsed_one_shows_only_its_count()
    {
        var organize = new ChatOrganizer.State
        {
            Folders =
            [
                new ChatFolder { Id = "f1", Name = "Open" },
                new ChatFolder { Id = "f2", Name = "Shut", Collapsed = true },
                new ChatFolder { Id = "f3", Name = "Empty" }
            ],
            Chats =
            {
                ["a"] = new ChatPlacement { FolderId = "f1" },
                ["b"] = new ChatPlacement { FolderId = "f2" },
                ["c"] = new ChatPlacement { FolderId = "f2" },
                // Закреплённый стоит наверху, даже если лежит в папке.
                ["p"] = new ChatPlacement { FolderId = "f1" }
            }
        };
        var items = new[]
        {
            Entry("a", Today), Entry("b", Today), Entry("c", Today), Entry("p", Today, pinned: true), Entry("loose", Today)
        };

        var nodes = ChatListLayout.Build(items, organize, ChatSort.Updated, null, false, Today);

        Assert.Equal(
            ["#S.ChatList.Pinned", "p", "#S.ChatList.Folders", "folder:Open/1", "a", "folder:Shut/2", "folder:Empty/0", "#S.ChatList.Today", "loose"],
            Shape(nodes));
    }

    /// <summary>
    /// Чат раскрытой папки помечен вложенным — по метке строка рисуется внутри карточки папки.
    /// До правки чаты папки стояли вровень с остальными, и что они в папке, видно не было.
    /// </summary>
    [Fact]
    public void Chats_of_an_open_folder_and_of_the_archive_are_nested_and_others_are_not()
    {
        var organize = new ChatOrganizer.State
        {
            Folders = [new ChatFolder { Id = "f1", Name = "Open" }],
            Chats =
            {
                ["in"] = new ChatPlacement { FolderId = "f1" },
                ["pinned"] = new ChatPlacement { FolderId = "f1" },
                ["old"] = new ChatPlacement { Archived = true }
            }
        };
        var items = new[] { Entry("in", Today), Entry("pinned", Today, pinned: true), Entry("loose", Today), Entry("old", Today) };

        var nested = ChatListLayout.Build(items, organize, ChatSort.Updated, null, archiveExpanded: true, Today)
            .OfType<ChatListChat>()
            .ToDictionary(chat => chat.Entry.Id, chat => chat.Nested);

        Assert.True(nested["in"]);
        Assert.True(nested["old"]);
        Assert.False(nested["pinned"]);
        Assert.False(nested["loose"]);
    }

    [Fact]
    public void A_chat_in_a_folder_that_was_removed_by_hand_is_not_lost()
    {
        var organize = new ChatOrganizer.State { Chats = { ["a"] = new ChatPlacement { FolderId = "deleted" } } };

        var nodes = ChatListLayout.Build([Entry("a", Today)], organize, ChatSort.Updated, null, false, Today);

        Assert.Equal(["a"], Rows(nodes));
    }

    [Fact]
    public void Archived_chats_hide_behind_the_archive_header_until_expanded()
    {
        var organize = new ChatOrganizer.State { Chats = { ["old"] = new ChatPlacement { Archived = true } } };
        var items = new[] { Entry("old", Today), Entry("new", Today) };

        var closed = ChatListLayout.Build(items, organize, ChatSort.Updated, null, false, Today);
        var open = ChatListLayout.Build(items, organize, ChatSort.Updated, null, true, Today);

        Assert.Equal(["#S.ChatList.Today", "new", "archive/1"], Shape(closed));
        Assert.Equal(["#S.ChatList.Today", "new", "archive/1", "old"], Shape(open));
    }

    [Fact]
    public void A_tag_filter_keeps_only_tagged_chats_and_skips_empty_folders()
    {
        var organize = new ChatOrganizer.State
        {
            Folders = [new ChatFolder { Id = "f", Name = "F" }],
            Tags = [new ChatTag { Id = "t", Name = "T", Color = "Status.Warning" }],
            Chats =
            {
                ["tagged"] = new ChatPlacement { Tags = ["t"] },
                ["infolder"] = new ChatPlacement { FolderId = "f" }
            }
        };
        var items = new[] { Entry("tagged", Today), Entry("infolder", Today), Entry("plain", Today) };

        var nodes = ChatListLayout.Build(items, organize, ChatSort.Updated, "t", false, Today);

        Assert.Equal(["#S.ChatList.Today", "tagged"], Shape(nodes));
        Assert.Equal("Status.Warning", Assert.Single(nodes.OfType<ChatListChat>()).Tags.Single().Color);
    }

    [Fact]
    public void A_tag_that_no_longer_exists_is_not_drawn()
    {
        var organize = new ChatOrganizer.State { Chats = { ["a"] = new ChatPlacement { Tags = ["gone"] } } };

        var row = ChatListLayout.Build([Entry("a", Today)], organize, ChatSort.Updated, null, false, Today)
            .OfType<ChatListChat>().Single();

        Assert.Empty(row.Tags);
    }

    [Fact]
    public void Other_sorts_use_one_group_instead_of_dates()
    {
        var items = new[]
        {
            Entry("b", Today, title: "beta", cost: 0.5m),
            Entry("a", Today.AddDays(-3), title: "Alpha", cost: 2m),
            Entry("c", Today.AddDays(-1), title: "gamma", cost: 0.1m)
        };
        var organize = new ChatOrganizer.State();

        Assert.Equal(["#S.ChatList.All", "a", "b", "c"], Shape(ChatListLayout.Build(items, organize, ChatSort.Title, null, false, Today)));
        Assert.Equal(["#S.ChatList.All", "a", "b", "c"], Shape(ChatListLayout.Build(items, organize, ChatSort.Cost, null, false, Today)));
        Assert.Equal(["#S.ChatList.All", "b", "c", "a"], Shape(ChatListLayout.Build(items, organize, ChatSort.Created, null, false, Today)));
    }

    [Fact]
    public void An_index_entry_from_an_older_version_sorts_by_its_last_change()
    {
        var old = new ChatIndexEntry { Id = "old", UpdatedAt = Today };
        var fresh = Entry("fresh", Today.AddDays(-2));

        Assert.Equal(["old", "fresh"], ChatListLayout.Sort([fresh, old], ChatSort.Created).Select(entry => entry.Id));
    }

    [Fact]
    public void Search_results_are_one_flat_group_with_tags()
    {
        var organize = new ChatOrganizer.State
        {
            Folders = [new ChatFolder { Id = "f", Name = "F", Collapsed = true }],
            Tags = [new ChatTag { Id = "t", Name = "T" }],
            Chats = { ["a"] = new ChatPlacement { FolderId = "f", Tags = ["t"], Archived = true } }
        };

        var nodes = ChatListLayout.Flat([Entry("a", Today)], organize, "S.Search.Found");

        Assert.Equal(["#S.Search.Found", "a"], Shape(nodes));
        Assert.Single(nodes.OfType<ChatListChat>().Single().Tags);
        Assert.Empty(ChatListLayout.Flat([], organize, "S.Search.Found"));
    }

    // ───────────────────────── ширина и цена ─────────────────────────

    [Theory]
    [InlineData(null, SidebarWidths.Default)]
    [InlineData(10.0, SidebarWidths.Min)]
    [InlineData(5000.0, SidebarWidths.Max)]
    [InlineData(250.0, 250.0)]
    [InlineData(double.NaN, SidebarWidths.Default)]
    public void A_saved_width_is_kept_within_bounds(double? saved, double expected) =>
        Assert.Equal(expected, SidebarWidths.Clamp(saved));

    [Fact]
    public void The_cost_of_a_chat_counts_hidden_variants_too()
    {
        var session = new ChatSession { Id = "c" };
        var shown = new ChatDisplayMessage
        {
            Id = "a2",
            Role = "assistant",
            Cost = new VeniceCost { Usd = 0.25m, HasData = true },
            Variants =
            [
                new ChatBranch
                {
                    Messages = [new ChatDisplayMessage { Id = "a1", Role = "assistant", Cost = new VeniceCost { Usd = 0.5m, HasData = true } }]
                }
            ]
        };
        session.Messages.Add(new ChatDisplayMessage { Id = "u", Role = "user" });
        session.Messages.Add(shown);

        Assert.Equal(0.75m, ChatCost.Total(session));
    }

    [Fact]
    public void The_index_carries_the_start_date_and_the_total_cost()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession { Id = "priced", Title = "x", CreatedAt = Today.AddDays(-3), UpdatedAt = Today };
        session.Messages.Add(new ChatDisplayMessage { Id = "a", Role = "assistant", Cost = new VeniceCost { Usd = 1.5m, HasData = true } });

        store.Save(session);
        store.Flush();

        var entry = new ChatStore(_root).List().Single();
        Assert.Equal(Today.AddDays(-3), entry.CreatedAt);
        Assert.Equal(1.5m, entry.TotalCost);
    }

    [Fact]
    public void Old_index_entries_are_filled_in_once()
    {
        var store = new ChatStore(_root);
        var session = new ChatSession { Id = "legacy", Title = "x", CreatedAt = Today.AddDays(-7), UpdatedAt = Today };
        session.Messages.Add(new ChatDisplayMessage { Id = "a", Role = "assistant", Cost = new VeniceCost { Usd = 0.2m, HasData = true } });
        store.Save(session);
        store.Flush();

        // Опись прежней версии: полей нет.
        var index = Path.Combine(_root, "chats", "index.json");
        var json = File.ReadAllText(index);
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        foreach (var item in node["items"]!.AsArray())
        {
            item!.AsObject().Remove("createdAt");
            item.AsObject().Remove("totalCost");
        }

        File.WriteAllText(index, node.ToJsonString());

        var reopened = new ChatStore(_root);
        Assert.Equal(default, reopened.List().Single().CreatedAt);

        Assert.Equal(1, reopened.BackfillIndex(CancellationToken.None));
        Assert.Equal(0, reopened.BackfillIndex(CancellationToken.None));
        var entry = reopened.List().Single();
        Assert.Equal(Today.AddDays(-7), entry.CreatedAt);
        Assert.Equal(0.2m, entry.TotalCost);
    }
}
