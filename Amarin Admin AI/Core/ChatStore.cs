using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Чаты на диске: сами переписки в <c>chats/</c> и опись <c>chats/index.json</c> для боковой панели.
/// </summary>
/// <remarks>
/// <para>
/// Опись держится в памяти, а запись уходит в фоновую задачу. Прежде каждый <c>List</c>,
/// <c>Search</c> и <c>Save</c> читал <c>index.json</c> с диска, а сохранение чата вдобавок
/// вычитывало весь его файл обратно ради сравнения строк — и всё это на потоке диспетчера,
/// по нескольку раз в секунду, пока модель отвечает. Отсюда и подёргивание интерфейса во
/// время ответа.
/// </para>
/// <para>
/// Кэш безопасен, потому что <c>SingleInstance</c> держит программу на пользователя в одном
/// экземпляре: файлы правит только этот объект. Единственное исключение — импорт данных, он
/// раскладывает чужие файлы мимо хранилища и обязан позвать <see cref="Invalidate"/>.
/// </para>
/// </remarks>
public sealed class ChatStore
{
    private readonly string _root;
    private readonly string _chatsDirectory;
    private readonly string _indexFile;

    private readonly Lock _gate = new();

    /// <summary>Разобранная опись. <c>null</c> — ещё не читали или сбросили.</summary>
    private ChatIndex? _index;

    /// <summary>Та же опись в порядке показа. Сбрасывается вместе с любой правкой.</summary>
    private IReadOnlyList<ChatIndexEntry>? _sorted;

    /// <summary>
    /// Отпечаток последнего записанного json по чату. Заменяет чтение файла обратно: сравнить
    /// нужно с тем, что лежит на диске, а положили это туда мы сами.
    /// </summary>
    private readonly Dictionary<string, string> _written = new(StringComparer.Ordinal);

    /// <summary>Чаты, ждущие записи. Словарь, а не очередь: повторное сохранение заменяет прежнее.</summary>
    private readonly Dictionary<string, ChatSession> _pendingChats = new(StringComparer.Ordinal);

    private string? _pendingIndex;
    private bool _draining;
    private Task _drain = Task.CompletedTask;

    public ChatStore(string? rootDirectory = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppPaths.Root
            : rootDirectory;
        _chatsDirectory = Path.Combine(_root, "chats");
        _indexFile = Path.Combine(_chatsDirectory, "index.json");
    }

