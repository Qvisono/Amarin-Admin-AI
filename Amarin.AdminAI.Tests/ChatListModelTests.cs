using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Список чатов без окна: слепок состава, выбор строк, бросок в раздел. До 1.30.0 всё это было
/// полями и методами окна и проверялось оконными тестами через отражение.
/// </summary>
public sealed class ChatListModelTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 10, 3);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-chatlist-" + Guid.NewGuid().ToString("N"));

    public ChatListModelTests() => Directory.CreateDirectory(_root);

    // ───────────────────────── слепок ─────────────────────────

    [Fact]
    public void The_signature_describes_the_list_not_the_open_chat()
    {
        // Открытый чат, идущие ходы и метки «ответ готов» в слепок не входят — их нет среди
        // входов: переключение чата обязано править подсветку строки, а не пересобирать панель.
        var items = Items();
        var organize = new ChatOrganizer.State();

        Assert.Equal(Signature(View(), items, organize), Signature(View(), items, organize));
    }

    [Fact]
    public void Anything_that_changes_what_is_drawn_changes_the_signature()
    {
        var items = Items();
        var organize = new ChatOrganizer.State();
        var baseline = Signature(View(), items, organize);

        Assert.NotEqual(baseline, Signature(View() with { Today = Today.AddDays(1) }, items, organize));
        Assert.NotEqual(baseline, Signature(View() with { Query = "диск" }, items, organize));
        Assert.NotEqual(baseline, Signature(View() with { Sort = ChatSort.Cost }, items, organize));
        Assert.NotEqual(baseline, Signature(View() with { ArchiveExpanded = true }, items, organize));
        Assert.NotEqual(baseline, Signature(View() with { TagFilter = "t1" }, items, organize));

        var renamed = Items();
        renamed[0].Title = "Другое имя";
        Assert.NotEqual(baseline, Signature(View(), renamed, organize));

        var repriced = Items();
        repriced[1].TotalCost = 1.25m;
        Assert.NotEqual(baseline, Signature(View(), repriced, organize));

        var collapsed = new ChatOrganizer.State { Folders = [new ChatFolder { Id = "f", Name = "Работа", Collapsed = true }] };
        var expanded = new ChatOrganizer.State { Folders = [new ChatFolder { Id = "f", Name = "Работа", Collapsed = false }] };
        Assert.NotEqual(Signature(View(), items, collapsed), Signature(View(), items, expanded));
    }

    // ───────────────────────── выбор ─────────────────────────

    [Fact]
    public void Ctrl_click_toggles_and_moves_the_anchor()
    {
        var selection = new ChatSelection();

        selection.Toggle("a");
        selection.Toggle("b");
        selection.Toggle("a");

        Assert.Equal(["b"], selection.Items);
        Assert.Equal("a", selection.Anchor);
    }

    [Fact]
    public void Shift_click_selects_everything_between_in_screen_order_and_keeps_the_anchor()
    {
        var selection = new ChatSelection();
        string[] visible = ["a", "b", "c", "d", "e"];

        selection.Toggle("b");
        selection.SelectRange("d", visible, fallbackAnchor: "a");
        Assert.Equal(["b", "c", "d"], selection.Items.Order(StringComparer.Ordinal));

        // Следующий Shift+щелчок — от того же якоря, как в Проводнике.
        selection.SelectRange("a", visible, fallbackAnchor: "a");
        Assert.Equal(["a", "b"], selection.Items.Order(StringComparer.Ordinal));
        Assert.Equal("b", selection.Anchor);
    }

    [Fact]
    public void Shift_click_without_an_anchor_starts_from_the_open_chat()
    {
        var selection = new ChatSelection();

        selection.SelectRange("c", ["a", "b", "c"], fallbackAnchor: "a");

        Assert.Equal(["a", "b", "c"], selection.Items.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Shift_click_on_a_row_out_of_sight_works_like_ctrl_click()
    {
        var selection = new ChatSelection();
        selection.Toggle("a");

        selection.SelectRange("hidden", ["a", "b"], fallbackAnchor: "a");

        Assert.Equal(["a", "hidden"], selection.Items.Order(StringComparer.Ordinal));
        Assert.Equal("hidden", selection.Anchor);
    }

    [Fact]
    public void Dragging_a_selected_row_carries_the_whole_selection()
    {
        var selection = new ChatSelection();
        selection.Toggle("a");
        selection.Toggle("b");

        Assert.Equal(["a", "b"], selection.DragSet("a").Order(StringComparer.Ordinal));
        Assert.Equal(["c"], selection.DragSet("c"));

        // Одна выбранная строка — тащится то, за что взялись.
        selection.Toggle("b");
        Assert.Equal(["z"], selection.DragSet("z"));
    }

    [Fact]
    public void Clearing_and_pruning_keep_the_selection_honest()
    {
        var selection = new ChatSelection();
        Assert.False(selection.Clear());

        selection.Toggle("a");
        selection.Toggle("b");
        selection.Toggle("c");
        selection.KeepOnly(new HashSet<string>(["a", "c"], StringComparer.Ordinal));
        Assert.Equal(["a", "c"], selection.Items.Order(StringComparer.Ordinal));

        selection.Remove(["c"]);
        Assert.Equal(["a"], selection.Items);

        Assert.True(selection.Clear());
        Assert.Equal(0, selection.Count);
        Assert.Null(selection.Anchor);
    }

    // ───────────────────────── бросок ─────────────────────────

    [Fact]
    public void A_pinned_chat_dropped_into_a_collapsed_folder_is_unpinned_and_the_folder_opens()
    {
        var (store, organizer) = Storage();
        Save(store, "a");
        store.SetPinned("a", true);
        var folder = organizer.CreateFolder("Работа");
        organizer.SetCollapsed(folder.Id, true);

        Assert.True(ChatDrop.Apply(store, organizer, ["a"], ChatDropTarget.Folder(folder.Id)));

        Assert.False(store.List().Single(entry => entry.Id == "a").IsPinned);
        Assert.Equal(folder.Id, organizer.PlacementOf("a").FolderId);
        Assert.False(organizer.Snapshot().Folders.Single(item => item.Id == folder.Id).Collapsed);
    }

    [Fact]
    public void A_drop_that_changes_nothing_reports_so()
    {
        var (store, organizer) = Storage();
        Save(store, "a");

        Assert.False(ChatDrop.Apply(store, organizer, ["a"], ChatDropTarget.Loose));
    }

    [Fact]
    public void An_archived_chat_dropped_on_the_pinned_section_comes_back_pinned()
    {
        var (store, organizer) = Storage();
        Save(store, "a");
        organizer.SetArchived(["a"], true);

        Assert.True(ChatDrop.Apply(store, organizer, ["a"], ChatDropTarget.Pinned));

        Assert.True(store.List().Single(entry => entry.Id == "a").IsPinned);
        Assert.False(organizer.PlacementOf("a").Archived);
    }

    // ───────────────────────── папки и архив в выборе (1.32.0) ─────────────────────────

    [Fact]
    public void The_members_of_a_folder_and_the_archive_match_what_the_layout_counts()
    {
        // Рамка по заголовку и Ctrl+A выбирают чаты за заголовками — тех же, что считает раскладка.
        var items = new List<ChatIndexEntry>
        {
            new() { Id = "pinned", Title = "P", UpdatedAt = Today, IsPinned = true },
            new() { Id = "in1", Title = "1", UpdatedAt = Today },
            new() { Id = "in2", Title = "2", UpdatedAt = Today },
            new() { Id = "loose", Title = "L", UpdatedAt = Today },
            new() { Id = "old", Title = "A", UpdatedAt = Today }
        };
        var organize = new ChatOrganizer.State
        {
            Folders = [new ChatFolder { Id = "f", Name = "Работа", Collapsed = true }, new ChatFolder { Id = "empty", Name = "Пусто" }],
            Chats =
            {
                ["pinned"] = new ChatPlacement { FolderId = "f" },
                ["in1"] = new ChatPlacement { FolderId = "f" },
                ["in2"] = new ChatPlacement { FolderId = "f" },
                ["old"] = new ChatPlacement { Archived = true }
            }
        };

        var members = ChatListLayout.Members(items, organize, tagFilter: null);
        var layout = ChatListLayout.Build(items, organize, ChatSort.Updated, null, archiveExpanded: false, Today);

        // Закреплённый в папке стоит наверху и в число на её заголовке не входит — не входит и сюда.
        Assert.Equal(["in1", "in2"], members.Of("f").OrderBy(id => id));
        Assert.Equal(layout.OfType<ChatListFolder>().Single(folder => folder.Folder.Id == "f").Count, members.Of("f").Count);
        Assert.Empty(members.Of("empty"));
        Assert.Equal(["old"], members.Archived);
        Assert.Equal(layout.OfType<ChatListArchive>().Single().Count, members.Archived.Count);
        Assert.Equal(items.Count, members.All.Count);

        // Под фильтром по тегу — только то, что фильтр показывает: невидимое не выбирается.
        organize.Tags.Add(new ChatTag { Id = "t", Name = "Тег" });
        organize.Chats["in1"].Tags.Add("t");
        var filtered = ChatListLayout.Members(items, organize, tagFilter: "t");
        Assert.Equal(["in1"], filtered.All);
        Assert.False(filtered.Folders.ContainsKey("empty"));
    }

    [Fact]
    public void A_folder_is_whole_while_all_its_chats_are_selected()
    {
        var members = new ChatListMembers(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["f"] = ["a", "b"], ["empty"] = [] },
            ["z"],
            ["a", "b", "c", "z"]);
        var selection = new ChatSelection();

        selection.Sweep(selection.BeginSweep(additive: false), ["a", "b"], ["f", "empty"]);
        Assert.Equal(["empty", "f"], selection.WholeFolders(members).OrderBy(id => id));

        // Снял Ctrl+щелчком один чат — папка уже не целиком, «Удалить» её не унесёт.
        selection.Toggle("a");
        Assert.Equal(["empty"], selection.WholeFolders(members));

        // Ctrl+щелчок по заголовку: выбрать целиком, второй — снять вместе с чатами.
        selection.ToggleFolder("f", members.Of("f"));
        Assert.Contains("f", selection.WholeFolders(members));
        selection.ToggleFolder("f", members.Of("f"));
        Assert.DoesNotContain("a", selection.Items);
        Assert.DoesNotContain("f", selection.WholeFolders(members));
    }

    [Fact]
    public void A_sweep_with_ctrl_keeps_the_old_selection_and_without_it_starts_anew()
    {
        var selection = new ChatSelection();
        selection.Toggle("old");

        selection.Sweep(selection.BeginSweep(additive: true), ["new"], []);
        Assert.Equal(["new", "old"], selection.Items.OrderBy(id => id));

        selection.Sweep(selection.BeginSweep(additive: false), ["other"], []);
        Assert.Equal(["other"], selection.Items);
    }

    [Fact]
    public void Select_all_takes_every_chat_and_every_folder_the_list_shows()
    {
        var members = new ChatListMembers(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["f"] = ["a"] },
            ["z"],
            ["a", "b", "z"]);
        var selection = new ChatSelection();

        selection.SelectAll(members);

        Assert.Equal(["a", "b", "z"], selection.Items.OrderBy(id => id));
        Assert.Equal(["f"], selection.WholeFolders(members));
        Assert.True(selection.Clear());
        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void Deleting_a_batch_removes_files_index_and_placements_in_one_pass()
    {
        var (store, organizer) = Storage();
        Save(store, "a");
        Save(store, "b");
        Save(store, "c");
        organizer.MoveToFolder(["a", "b"], organizer.CreateFolder("Работа").Id);
        IReadOnlyList<string>? reported = null;
        var batches = 0;
        store.DeletedMany += ids =>
        {
            batches++;
            reported = ids;
            organizer.Forget(ids);
        };

        var removed = store.DeleteMany(["a", "b", "b", " "]);

        Assert.Equal(2, removed);
        Assert.Equal(1, batches);
        Assert.Equal(["a", "b"], reported);
        Assert.Equal(["c"], store.List().Select(entry => entry.Id));
        Assert.Null(store.TryLoad("a"));
        Assert.False(organizer.Snapshot().Chats.ContainsKey("a"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка — уберёт система.
        }
    }

    private static ChatListView View() => new("", false, 0, ChatSort.Updated, null, false, Today);

    private static List<ChatIndexEntry> Items() =>
    [
        new() { Id = "один", Title = "Первый", UpdatedAt = new DateTime(2026, 10, 1) },
        new() { Id = "два", Title = "Второй", UpdatedAt = new DateTime(2026, 10, 2) }
    ];

    private static string Signature(ChatListView view, IReadOnlyList<ChatIndexEntry> items, ChatOrganizer.State organize)
    {
        var builder = new StringBuilder();
        ChatListSignature.Append(builder, view, items, organize);
        return builder.ToString();
    }

    private (ChatStore Store, ChatOrganizer Organizer) Storage() => (new ChatStore(_root), new ChatOrganizer(_root));

    private static void Save(ChatStore store, string id)
    {
        var now = DateTime.Now;
        store.Save(new ChatSession
        {
            Id = id,
            Title = "Чат " + id,
            CreatedAt = now,
            UpdatedAt = now,
            Messages = [new ChatDisplayMessage { Role = "user", Id = id + "-u", CreatedAt = now, Text = "привет" }]
        });
        store.Flush();
    }
}
