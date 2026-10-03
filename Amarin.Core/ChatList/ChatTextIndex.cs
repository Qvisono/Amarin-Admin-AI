using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Находка поиска по всем чатам: чат, сообщение и кусок текста вокруг совпадения.</summary>
public sealed record TextSearchHit(
    string ChatId,
    string Title,
    string MessageId,
    DateTime Created,
    string Snippet,
    int MatchStart,
    int MatchLength);

/// <summary>
/// Текст всех чатов профиля для поиска «по тексту» (D1): в памяти и файлом <c>chats/search.idx</c>.
/// </summary>
/// <remarks>
/// <para>
/// Файл — чтобы на запуске не читать все переписки подряд: сверяется время изменения из описи,
/// и перечитываются только изменившиеся чаты. При включённом шифровании чатов файл шифруется тем
/// же DPAPI — иначе он стал бы открытой копией всех переписок рядом с зашифрованными.
/// </para>
/// <para>
/// В индекс идёт текст сообщений человека и ответов — то, что человек видит и ищет. Блоки
/// инструментов не входят: их бывает на порядки больше, а для них есть поиск внутри чата.
/// </para>
/// </remarks>
internal sealed class ChatTextIndex
{
    internal const string FileName = "search.idx";

    /// <summary>Сколько находок отдавать: дальше — уточнять запрос.</summary>
    public const int Limit = 200;

    private const int SnippetRadius = 60;

    /// <summary>
    /// Как писать файл: без отступов и без <c>\uXXXX</c> вместо кириллицы. Файл — кэш, его не
    /// читают глазами, а с настройками остальных файлов русский текст занимал вшестеро больше
    /// места и сериализовался во столько же раз дольше. Читается файл любым разбором — и прежний.
    /// </summary>
    private static readonly JsonSerializerOptions FileOptions = new(AppJson.Options)
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private long _version;
    private readonly Lock _fileGate = new();
    private string _chatsDirectory;
    private Func<bool> _encrypt;
    private Timer? _saveTimer;

    public ChatTextIndex(string root, Func<bool> encrypt)
    {
        _chatsDirectory = Path.Combine(root, "chats");
        _encrypt = encrypt;
    }

    public sealed record Line(string MessageId, DateTime Created, string Text);

    public sealed record Entry(string ChatId, string Title, DateTime UpdatedAt, List<Line> Lines);

    private sealed class Stored
    {
        public int Version { get; set; } = 1;

        public List<Entry> Chats { get; set; } = [];
    }

    public int Count => _entries.Count;

    /// <summary>
    /// Растёт с каждой правкой содержимого. По нему окно узнаёт, что показанная выдача ещё
    /// верна, и не ищет заново на каждую перерисовку списка, пока соседний чат отвечает.
    /// </summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>Переводит индекс на другой профиль; прежний сохраняется, новый читается с диска.</summary>
    public void UseRoot(string root, Func<bool> encrypt)
    {
        SaveNow();
        _entries.Clear();
        _chatsDirectory = Path.Combine(root, "chats");
        _encrypt = encrypt;
        Interlocked.Increment(ref _version);
    }

