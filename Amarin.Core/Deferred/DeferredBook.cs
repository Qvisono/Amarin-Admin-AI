using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Отложенные задачи профиля: <c>deferred.json</c> рядом с настройками и ключ печати
/// <c>deferred.key</c>.
/// </summary>
/// <remarks>
/// <para>
/// Файл пишется через <see cref="GuardedJsonFile"/>: при каждой записи рядом остаётся
/// <c>.bak</c>, а повреждённый файл не затирается пустым списком, как было бы у простого чтения, —
/// он откладывается, и на место встаёт копия. Потерять задачу молча здесь хуже всего.
/// </para>
/// <para>
/// В памяти держится прочитанный список, наружу отдаются копии: таймер, инструмент и страница
/// читают его из разных потоков, а менять задачу можно только через <see cref="Update"/> под замком.
/// </para>
/// <para>
/// Печать (<see cref="Seal"/>) — HMAC под ключом, закрытым DPAPI этого пользователя Windows. Ею
/// подписано то, что выполнится без человека: задача из чужого архива, перенесённая с другого
/// компьютера или поправленная руками, печати не сойдётся и не выполнится. От программы, запущенной
/// тем же пользователем, это не защищает — граница та же, что у <c>settings.json</c>.
/// </para>
/// </remarks>
internal sealed class DeferredBook
{
    internal const string FileName = "deferred.json";
    internal const string KeyName = "deferred.key";

    /// <summary>Сколько законченных задач помнить для вкладки «Отложенные».</summary>
    internal const int HistoryKeep = 200;

    private static readonly JsonSerializerOptions Json = new(AppJson.Options) { WriteIndented = true };

    private readonly Lock _gate = new();
    private string _root;
    private List<DeferredTask>? _tasks;

    public DeferredBook(string root) => _root = root;

    /// <summary>Список изменился: таймер переставляет срок, вкладка перерисовывается.</summary>
    public event Action? Changed;

    public string Root
    {
        get
        {
            lock (_gate)
            {
                return _root;
            }
        }
    }

    /// <summary>Переводит книгу на папку другого профиля; прочитанное забывается.</summary>
    public void UseRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        lock (_gate)
        {
            _root = root;
            _tasks = null;
        }

        Changed?.Invoke();
    }

    /// <summary>Копии всех задач. Никогда не бросает: нечитаемый файл — пустой список, а не сбой.</summary>
    public IReadOnlyList<DeferredTask> Snapshot()
    {
        lock (_gate)
        {
            return [.. Loaded().Select(Clone)];
        }
    }

    public DeferredTask? Peek(string id)
    {
        lock (_gate)
        {
            return Loaded().FirstOrDefault(task => task.Id == id) is { } task ? Clone(task) : null;
        }
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return Loaded().Count(task => task.IsActive);
            }
        }
    }

    /// <summary>Добавляет задачу и отдаёт её копию с выданным идентификатором.</summary>
    public DeferredTask Add(DeferredTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        DeferredTask stored;
        lock (_gate)
        {
            var tasks = Loaded();
            stored = Clone(task);
            if (string.IsNullOrWhiteSpace(stored.Id) || tasks.Any(existing => existing.Id == stored.Id))
            {
                stored.Id = Guid.NewGuid().ToString("N")[..12];
            }

            tasks.Add(stored);
            SaveLocked(tasks);
            stored = Clone(stored);
        }

        Changed?.Invoke();
        return stored;
    }

    /// <summary>Меняет одну задачу под замком. False — такой задачи нет.</summary>
    public bool Update(string id, Action<DeferredTask> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var tasks = Loaded();
            if (tasks.FirstOrDefault(task => task.Id == id) is not { } task)
            {
                return false;
            }

            change(task);
            SaveLocked(tasks);
        }

        Changed?.Invoke();
        return true;
    }

    // ───────────────────────── печать ─────────────────────────

    /// <summary>Печать задачи: то, что выполнится без человека, — вид, команда, кто и когда поставил.</summary>
    public string Seal(DeferredTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        using var hmac = new HMACSHA256(Key());
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(Sealed(task))));
    }

    /// <summary>Сходится ли печать задачи с её содержимым.</summary>
    public bool Verify(DeferredTask task) =>
        task.Seal is { Length: > 0 } seal &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(seal), Convert.FromHexString(Seal(task)));

    private static string Sealed(DeferredTask task) =>
        string.Join('\n', task.Id, task.Kind, task.Command ?? "", task.CreatedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

    private byte[] Key()
    {
        var path = Path.Combine(Root, KeyName);
        lock (_gate)
        {
            try
            {
                if (File.Exists(path) && DataProtector.Unprotect(File.ReadAllText(path)) is { } stored)
                {
                    return Convert.FromBase64String(stored);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
            {
                // Ключ не читается — заводится новый: прежние печати не сойдутся, и задачи
                // спросят человека заново, а не выполнятся по неизвестно чьему разрешению.
            }

            var key = RandomNumberGenerator.GetBytes(32);
            if (DataProtector.Protect(Convert.ToBase64String(key)) is { } protectedKey)
            {
                AppDataFile.WriteAtomic(path, protectedKey);
            }

            return key;
        }
    }

    // ───────────────────────── файл ─────────────────────────

    private List<DeferredTask> Loaded()
    {
        if (_tasks is not null)
        {
            return _tasks;
        }

        var path = Path.Combine(_root, FileName);
        try
        {
            var state = GuardedJsonFile.Read(path, text => Parse(text) is not null, out var text);
            _tasks = state is GuardedFileState.Ok or GuardedFileState.RestoredFromBackup && text is not null
                ? Parse(text) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _tasks = [];
        }

        return _tasks;
    }

    private static List<DeferredTask>? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<DeferredFile>(text, Json)?.Tasks?.Where(task => !string.IsNullOrWhiteSpace(task.Id)).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void SaveLocked(List<DeferredTask> tasks)
    {
        Trim(tasks);
        GuardedJsonFile.Write(Path.Combine(_root, FileName), JsonSerializer.Serialize(new DeferredFile { Tasks = tasks }, Json));
    }

    /// <summary>Законченных задач — не больше <see cref="HistoryKeep"/>, самые старые уходят.</summary>
    private static void Trim(List<DeferredTask> tasks)
    {
        var finished = tasks.Where(task => !task.IsActive && task.AwaitingAckSinceUtc is null)
            .OrderByDescending(task => task.LastFiredUtc ?? task.CreatedUtc)
            .Skip(HistoryKeep)
            .ToHashSet();
        if (finished.Count > 0)
        {
            tasks.RemoveAll(finished.Contains);
        }
    }

    private static DeferredTask Clone(DeferredTask task) =>
        JsonSerializer.Deserialize<DeferredTask>(JsonSerializer.Serialize(task, Json), Json) ?? new DeferredTask();

    private sealed class DeferredFile
    {
        public int Version { get; set; } = 1;

        public List<DeferredTask> Tasks { get; set; } = [];
    }
}
