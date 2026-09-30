using System.Text;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Поиск по тексту всех чатов (D1): индекс на диске, дочитывание изменившихся чатов и то, что
/// при шифровании чатов индекс не лежит открытой копией переписок.
/// </summary>
public sealed class ChatTextIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-textindex-" + Guid.NewGuid().ToString("N"));

    public ChatTextIndexTests() => Directory.CreateDirectory(Path.Combine(_root, "chats"));

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

    private static ChatSession Chat(string id, DateTime updated, params string[] texts)
    {
        var session = new ChatSession { Id = id, Title = "Чат " + id, UpdatedAt = updated };
        for (var i = 0; i < texts.Length; i++)
        {
            session.Messages.Add(new ChatDisplayMessage
            {
                Id = id + "-" + i,
                Role = i % 2 == 0 ? "user" : "assistant",
                Text = texts[i],
                CreatedAt = updated.AddMinutes(i)
            });
        }

        return session;
    }

    [Fact]
    public void Hits_come_from_every_chat_freshest_first_with_the_match_marked()
    {
        var index = new ChatTextIndex(_root, () => false);
        index.Update(Chat("old", new DateTime(2026, 1, 1), "проверь драйвер Wi-Fi"));
        index.Update(Chat("new", new DateTime(2026, 9, 1), "ничего", "обновил драйвер видеокарты"));

        var hits = index.Search("ДРАЙВЕР");

        Assert.Equal(["new", "old"], hits.Select(hit => hit.ChatId));
        var first = hits[0];
        Assert.Equal("драйвер", first.Snippet.Substring(first.MatchStart, first.MatchLength));
        Assert.Equal("new-1", first.MessageId);
    }

    [Fact]
    public void A_one_letter_query_finds_nothing() =>
        Assert.Empty(new ChatTextIndex(_root, () => false).Search("д"));

    [Fact]
    public void A_long_message_is_cut_around_the_match_and_keeps_one_line()
    {
        var text = new string('a', 200) + "\nцель\n" + new string('b', 200);
        var at = text.IndexOf("цель", StringComparison.Ordinal);

        var (snippet, start) = ChatTextIndex.Snippet(text, at, 4);

        Assert.StartsWith("…", snippet, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet, StringComparison.Ordinal);
        Assert.Equal("цель", snippet.Substring(start, 4));
        Assert.DoesNotContain('\n', snippet);
    }

    [Fact]
    public void A_restart_rereads_only_the_chats_that_changed()
    {
        var first = new ChatTextIndex(_root, () => false);
        var a = Chat("a", new DateTime(2026, 9, 1), "alpha");
        var b = Chat("b", new DateTime(2026, 9, 2), "beta");
        first.Build([Entry(a), Entry(b)], id => id == "a" ? a : b, CancellationToken.None);

        var loaded = new List<string>();
        var bChanged = Chat("b", new DateTime(2026, 9, 3), "beta gamma");
        var second = new ChatTextIndex(_root, () => false);
        second.Build([Entry(a), Entry(bChanged)], id =>
        {
            loaded.Add(id);
            return id == "a" ? a : bChanged;
        }, CancellationToken.None);

        Assert.Equal(["b"], loaded);
        Assert.Single(second.Search("gamma"));
    }

    [Fact]
    public void Deleted_chats_leave_the_index_on_rebuild()
    {
        var index = new ChatTextIndex(_root, () => false);
        index.Update(Chat("gone", DateTime.Now, "секрет"));

        index.Build([], _ => null, CancellationToken.None);

        Assert.Empty(index.Search("секрет"));
    }

    [Fact]
    public void With_chat_encryption_on_the_index_file_hides_the_text()
    {
        var updated = new DateTime(2026, 9, 30, 12, 0, 0);
        var index = new ChatTextIndex(_root, () => true);
        index.Update(Chat("s", updated, "topsecret-phrase"));
        index.SaveNow();

        var bytes = File.ReadAllBytes(Path.Combine(_root, "chats", ChatTextIndex.FileName));
        Assert.True(AtRestCipher.IsEncrypted(bytes));
        Assert.DoesNotContain("topsecret-phrase", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);

        // Прочитанный обратно файл находит то же, не перечитывая чат.
        var reread = new ChatTextIndex(_root, () => true);
        reread.Build([new ChatIndexEntry { Id = "s", UpdatedAt = updated }], _ => null, CancellationToken.None);
        Assert.Single(reread.Search("topsecret"));
    }

    [Fact]
    public void Saving_and_deleting_a_chat_keeps_the_index_current()
    {
        var store = new ChatStore(_root);
        var index = new ChatTextIndex(_root, () => false);
        store.Saved += index.Update;
        store.Deleted += index.Remove;

        var chat = Chat("live", DateTime.Now, "найди меня");
        store.Save(chat);
        Assert.Single(index.Search("найди"));

        store.Delete("live");
        Assert.Empty(index.Search("найди"));
    }

    [Fact]
    public void The_index_is_rebuilt_on_the_new_machine_not_carried() =>
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("chats/" + ChatTextIndex.FileName));

    private static ChatIndexEntry Entry(ChatSession session) =>
        new() { Id = session.Id, Title = session.Title, UpdatedAt = session.UpdatedAt };
}
