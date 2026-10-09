using System.Collections.Concurrent;

namespace Amarin.Tools;

/// <summary>
/// Что модель в каждом чате читала и что правит прямо сейчас: свежесть прочитанного и замок на файл.
/// </summary>
/// <remarks>
/// <para>
/// Правка по памяти — главная беда длинного чата: модель помнит файл таким, каким он был три
/// версии назад, и меняет то, чего там уже нет, или затирает правку человека. Поэтому правка и
/// перезапись существующего файла идут только после чтения в этом же чате, и только пока файл на
/// диске тот же, что прочитан (время записи и длина). Своя удачная правка штамп обновляет —
/// несколько правок подряд не требуют перечитывать файл.
/// </para>
/// <para>
/// Замок на путь держится на весь цикл «прочитать — проверить — записать»: вызовы одного раунда
/// идут параллельно, и две правки одного файла иначе теряли бы одну из них.
/// </para>
/// <para>
/// Служба корня композиции, а не статика: тест собирает свою, и чужие чтения ему не мешают.
/// Без чата (рецепт, прогон инструментов) свежесть не проверяется — там нет «прочитанного раньше».
/// </para>
/// </remarks>
internal sealed class FileToolState
{
    /// <summary>Сколько штампов помнить: дальше забываются самые старые.</summary>
    internal const int Capacity = 4096;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Session, string Path), Stamp> _reads = new(KeyComparer.Instance);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);
    private long _tick;

    /// <summary>Модель прочла файл в этом чате: запомнить, каким он был.</summary>
    public void NoteRead(string? session, string path) => Remember(session, path);

    /// <summary>Модель сама записала файл: он такой, каким она его сделала, — перечитывать незачем.</summary>
    public void NoteWritten(string? session, string path) => Remember(session, path);

    /// <summary>
    /// Можно ли править или перезаписать файл по тому, что модель о нём знает. Null — можно; иначе
    /// отказ для модели с тем, что делать.
    /// </summary>
    public string? CheckFresh(string? session, string path)
    {
        if (string.IsNullOrEmpty(session) || StampOf(path) is not { } now)
        {
            return null;
        }

        Stamp? read;
        lock (_gate)
        {
            read = _reads.TryGetValue((session, path), out var known) ? known : null;
        }

        if (read is null)
        {
            return $"{path} has not been read in this chat. Read it with read_file first and base the change on what you see now, not on memory.";
        }

        return read.Value.Same(now)
            ? null
            : $"{path} changed on disk after you read it (the user or another program saved it). Read it again with read_file and make the change on the current text.";
    }

    /// <summary>Замок на путь: до его освобождения второй вызов к тому же файлу ждёт.</summary>
    public async Task<IDisposable> LockAsync(string path, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(Normalize(path), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(gate);
    }

    private void Remember(string? session, string path)
    {
        if (string.IsNullOrEmpty(session) || StampOf(path) is not { } stamp)
        {
            return;
        }

        lock (_gate)
        {
            _reads[(session, path)] = stamp with { Tick = ++_tick };
            if (_reads.Count > Capacity)
            {
                // Полкорзины самых старых разом: перебор один раз на две тысячи записей, а не на каждую.
                foreach (var key in _reads.OrderBy(pair => pair.Value.Tick).Take(Capacity / 2).Select(pair => pair.Key).ToList())
                {
                    _reads.Remove(key);
                }
            }
        }
    }

    private static Stamp? StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new Stamp(info.LastWriteTimeUtc, info.Length, 0) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <param name="Tick">Порядок записи — по нему забываются самые старые.</param>
    private readonly record struct Stamp(DateTime WrittenUtc, long Length, long Tick)
    {
        public bool Same(Stamp other) => WrittenUtc == other.WrittenUtc && Length == other.Length;
    }

    private sealed class Release(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }

    private sealed class KeyComparer : IEqualityComparer<(string Session, string Path)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Session, string Path) x, (string Session, string Path) y) =>
            string.Equals(x.Session, y.Session, StringComparison.Ordinal) &&
            string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Session, string Path) key) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(key.Session), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path));
    }
}
