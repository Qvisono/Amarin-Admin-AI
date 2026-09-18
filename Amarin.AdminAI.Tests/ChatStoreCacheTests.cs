using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Опись чатов живёт в памяти, а запись уходит в фоновую задачу.
/// </summary>
/// <remarks>
/// Прежде каждый <c>List</c> и <c>Search</c> читал <c>index.json</c> с диска, а сохранение чата
/// вычитывало весь его файл обратно ради сравнения строк — всё на потоке диспетчера и по
/// нескольку раз в секунду, пока модель отвечает.
/// </remarks>
public sealed class ChatStoreCacheTests
{
    [Fact]
    public void The_index_is_read_from_disk_once()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            Seed(store, "Первый");
            store.Flush();

            Assert.Single(store.List());

            // Файла больше нет, а список обязан остаться прежним: значит второго чтения не было.
            File.Delete(Path.Combine(root, "chats", "index.json"));
            Assert.Single(store.List());
            Assert.Equal("Первый", store.List()[0].Title);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Saving_an_unchanged_session_writes_nothing()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = Seed(store, "Нетронутый");
            store.Flush();

            var path = Path.Combine(root, "chats", session.Id + ".json");
            Assert.True(File.Exists(path));

            // Сносим файл и сохраняем тот же самый объект ещё раз. Если хранилище сочтёт, что
            // писать нечего, файл не вернётся — а это и есть то, что делает переключение чата.
            File.Delete(path);
            store.Save(session);
            store.Flush();

            Assert.False(File.Exists(path));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_real_change_is_written()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = Seed(store, "Растущий");
            store.Flush();

            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "m2", Text = "второе" });
            session.UpdatedAt = DateTime.Now;
            store.Save(session);
            store.Flush();

            var reloaded = new ChatStore(root).TryLoad(session.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(2, reloaded.Messages.Count);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Flush_waits_for_the_background_write()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = Seed(store, "Ждущий");
            store.Flush();

            Assert.True(File.Exists(Path.Combine(root, "chats", session.Id + ".json")));
            Assert.True(File.Exists(Path.Combine(root, "chats", "index.json")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void A_chat_that_has_not_reached_the_disk_yet_still_loads()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = Seed(store, "Свежий");

            // Без Flush: TryLoad обязан сам дописать очередь, иначе открытие только что
            // сохранённого чата отдавало бы пустоту.
            var loaded = store.TryLoad(session.Id);

            Assert.NotNull(loaded);
            Assert.Equal("Свежий", loaded.Title);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Deleting_a_chat_beats_its_queued_write()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = Seed(store, "Обречённый");

            // Удаление идёт вплотную за сохранением: отложенная запись не должна воскресить файл.
            Assert.True(store.Delete(session.Id));
            store.Flush();

            Assert.False(File.Exists(Path.Combine(root, "chats", session.Id + ".json")));
            Assert.Empty(store.List());
            Assert.Empty(new ChatStore(root).List());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Invalidate_makes_the_store_read_the_index_again()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            Seed(store, "До импорта");
            store.Flush();
            Assert.Single(store.List());

            // Что делает импорт данных: раскладывает чужие файлы мимо хранилища.
            var other = new ChatStore(root);
            other.Invalidate();
            Seed(other, "После импорта");
            Seed(other, "И ещё один");
            other.Flush();

            store.Invalidate();
            Assert.Equal(3, store.List().Count);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Wiping_everything_clears_both_the_files_and_the_cache()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            Seed(store, "Раз");
            Seed(store, "Два");

            Assert.Equal(2, store.DeleteAll());
            Assert.Empty(store.List());

            var leftovers = Directory.GetFiles(Path.Combine(root, "chats"), "*.json")
                .Select(Path.GetFileName)
                .Where(name => !string.Equals(name, "index.json", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Empty(leftovers);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static ChatSession Seed(ChatStore store, string title)
    {
        var session = store.CreateNew();
        session.Title = title;
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "user",
            Id = Guid.NewGuid().ToString("N"),
            Text = title
        });
        store.Save(session);
        return session;
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-chatcache-" + Guid.NewGuid().ToString("N"));
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
            // Временная папка; ронять из-за неё тест не стоит.
        }
    }
}