    public ChatSession CreateNew(string? selectedModelId = null)
    {
        var now = DateTime.Now;
        return new ChatSession
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = ChatTitle.Default,
            CreatedAt = now,
            UpdatedAt = now,
            SelectedModelId = selectedModelId ?? ""
        };
    }

    /// <summary>
    /// Ставит чат в очередь на запись и правит опись. Возвращается сразу: сериализация и диск —
    /// в фоновой задаче.
    /// </summary>
    public void Save(ChatSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            throw new ArgumentException("Chat session id is required.", nameof(session));
        }

        // UpdatedAt belongs to whoever actually changes the session — ChatEngine and
        // ChatSessionEdit already stamp it on every append, edit and delete. Stamping it here as
        // well made an ordinary save indistinguishable from an edit, so merely opening a chat
        // (which saves the one being left) dragged that chat into today's sidebar group.
        if (session.UpdatedAt == default)
        {
            session.UpdatedAt = session.CreatedAt == default ? DateTime.Now : session.CreatedAt;
        }

        lock (_gate)
        {
            _pendingChats[session.Id] = session;
        }

        UpsertIndex(session);
        StartDrain();
    }

    /// <summary>Дожидается, пока всё отложенное ляжет на диск.</summary>
    /// <remarks>
    /// Зовётся там, где дальше файлы читает или правит кто-то другой: закрытие окна, смена
    /// профиля, экспорт и импорт данных, удаление чата. Ждать тут нечего в подавляющем
    /// большинстве случаев — очередь пуста, и вызов возвращается сразу.
    /// </remarks>
    public void Flush()
    {
        // Три попытки, потому что сериализация может наткнуться на чат, который прямо сейчас
        // правит идущий ход, и тогда он возвращается в очередь (см. TryWriteChat).
        for (var attempt = 0; attempt < 3; attempt++)
        {
            StartDrain();

            Task drain;
            lock (_gate)
            {
                if (!_draining && _pendingChats.Count == 0 && _pendingIndex is null)
                {
                    return;
                }

                drain = _drain;
            }

            drain.Wait(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Забыть всё, что помнится о диске. Нужно после импорта данных: он подменяет файлы чатов
    /// в обход хранилища, и опись в памяти после него описывает уже не то, что лежит на диске.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _index = null;
            _sorted = null;
            _written.Clear();
        }
    }

    public ChatSession? TryLoad(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        // Чат мог ещё не доехать до диска. Файл — единственный источник для чтения, поэтому
        // сначала дописываем очередь, а уже потом читаем.
        bool pending;
        lock (_gate)
        {
            pending = _pendingChats.ContainsKey(id);
        }

        if (pending)
        {
            Flush();
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
        lock (_gate)
        {
            return _sorted ??= Sort(LoadIndexLocked().Items);
        }
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

    /// <summary>
    /// Retitles a chat without touching <see cref="ChatSession.UpdatedAt"/> — renaming is
    /// housekeeping, not a new message, so it must not reshuffle the sidebar.
    /// </summary>
    public bool Rename(string id, string title)
    {
        var trimmed = title?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(id) || trimmed.Length == 0)
        {
            return false;
        }

        var session = TryLoad(id);
        if (session is null)
        {
            return false;
        }

        session.Title = trimmed;
        Save(session);
        return true;
    }

    /// <summary>Pins or unpins a chat. Index-only, so the conversation file is untouched.</summary>
    public bool SetPinned(string id, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        lock (_gate)
        {
            var index = LoadIndexLocked();
            var entry = index.Items.FirstOrDefault(item => item.Id == id);
            if (entry is null || entry.IsPinned == pinned)
            {
                return false;
            }

            entry.IsPinned = pinned;
            SaveIndexLocked(index);
        }

        StartDrain();
        return true;
    }

    public bool Delete(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        // Сначала очередь: иначе отложенная запись воскресила бы только что удалённый файл.
        lock (_gate)
        {
            _pendingChats.Remove(id);
            _written.Remove(id);
        }

        Flush();

        var path = ChatPath(id);
        var existed = File.Exists(path);
        if (existed)
        {
            File.Delete(path);
        }

        bool removed;
        lock (_gate)
        {
            var index = LoadIndexLocked();
            removed = index.Items.RemoveAll(item => item.Id == id) > 0;
            if (removed)
            {
                SaveIndexLocked(index);
            }
        }

        StartDrain();
        return existed || removed;
    }

    public int DeleteAll()
    {
        lock (_gate)
        {
            _pendingChats.Clear();
            _written.Clear();
        }

        Flush();

        var count = List().Count;
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
        lock (_gate)
        {
            SaveIndexLocked(new ChatIndex());
        }

        Flush();
        return count;
    }

    private void UpsertIndex(ChatSession session)
    {
        lock (_gate)
        {
            var index = LoadIndexLocked();
            var entry = index.Items.FirstOrDefault(item => item.Id == session.Id);
            if (entry is null)
            {
                entry = new ChatIndexEntry { Id = session.Id };
                index.Items.Add(entry);
            }
            else if (entry.Title == session.Title &&
                     entry.UpdatedAt == session.UpdatedAt &&
                     entry.Summary == session.Summary)
            {
                // Опись уже описывает этот чат верно. Прежде она перезаписывалась при каждом
                // сохранении, то есть дважды в секунду на протяжении всего ответа.
                return;
            }

            entry.Title = session.Title;
            entry.UpdatedAt = session.UpdatedAt;
            entry.Summary = session.Summary;
            SaveIndexLocked(index);
        }
    }

    private ChatIndex LoadIndexLocked()
    {
        if (_index is not null)
        {
            return _index;
        }

        if (!File.Exists(_indexFile))
        {
            return _index = new ChatIndex();
        }

        try
        {
            var text = File.ReadAllText(_indexFile);
            _index = JsonSerializer.Deserialize<ChatIndex>(text, AppJson.Options) ?? new ChatIndex();
        }
        catch
        {
            _index = new ChatIndex();
        }

        return _index;
    }

    private void SaveIndexLocked(ChatIndex index)
    {
        index.Items = [.. Sort(index.Items)];
        _index = index;
        _sorted = null;
        _pendingIndex = JsonSerializer.Serialize(index, AppJson.Options) + Environment.NewLine;
    }

    private static List<ChatIndexEntry> Sort(IEnumerable<ChatIndexEntry> items) =>
    [
        .. items
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.UpdatedAt)
    ];

    private void StartDrain()
    {
        lock (_gate)
        {
            if (_draining || (_pendingChats.Count == 0 && _pendingIndex is null))
            {
                return;
            }

            _draining = true;
            _drain = Task.Run(Drain);
        }
    }

    /// <summary>Разгребает очередь записи. Работает вне потока диспетчера и по одному файлу за раз.</summary>
    private void Drain()
    {
        try
        {
            DrainCore();
        }
        catch (Exception ex)
        {
            // Задача фоновая: её отказ иначе всплыл бы как необработанное исключение и унёс бы
            // программу. Флаг обязан сняться, иначе очередь замрёт до перезапуска.
            PerfLog.Write("chat_store drain_failed " + ex.Message);
            lock (_gate)
            {
                _draining = false;
            }
        }
    }

    private void DrainCore()
    {
        while (true)
        {
            string? indexJson;
            string? chatId = null;
            ChatSession? chat = null;

            lock (_gate)
            {
                indexJson = _pendingIndex;
                _pendingIndex = null;

                if (indexJson is null)
                {
                    if (_pendingChats.Count == 0)
                    {
                        _draining = false;
                        return;
                    }

                    (chatId, chat) = _pendingChats.First();
                    _pendingChats.Remove(chatId);
                }
            }

            if (indexJson is not null)
            {
                WriteQuietly(_indexFile, indexJson);
                continue;
            }

            if (!TryWriteChat(chatId!, chat!))
            {
                // Чат прямо сейчас правит идущий ход. Возвращаем его в очередь и уходим:
                // следующее сохранение — а ход сохраняется и по таймеру, и в конце —
                // запустит проход заново, когда переписка уже не будет меняться.
                lock (_gate)
                {
                    _pendingChats.TryAdd(chatId!, chat!);
                    _draining = false;
                }

                return;
            }
        }
    }

    private bool TryWriteChat(string id, ChatSession session)
    {
        string json;
        try
        {
            json = JsonSerializer.Serialize(session, AppJson.Options) + Environment.NewLine;
        }
        catch (InvalidOperationException)
        {
            // Движок правит списки сообщения из параллельных задач инструментов, и сериализатор
            // на меняющейся коллекции бросает.
            return false;
        }

        var stamp = Fingerprint(json);
        lock (_gate)
        {
            if (_written.TryGetValue(id, out var previous) && previous == stamp)
            {
                // Переключение чата сохраняет тот, из которого ушли, независимо от того, менялся
                // он или нет. Переписывать нетронутую переписку — с картинками в base64 внутри —
                // чистая трата диска.
                return true;
            }
        }

        if (!WriteQuietly(ChatPath(id), json))
        {
            return true;
        }

        lock (_gate)
        {
            _written[id] = stamp;
        }

        return true;
    }

    private static bool WriteQuietly(string path, string json)
    {
        try
        {
            AppDataFile.WriteAtomic(path, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Диск переполнен, файл занят антивирусом, папка стала недоступной. Ронять программу
            // из-за неудавшейся записи нельзя: переписка есть в памяти, и следующая попытка
            // придёт через полсекунды.
            return false;
        }
    }

    private static string Fingerprint(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private string ChatPath(string id) => Path.Combine(_chatsDirectory, $"{id}.json");
}
