using System.Text.Json;

namespace Amarin.Core;

public sealed class ChatStore
{
    private readonly string _root;
    private readonly string _chatsDirectory;
    private readonly string _indexFile;

    public ChatStore(string? rootDirectory = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppPaths.Root
            : rootDirectory;
        _chatsDirectory = Path.Combine(_root, "chats");
        _indexFile = Path.Combine(_chatsDirectory, "index.json");
    }

    public string RootDirectory => _root;

    public ChatSession CreateNew(string? selectedModelId = null)
    {
        var now = DateTime.Now;
        return new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = "Новый чат",
            CreatedAt = now,
            UpdatedAt = now,
            SelectedModelId = selectedModelId ?? ""
        };
    }

    public void Save(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            throw new ArgumentException("Chat session id is required.", nameof(session));
        }

        session.UpdatedAt = DateTime.Now;
        Directory.CreateDirectory(_chatsDirectory);

        var json = JsonSerializer.Serialize(session, AppJson.Options) + Environment.NewLine;
        AppDataFile.WriteAtomic(ChatPath(session.Id), json);
        UpsertIndex(session);
    }

    public ChatSession? TryLoad(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var path = ChatPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ChatSession>(text, AppJson.Options);
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<ChatIndexEntry> List()
    {
        var index = LoadIndex();
        return index.Items
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
    }

    public IReadOnlyList<ChatIndexEntry> Search(string query)
    {
        var items = List();
        if (string.IsNullOrWhiteSpace(query))
        {
            return items;
        }

        return items
            .Where(item => item.Title.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var path = ChatPath(id);
        var existed = File.Exists(path);
        if (existed)
        {
            File.Delete(path);
        }

        var index = LoadIndex();
        var removed = index.Items.RemoveAll(item => item.Id == id) > 0;
        if (removed)
        {
            SaveIndex(index);
        }

        return existed || removed;
    }

    public int DeleteAll()
    {
        var count = LoadIndex().Items.Count;
        if (Directory.Exists(_chatsDirectory))
        {
            foreach (var file in Directory.GetFiles(_chatsDirectory, "*.json"))
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // continue wiping the rest
                }
            }
        }

        Directory.CreateDirectory(_chatsDirectory);
        SaveIndex(new ChatIndex());
        return count;
    }

    private void UpsertIndex(ChatSession session)
    {
        var index = LoadIndex();
        var entry = index.Items.FirstOrDefault(item => item.Id == session.Id);
        if (entry is null)
        {
            entry = new ChatIndexEntry { Id = session.Id };
            index.Items.Add(entry);
        }

        entry.Title = session.Title;
        entry.UpdatedAt = session.UpdatedAt;
        SaveIndex(index);
    }

    private ChatIndex LoadIndex()
    {
        if (!File.Exists(_indexFile))
        {
            return new ChatIndex();
        }

        try
        {
            var text = File.ReadAllText(_indexFile);
            return JsonSerializer.Deserialize<ChatIndex>(text, AppJson.Options) ?? new ChatIndex();
        }
        catch
        {
            return new ChatIndex();
        }
    }

    private void SaveIndex(ChatIndex index)
    {
        index.Items = index.Items
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();
        var json = JsonSerializer.Serialize(index, AppJson.Options) + Environment.NewLine;
        AppDataFile.WriteAtomic(_indexFile, json);
    }

    private string ChatPath(string id) => Path.Combine(_chatsDirectory, $"{id}.json");
}
