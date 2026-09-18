using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The sidebar groups chats by <see cref="ChatSession.UpdatedAt"/>, so that field has to mean
/// "last real activity". It used to be restamped on every save, and because switching chats
/// saves the one being left, merely visiting an old conversation dragged it into "Сегодня".
/// </summary>
public sealed class ChatStoreTimestampTests
{
    [Fact]
    public void Saving_an_unchanged_session_does_not_move_it_to_today()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var lastWeek = DateTime.Now.AddDays(-7);
            var session = store.CreateNew();
            session.Title = "Старый чат";
            session.CreatedAt = lastWeek;
            session.UpdatedAt = lastWeek;
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m1", Text = "привет" });
            store.Save(session);

            // What opening another chat does to this one.
            store.Save(session);
            store.Save(session);

            // Хранилище пишет в фоне, а читает здесь второй экземпляр — прямо с диска.
            store.Flush();

            var entry = Assert.Single(new ChatStore(root).List());
            Assert.Equal(lastWeek, entry.UpdatedAt, TimeSpan.FromSeconds(1));
            Assert.NotEqual(DateTime.Today, entry.UpdatedAt.Date);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_real_edit_still_moves_the_chat_up()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew();
            session.CreatedAt = DateTime.Now.AddDays(-3);
            session.UpdatedAt = DateTime.Now.AddDays(-3);
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m1", Text = "первое" });
            store.Save(session);

            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m2", Text = "второе" });
            session.UpdatedAt = DateTime.Now;
            store.Save(session);

            var entry = Assert.Single(store.List());
            Assert.Equal(DateTime.Today, entry.UpdatedAt.Date);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Renaming_keeps_the_chat_where_it_was()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var lastMonth = DateTime.Now.AddDays(-30);
            var session = store.CreateNew();
            session.CreatedAt = lastMonth;
            session.UpdatedAt = lastMonth;
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m1", Text = "привет" });
            store.Save(session);

            Assert.True(store.Rename(session.Id, "  Новое имя  "));

            var entry = Assert.Single(store.List());
            Assert.Equal("Новое имя", entry.Title);
            Assert.Equal(lastMonth, entry.UpdatedAt, TimeSpan.FromSeconds(1));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Pinning_survives_a_reload_and_sorts_first_without_touching_the_timestamp()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var old = Seed(store, "Старый", DateTime.Now.AddDays(-10));
            Seed(store, "Свежий", DateTime.Now);

            Assert.True(store.SetPinned(old.Id, true));
            store.Flush();

            var reloaded = new ChatStore(root).List();
            Assert.Equal(old.Id, reloaded[0].Id);
            Assert.True(reloaded[0].IsPinned);
            Assert.False(reloaded[1].IsPinned);
            Assert.Equal(DateTime.Now.AddDays(-10), reloaded[0].UpdatedAt, TimeSpan.FromSeconds(5));

            var second = new ChatStore(root);
            Assert.True(second.SetPinned(old.Id, false));
            second.Flush();
            Assert.False(new ChatStore(root).List().Single(i => i.Id == old.Id).IsPinned);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static ChatSession Seed(ChatStore store, string title, DateTime when)
    {
        var session = store.CreateNew();
        session.Title = title;
        session.CreatedAt = when;
        session.UpdatedAt = when;
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = Guid.NewGuid().ToString("N"), Text = title });
        store.Save(session);
        return session;
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-chatstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp folder; not worth failing a test over.
        }
    }
}
