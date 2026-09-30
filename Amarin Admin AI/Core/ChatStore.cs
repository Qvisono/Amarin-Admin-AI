using System.Collections.Concurrent;
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

    /// <summary>Набор вложений, записанный у чата последним: убирать сирот — только когда он сменился.</summary>
    private readonly Dictionary<string, string> _blobSets = new(StringComparer.Ordinal);

    private string? _pendingIndex;
    private bool _draining;

    /// <summary>
    /// Чат, который фоновая запись уже сняла с очереди, но ещё не положила на диск.
    /// </summary>
    /// <remarks>
    /// Без этого <see cref="TryLoad"/> в это окно не находил чат ни в очереди, ни на диске и
    /// отвечал «такого нет» — переименование сразу после сохранения молча не срабатывало.
    /// </remarks>
    private string? _inFlightChat;
    private Task _drain = Task.CompletedTask;

    /// <summary>Зовётся фоновой записью прямо перед записью файла чата. Только для тестов.</summary>
    internal Action<string>? WritingChat { get; set; }

    /// <summary>Через сколько повторить запись чата, который не удалось сериализовать.</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// Замок на файл: фоновая запись и перешифровка одного файла идут по очереди.
    /// </summary>
    /// <remarks>
    /// Перешифровка читает файл и пишет его обратно. Без замка между этими шагами успевала бы
    /// лечь свежая запись из очереди, и перешифровка затёрла бы её старой копией.
    /// </remarks>
    private readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Идущая перешифровка; отменяется новой и сменой профиля.</summary>
    private CancellationTokenSource? _reformat;

    /// <summary>
    /// Шифровать ли файлы при записи (<see cref="AppSettings.EncryptChats"/>).
    /// </summary>
    /// <remarks>
    /// Функцией, а не значением: галочку меняют на ходу, а хранилище живёт весь сеанс. Читаются
    /// оба формата всегда, так что смена значения ничего не делает нечитаемым.
    /// </remarks>
    public Func<bool> Encrypt { get; init; } = static () => false;

    /// <summary>Чат поставлен в очередь на запись — индекс поиска по тексту обновляет его.</summary>
    public event Action<ChatSession>? Saved;

    /// <summary>Чат удалён.</summary>
    public event Action<string>? Deleted;

    public ChatStore(string? rootDirectory = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppPaths.Root
            : rootDirectory;
        _chatsDirectory = Path.Combine(_root, "chats");
        _indexFile = Path.Combine(_chatsDirectory, "index.json");
    }

    /// <summary>Папка чатов этого профиля — для «Крупнейших чатов» (F3).</summary>
    internal string Folder => _chatsDirectory;

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
        Saved?.Invoke(session);
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
            pending = _pendingChats.ContainsKey(id) ||
                      string.Equals(_inFlightChat, id, StringComparison.Ordinal);
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
            return ReadText(path) is { } text
                ? JsonSerializer.Deserialize<ChatSession>(InlineAttachments(id, text), AppJson.Options)
                : null;
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
        bool existed;
        lock (FileLock(path))
        {
            existed = File.Exists(path);
            if (existed)
            {
                File.Delete(path);
            }

            // Вложения чата (F4) — вместе с ним.
            var folder = Path.Combine(_chatsDirectory, id);
            if (IsSafeId(id) && Directory.Exists(ChatAttachmentFiles.FolderOf(_chatsDirectory, id)))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        lock (_gate)
        {
            _blobSets.Remove(id);
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
        Deleted?.Invoke(id);
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
                    lock (FileLock(file))
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // continue wiping the rest
                }
            }

            // Папки вложений (F4): только те, где и правда лежат вложения, — другие папки не наши.
            foreach (var folder in Directory.GetDirectories(_chatsDirectory))
            {
                if (Directory.Exists(Path.Combine(folder, ChatAttachmentFiles.FolderName)))
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
        }

        lock (_gate)
        {
            _blobSets.Clear();
        }

        Directory.CreateDirectory(_chatsDirectory);
        lock (_gate)
        {
            SaveIndexLocked(new ChatIndex());
        }

        Flush();
        return count;
    }

    /// <summary>
    /// Дописывает в опись поля, которых у записей прежних версий нет: дату начала и цену чата
    /// целиком (D5, E3). Читает только такие чаты и только раз — дальше их поля обновляет
    /// обычное сохранение. Для рабочего потока.
    /// </summary>
    public int BackfillIndex(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var entry in List().Where(entry => entry.CreatedAt == default))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryLoad(entry.Id) is { } session)
            {
                UpsertIndex(session);
                count++;
            }
        }

        return count;
    }

    private void UpsertIndex(ChatSession session)
    {
        // Вне замка хранилища: цена считается под замком сессии, а брать их в обратном порядке
        // где-то ещё было бы приглашением к взаимной блокировке.
        var cost = ChatCost.Total(session);
        // У очень старых переписок даты начала нет — берём последнюю: иначе дозаполнение
        // описи (BackfillIndex) перечитывало бы такой чат на каждом запуске.
        var created = session.CreatedAt == default ? session.UpdatedAt : session.CreatedAt;
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
                     entry.Summary == session.Summary &&
                     entry.CreatedAt == created &&
                     entry.TotalCost == cost)
            {
                // Опись уже описывает этот чат верно. Прежде она перезаписывалась при каждом
                // сохранении, то есть дважды в секунду на протяжении всего ответа.
                return;
            }

            entry.Title = session.Title;
            entry.UpdatedAt = session.UpdatedAt;
            entry.Summary = session.Summary;
            entry.CreatedAt = created;
            entry.TotalCost = cost;
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
            var text = ReadText(_indexFile);
            _index = text is null
                ? new ChatIndex()
                : JsonSerializer.Deserialize<ChatIndex>(text, AppJson.Options) ?? new ChatIndex();
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
                _inFlightChat = null;
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
                    _inFlightChat = chatId;
                }
            }

            if (indexJson is not null)
            {
                WriteQuietly(_indexFile, indexJson, Encrypt());
                continue;
            }

            var written = TryWriteChat(chatId!, chat!);
            lock (_gate)
            {
                _inFlightChat = null;
            }

            if (!written)
            {
                // Чат прямо сейчас правит идущий ход. Возвращаем его в очередь и уходим:
                // следующее сохранение — а ход сохраняется и по таймеру, и в конце —
                // запустит проход заново, когда переписка уже не будет меняться.
                lock (_gate)
                {
                    _pendingChats.TryAdd(chatId!, chat!);
                    _draining = false;
                }

                // Страховка на случай, когда следующего сохранения нет: правка вне хода
                // (переключение варианта) сохраняется один раз, и без повтора чат остался бы
                // лежать в очереди до закрытия программы.
                _ = Task.Delay(RetryDelay).ContinueWith(_ => StartDrain(), TaskScheduler.Default);
                return;
            }
        }
    }

    private bool TryWriteChat(string id, ChatSession session)
    {
        string json;
        try
        {
            // Под замком сессии: вне хода списки меняют только операции с вариантами ответа, и
            // перестановка хвостов посреди обхода дала бы файл, где лента от одной ветки, а
            // история модели — от другой.
            lock (session.Gate)
            {
                json = JsonSerializer.Serialize(session, AppJson.Options) + Environment.NewLine;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            // Движок правит списки сообщения из параллельных задач инструментов, и сериализатор
            // на меняющейся коллекции бросает — не только InvalidOperationException: список он
            // пишет по индексу, и укоротившийся посреди обхода даёт ArgumentOutOfRangeException.
            // Раньше такое исключение роняло весь проход, и чат, уже снятый с очереди, терялся.
            PerfLog.Write($"chat_store serialize_retry {ex.GetType().Name}");
            return false;
        }

        // Формат — часть отпечатка: включённое шифрование обязано переписать и нетронутый чат.
        var encrypt = Encrypt();
        var stamp = Fingerprint((encrypt ? "E" : "P") + json);
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

        WritingChat?.Invoke(id);
        if (!WriteChatQuietly(id, json, encrypt))
        {
            return true;
        }

        lock (_gate)
        {
            _written[id] = stamp;
        }

        return true;
    }

    /// <summary>
    /// Пишет чат: сначала вынесенные вложения (F4), потом сам файл со ссылками, потом убирает
    /// вложения, на которые чат больше не ссылается. В таком порядке сбой посередине оставляет
    /// лишний файл, а не ссылку в никуда.
    /// </summary>
    private bool WriteChatQuietly(string id, string json, bool encrypt)
    {
        var path = ChatPath(id);
        var stored = ChatAttachmentFiles.Externalize(json, out var blobs);
        try
        {
            lock (FileLock(path))
            {
                if (!WriteBlobs(id, blobs, encrypt))
                {
                    return false;
                }

                if (!WriteQuietly(path, stored, encrypt))
                {
                    return false;
                }

                RemoveOrphans(id, blobs);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Кладёт вложения, которых ещё нет. Файл назван отпечатком содержимого, поэтому уже
    /// лежащий не переписывается: иначе картинка писалась бы на диск на каждом сохранении хода.
    /// </summary>
    private bool WriteBlobs(string id, IReadOnlyList<AttachmentBlob> blobs, bool encrypt)
    {
        if (blobs.Count == 0 || !IsSafeId(id))
        {
            return blobs.Count == 0;
        }

        var folder = ChatAttachmentFiles.FolderOf(_chatsDirectory, id);
        Directory.CreateDirectory(folder);
        foreach (var blob in blobs)
        {
            var file = ChatAttachmentFiles.BlobPath(folder, blob.Sha);
            if (File.Exists(file))
            {
                continue;
            }

            // Отказ Windows шифровать — не повод класть картинку открытой вопреки галочке.
            var content = encrypt ? AtRestCipher.EncryptBytes(blob.Bytes) : blob.Bytes;
            if (content is null)
            {
                PerfLog.Write("chat_store encrypt_failed");
                return false;
            }

            AppDataFile.WriteAtomicBytes(file, content);
        }

        return true;
    }

    /// <summary>Убирает вложения, на которые чат больше не ссылается (сообщение удалили, ветку стёрли).</summary>
    private void RemoveOrphans(string id, IReadOnlyList<AttachmentBlob> blobs)
    {
        var key = string.Join(',', blobs.Select(blob => blob.Sha).Order(StringComparer.Ordinal));
        lock (_gate)
        {
            if (_blobSets.TryGetValue(id, out var previous) && previous == key)
            {
                return;
            }

            _blobSets[id] = key;
        }

        var folder = ChatAttachmentFiles.FolderOf(_chatsDirectory, id);
        if (!IsSafeId(id) || !Directory.Exists(folder))
        {
            return;
        }

        var keep = blobs.Select(blob => blob.Sha).ToHashSet(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(folder, "*.bin"))
        {
            if (!keep.Contains(Path.GetFileNameWithoutExtension(file)))
            {
                TryDeleteFile(file);
            }
        }
    }

    /// <summary>Разворачивает ссылки на вынесенные вложения; чат прежнего формата — как есть.</summary>
    private string InlineAttachments(string id, string text) =>
        !ChatAttachmentFiles.HasRefs(text) || !IsSafeId(id)
            ? text
            : ChatAttachmentFiles.Inline(text, sha => ReadBlob(ChatAttachmentFiles.FolderOf(_chatsDirectory, id), sha));

    internal static byte[]? ReadBlob(string folder, string sha)
    {
        try
        {
            var file = ChatAttachmentFiles.BlobPath(folder, sha);
            return File.Exists(file) ? AtRestCipher.DecryptBytes(File.ReadAllBytes(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Имя чата годится в путь: папка вложений строится из него.</summary>
    private static bool IsSafeId(string id) =>
        id.Length > 0 && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && !id.Contains("..", StringComparison.Ordinal);

    private static void TryDeleteFile(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Выносит вложения из чатов, записанных до F4, — байтами и под замком файла, как и
    /// перешифровка: разбор в <see cref="ChatSession"/> и сохранение затёрли бы свежий ответ
    /// идущего хода копией с диска. Формат файла (открытый или зашифрованный) остаётся прежним.
    /// </summary>
    /// <returns>Сколько чатов переписано.</returns>
    public int ExternalizeExisting(CancellationToken cancellationToken)
    {
        Flush();
        string[] files;
        try
        {
            files = Directory.Exists(_chatsDirectory) ? Directory.GetFiles(_chatsDirectory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        var count = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (string.Equals(id, "index", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                lock (FileLock(file))
                {
                    if (!File.Exists(file))
                    {
                        continue;
                    }

                    var bytes = File.ReadAllBytes(file);
                    if (AtRestCipher.DecryptFile(bytes) is not { } text)
                    {
                        continue;
                    }

                    var stored = ChatAttachmentFiles.Externalize(text, out var blobs);
                    if (blobs.Count == 0)
                    {
                        continue;
                    }

                    var encrypted = AtRestCipher.IsEncrypted(bytes);
                    if (WriteBlobs(id, blobs, encrypted) && WriteQuietly(file, stored, encrypted))
                    {
                        count++;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Занятый файл останется прежним — читается он и так.
            }
        }

        return count;
    }

    private bool WriteQuietly(string path, string json, bool encrypt)
    {
        try
        {
            lock (FileLock(path))
            {
                if (!encrypt)
                {
                    AppDataFile.WriteAtomic(path, json);
                    return true;
                }

                // Отказ Windows шифровать — не повод писать переписку открытым текстом вопреки
                // галочке: чат есть в памяти, и следующее сохранение попробует снова.
                if (AtRestCipher.EncryptFile(json) is not { } sealedBytes)
                {
                    PerfLog.Write("chat_store encrypt_failed");
                    return false;
                }

                AppDataFile.WriteAtomicBytes(path, sealedBytes);
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Диск переполнен, файл занят антивирусом, папка стала недоступной. Ронять программу
            // из-за неудавшейся записи нельзя: переписка есть в памяти, и следующая попытка
            // придёт через полсекунды.
            return false;
        }
    }

    /// <summary>Текст файла в любом из двух форматов; null — не расшифровывается здесь.</summary>
    private static string? ReadText(string path) => AtRestCipher.DecryptFile(File.ReadAllBytes(path));

    private object FileLock(string path) => _fileLocks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    /// <summary>
    /// Приводит файлы чатов и описи к текущему значению <see cref="Encrypt"/> — в фоне, по
    /// одному файлу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Файлы перекладываются байтами, а не через разбор в <see cref="ChatSession"/> и обратное
    /// сохранение: копия, прочитанная с диска, могла бы лечь поверх свежей записи идущего хода.
    /// Здесь же читается и пишется то, что на диске сейчас, под замком файла.
    /// </para>
    /// <para>
    /// Цель запоминается при вызове: после смены профиля <see cref="Encrypt"/> смотрит уже на
    /// настройки другого профиля. Новый вызов отменяет прежний проход. Файл, который здесь не
    /// расшифровать (перенесён с другой машины), остаётся как есть.
    /// </para>
    /// </remarks>
    /// <returns>Проход — ради тестов; программа его не ждёт.</returns>
    public Task EnsureFormat()
    {
        var target = Encrypt();
        var cancel = new CancellationTokenSource();
        Interlocked.Exchange(ref _reformat, cancel)?.Cancel();
        return Task.Run(() => Reformat(target, cancel.Token));
    }

    /// <summary>Останавливает перешифровку: хранилище больше не ведёт эту папку.</summary>
    public void StopReformat() => Interlocked.Exchange(ref _reformat, null)?.Cancel();

    private void Reformat(bool encrypt, CancellationToken cancellationToken)
    {
        string[] files;
        try
        {
            files = Directory.Exists(_chatsDirectory) ? Directory.GetFiles(_chatsDirectory, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Вложения — отдельно от самого чата: прерванный прежний проход мог перевести файл
            // чата и не успеть с его вложениями.
            ReformatBlobs(file, encrypt);

            try
            {
                lock (FileLock(file))
                {
                    // Файл удалили, пока до него шла очередь: воскрешать его нельзя.
                    if (!File.Exists(file) || HasFormat(file, encrypt))
                    {
                        continue;
                    }

                    if (AtRestCipher.DecryptFile(File.ReadAllBytes(file)) is not { } text)
                    {
                        continue;
                    }

                    if (encrypt)
                    {
                        if (AtRestCipher.EncryptFile(text) is { } sealedBytes)
                        {
                            AppDataFile.WriteAtomicBytes(file, sealedBytes);
                        }
                    }
                    else
                    {
                        AppDataFile.WriteAtomic(file, text);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Файл занят — останется в прежнем формате, читается он и так.
            }
        }
    }

    /// <summary>Вложения чата (F4) — в тот же формат, что и сам чат, под тем же замком.</summary>
    private void ReformatBlobs(string chatFile, bool encrypt)
    {
        var id = Path.GetFileNameWithoutExtension(chatFile);
        var folder = ChatAttachmentFiles.FolderOf(_chatsDirectory, id);
        if (!IsSafeId(id) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            lock (FileLock(chatFile))
            {
                foreach (var blob in Directory.GetFiles(folder, "*.bin"))
                {
                    if (HasFormat(blob, encrypt) || AtRestCipher.DecryptBytes(File.ReadAllBytes(blob)) is not { } plain)
                    {
                        continue;
                    }

                    if ((encrypt ? AtRestCipher.EncryptBytes(plain) : plain) is { } content)
                    {
                        AppDataFile.WriteAtomicBytes(blob, content);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Формат файла уже тот: смотрим только на первые байты, не читая чат целиком.</summary>
    private static bool HasFormat(string file, bool encrypted)
    {
        Span<byte> head = stackalloc byte[AtRestCipher.FileMagic.Length];
        using var stream = File.OpenRead(file);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return AtRestCipher.IsEncrypted(head[..read]) == encrypted;
    }

    private static string Fingerprint(string json) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private string ChatPath(string id) => Path.Combine(_chatsDirectory, $"{id}.json");
}