    /// <summary>Чат сохранён — его текст в индексе обновляется.</summary>
    public void Update(ChatSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Id))
        {
            return;
        }

        _entries[session.Id] = FromSession(session);
        Interlocked.Increment(ref _version);
        ScheduleSave();
    }

    public void Remove(string chatId)
    {
        if (_entries.TryRemove(chatId, out _))
        {
            Interlocked.Increment(ref _version);
            ScheduleSave();
        }
    }

    internal static Entry FromSession(ChatSession session)
    {
        // Под замком сессии: ход дописывает сообщения из фона, и перебор без замка мог бы
        // упасть на изменившемся списке.
        lock (session.Gate)
        {
            var lines = session.Messages
                .Where(message => message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Text))
                .Select(message => new Line(message.Id, message.CreatedAt, message.Text))
                .ToList();
            return new Entry(session.Id, session.Title, session.UpdatedAt, lines);
        }
    }

    /// <summary>
    /// Сверяет индекс с описью: читает файл, дочитывает изменившиеся чаты, выбрасывает удалённые.
    /// Для рабочего потока.
    /// </summary>
    public void Build(IReadOnlyList<ChatIndexEntry> chats, Func<string, ChatSession?> load, CancellationToken cancellationToken)
    {
        foreach (var entry in Read())
        {
            _entries.TryAdd(entry.ChatId, entry);
        }

        var alive = chats.ToDictionary(chat => chat.Id, StringComparer.Ordinal);
        foreach (var id in _entries.Keys.Where(id => !alive.ContainsKey(id)).ToList())
        {
            _entries.TryRemove(id, out _);
        }

        Interlocked.Increment(ref _version);

        var changed = false;
        foreach (var chat in chats)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_entries.TryGetValue(chat.Id, out var known) && known.UpdatedAt == chat.UpdatedAt)
            {
                continue;
            }

            if (load(chat.Id) is { } session)
            {
                _entries[chat.Id] = FromSession(session);
                changed = true;
            }
        }

        if (changed)
        {
            Interlocked.Increment(ref _version);
            SaveNow();
        }
    }

    /// <summary>Находки по всем чатам, свежие первыми.</summary>
    /// <remarks>
    /// Можно звать с рабочего потока, пока индекс обновляется: записи не правятся на месте, а
    /// подменяются целиком. Окно так и делает — на сотнях чатов просмотр всего текста занимает
    /// заметное время, и на потоке окна он задерживал каждую набранную букву.
    /// </remarks>
    public IReadOnlyList<TextSearchHit> Search(string? query, CancellationToken cancellationToken = default)
    {
        var needle = query?.Trim() ?? "";
        var hits = new List<TextSearchHit>();
        if (needle.Length < 2)
        {
            return hits;
        }

        foreach (var entry in _entries.Values.OrderByDescending(entry => entry.UpdatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var line in Enumerable.Reverse(entry.Lines))
            {
                var at = line.Text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);
                if (at < 0)
                {
                    continue;
                }

                var (snippet, start) = Snippet(line.Text, at, needle.Length);
                hits.Add(new TextSearchHit(entry.ChatId, entry.Title, line.MessageId, line.Created, snippet, start, needle.Length));
                if (hits.Count >= Limit)
                {
                    return hits;
                }
            }
        }

        return hits;
    }

    /// <summary>Кусок вокруг совпадения в одну строку — с многоточиями, если текст обрезан.</summary>
    internal static (string Snippet, int MatchStart) Snippet(string text, int at, int length)
    {
        var from = Math.Max(0, at - SnippetRadius);
        var to = Math.Min(text.Length, at + length + SnippetRadius);
        var builder = new StringBuilder();
        if (from > 0)
        {
            builder.Append('…');
        }

        var start = builder.Length + (at - from);
        builder.Append(text.AsSpan(from, to - from));
        if (to < text.Length)
        {
            builder.Append('…');
        }

        // Переводы строк — в пробелы: выдача однострочная, а длина не меняется, и начало
        // совпадения остаётся на месте.
        builder.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return (builder.ToString(), start);
    }

    private IEnumerable<Entry> Read()
    {
        var path = Path.Combine(_chatsDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var bytes = File.ReadAllBytes(path);
            var json = AtRestCipher.IsEncrypted(bytes) ? AtRestCipher.DecryptFile(bytes) : Encoding.UTF8.GetString(bytes);
            return json is null ? [] : JsonSerializer.Deserialize<Stored>(json, AppJson.Options)?.Chats ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private void ScheduleSave()
    {
        // Пять секунд тишины: во время ответа чат сохраняется по два раза в секунду.
        var timer = _saveTimer ??= new Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    public void SaveNow()
    {
        lock (_fileGate)
        {
            try
            {
                var stored = new Stored { Chats = [.. _entries.Values] };
                var bytes = _encrypt()
                    ? AtRestCipher.EncryptFile(JsonSerializer.Serialize(stored, FileOptions))
                    : JsonSerializer.SerializeToUtf8Bytes(stored, FileOptions);
                if (bytes is null || !Directory.Exists(_chatsDirectory))
                {
                    return;
                }

                AppDataFile.WriteAtomicBytes(Path.Combine(_chatsDirectory, FileName), bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Индекс — производное: не записался — соберётся заново на следующем запуске.
            }
        }
    }
}
