using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Временный чат (1.37.0): не сохраняется, не попадает в поиск, архив и копии.
/// </summary>
public sealed class TemporaryChatTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 10, 23);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-temporary-" + Guid.NewGuid().ToString("N"));

    public TemporaryChatTests() => Directory.CreateDirectory(_root);

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

    /// <summary>
    /// Охрана в самом хранилище: ход, поздние заголовок и сводка, переименование — все пишут через
    /// него, и ни один путь не должен положить временный чат на диск или в опись.
    /// </summary>
    [Fact]
    public void The_store_never_writes_a_temporary_chat()
    {
        var store = new ChatStore(_root);
        var saved = 0;
        store.Saved += _ => saved++;
        var chat = store.CreateNew();
        chat.IsTemporary = true;
        chat.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m1", Text = "secret-plan" });

        store.Save(chat);
        store.Flush();

        Assert.Equal(0, saved);
        Assert.Empty(store.List());
        Assert.Null(store.TryLoad(chat.Id));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("secret-plan", StringComparison.Ordinal));
    }

    [Fact]
    public void The_temporary_mark_never_reaches_a_file()
    {
        var json = JsonSerializer.Serialize(new ChatSession { Id = "c", IsTemporary = true }, AppJson.Options);

        Assert.DoesNotContain("temporary", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options)!.IsTemporary);
    }

    [Fact]
    public void The_list_shows_only_temporary_chats_that_have_messages()
    {
        var chats = new TemporaryChats();
        var empty = new ChatSession { Id = "empty", IsTemporary = true };
        var talked = new ChatSession { Id = "talked", IsTemporary = true, Title = "t" };
        talked.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "hi" });
        chats.Keep(empty);
        chats.Keep(talked);
        chats.Keep(new ChatSession { Id = "regular" });

        var entry = Assert.Single(chats.Entries());
        Assert.Equal("talked", entry.Id);
        Assert.True(entry.IsTemporary);
        Assert.False(chats.Contains("regular"));
    }

    [Fact]
    public void Temporary_chats_get_their_own_group_on_top_and_nowhere_else()
    {
        var items = new List<ChatIndexEntry>
        {
            new() { Id = "pinned", UpdatedAt = Today, IsPinned = true },
            new() { Id = "temp", UpdatedAt = Today, IsTemporary = true },
            new() { Id = "today", UpdatedAt = Today }
        };
        var organize = new ChatOrganizer.State();

        var nodes = ChatListLayout.Build(items, organize, ChatSort.Updated, null, archiveExpanded: false, Today);

        Assert.Equal("S.ChatList.Temporary", Assert.IsType<ChatListGroup>(nodes[0]).TitleKey);
        Assert.Equal("temp", Assert.IsType<ChatListChat>(nodes[1]).Entry.Id);
        Assert.Single(nodes.OfType<ChatListChat>(), row => row.Entry.Id == "temp");
        Assert.Contains("temp", ChatListLayout.Members(items, organize, null).All);
    }

    /// <summary>Отложенная задача легла бы на диск со словами человека — во временном чате её нет.</summary>
    [Fact]
    public async Task A_scheduled_task_is_refused_in_a_temporary_chat()
    {
        var book = new DeferredBook(_root);
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = new ToolCallRecord { Id = "call", Name = DeferredTaskTool.ToolName },
            Assistant = new ChatDisplayMessage { Role = "assistant" },
            Observer = SilentTurnObserver.Instance,
            SessionId = "chat",
            TemporaryChat = true
        }))
        {
            var result = await new DeferredTaskTool(book).ExecuteAsync(
                JsonSerializer.SerializeToElement(new { action = "remind", text = "call", @in = "5m" }));

            Assert.False(result.Success);
            Assert.Contains("temporary chat", result.Output, StringComparison.Ordinal);
        }

        Assert.Empty(book.Snapshot());
    }
}
