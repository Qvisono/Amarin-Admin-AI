using System.Text.Json;
using Amarin.Core;

namespace Amarin.Fox;

/// <summary>
/// Память Розы на диске: <c>fox/state.json</c> и <c>fox/diary.json</c> в папке профиля.
/// </summary>
/// <remarks>
/// <para>
/// Состояние меняется часто — каждое поглаживание, каждое сообщение, — поэтому пишется не сразу,
/// а через паузу после последней перемены, на рабочем потоке. Закрытие программы и смена профиля
/// сбрасывают отложенное сразу (<see cref="Flush"/>).
/// </para>
/// <para>
/// Шифруется тем же DPAPI, что и переписка (<see cref="AppSettings.EncryptChats"/>): дневник
/// пересказывает разговоры, и лежать открытым рядом с зашифрованными чатами ему незачем.
/// Повреждённый файл не роняет Розу: он откладывается рядом (<c>…broken-…</c>, архив такие не
/// берёт), а Роза начинает знакомство заново.
/// </para>
/// <para>
/// Пока идёт импорт архива (<see cref="BeginImport"/>), запись стоит: иначе живая Роза затёрла бы
/// только что приехавшее состояние своей копией из памяти. После импорта память сбрасывается и
/// перечитывается с диска.
/// </para>
/// </remarks>
internal sealed class FoxStore : IDisposable
{
    /// <summary>Пауза между последней переменой и записью.</summary>
    internal static readonly TimeSpan DefaultWriteDelay = TimeSpan.FromSeconds(3);

    private readonly TimeSpan _writeDelay;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private string _root;
    private Func<bool> _encrypt;
    private FoxState? _state;
    private List<FoxDiaryEntry>? _diary;
    private bool _stateDirty;
    private bool _diaryDirty;
    private int _importing;

    /// <param name="writeDelay">Пауза перед записью; тестам — короче заводской.</param>
    public FoxStore(string root, Func<bool> encrypt, TimeSpan? writeDelay = null)
    {
        _root = root;
        _encrypt = encrypt;
        _writeDelay = writeDelay ?? DefaultWriteDelay;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Смена профиля: отложенное записывается в прежнюю папку, память забывается.</summary>
    public void UseRoot(string root, Func<bool> encrypt)
    {
        Flush();
        lock (_gate)
        {
            _root = root;
            _encrypt = encrypt;
            _state = null;
            _diary = null;
        }
    }

    /// <summary>Копия состояния — для чтения; менять — через <see cref="Update"/>.</summary>
    public FoxState Snapshot()
    {
        lock (_gate)
        {
            return Clone(LoadedState());
        }
    }

    /// <summary>Меняет состояние под замком и ставит запись в очередь.</summary>
    public void Update(Action<FoxState> change)
    {
        lock (_gate)
        {
            change(LoadedState());
            _stateDirty = true;
        }

        Schedule();
    }

    /// <summary>Дневник по дате, от старых записей к новым.</summary>
    public IReadOnlyList<FoxDiaryEntry> Diary()
    {
        lock (_gate)
        {
            return [.. LoadedDiary()];
        }
    }

    /// <summary>Добавляет запись дневника; запись за тот же день заменяется.</summary>
    public void Write(FoxDiaryEntry entry)
    {
        lock (_gate)
        {
            var diary = LoadedDiary();
            diary.RemoveAll(existing => existing.Date == entry.Date);
            diary.Add(entry);
            diary.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
            _diaryDirty = true;
        }

        Schedule();
    }

    /// <summary>«Начать дружбу заново»: знакомство с нуля. Дневник остаётся — это память человека.</summary>
    public void Reset(DateTime now)
    {
        lock (_gate)
        {
            var fresh = new FoxState { MetAt = now, LastSeenAt = now, LastAttentionAt = now };
            fresh.Today.Date = LoadedState().Today.Date;
            fresh.Today.AffectionAtStart = fresh.Affection;
            _state = fresh;
            _stateDirty = true;
        }

        Schedule();
    }

    /// <summary>Записывает отложенное сейчас. Безопасно звать с любого потока.</summary>
    public void Flush()
    {
        string? stateJson = null;
        string? diaryJson = null;
        string root;
        bool encrypt;
        lock (_gate)
        {
            if (_importing > 0)
            {
                return;
            }

            if (_stateDirty && _state is not null)
            {
                stateJson = JsonSerializer.Serialize(_state, AppJson.Options);
                _stateDirty = false;
            }

            if (_diaryDirty && _diary is not null)
            {
                diaryJson = JsonSerializer.Serialize(_diary, AppJson.Options);
                _diaryDirty = false;
            }

            root = _root;
            encrypt = _encrypt();
        }

        // Сам диск — вне замка: Update с потока окна не должен ждать запись файла.
        if (stateJson is not null && !FoxData.Write(FoxData.StatePath(root), stateJson, encrypt))
        {
            MarkDirty(state: true);
        }

        if (diaryJson is not null && !FoxData.Write(FoxData.DiaryPath(root), diaryJson, encrypt))
        {
            MarkDirty(state: false);
        }
    }

    /// <summary>Импорт архива начинается: отложенное — на диск, дальше запись стоит.</summary>
    public void BeginImport()
    {
        Flush();
        lock (_gate)
        {
            _importing++;
        }
    }

    /// <summary>Импорт закончен: память забывается и перечитывается с диска при первом обращении.</summary>
    public void EndImport()
    {
        lock (_gate)
        {
            _importing = Math.Max(0, _importing - 1);
            _state = null;
            _diary = null;
            _stateDirty = false;
            _diaryDirty = false;
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }

    private void Schedule() => _timer.Change(_writeDelay, Timeout.InfiniteTimeSpan);

    private void MarkDirty(bool state)
    {
        lock (_gate)
        {
            if (state)
            {
                _stateDirty = true;
            }
            else
            {
                _diaryDirty = true;
            }
        }
    }

    private FoxState LoadedState()
    {
        if (_state is not null)
        {
            return _state;
        }

        var path = FoxData.StatePath(_root);
        _state = Parse<FoxState>(path) ?? new FoxState();
        _state.Milestones ??= [];
        _state.Today ??= new FoxDay();
        _state.Unwritten ??= [];
        _state.Today.Topics ??= new Dictionary<string, int>(StringComparer.Ordinal);
        _state.Today.Events ??= [];
        return _state;
    }

    private List<FoxDiaryEntry> LoadedDiary()
    {
        if (_diary is not null)
        {
            return _diary;
        }

        _diary = Parse<List<FoxDiaryEntry>>(FoxData.DiaryPath(_root)) ?? [];
        _diary.RemoveAll(entry => entry is null || string.IsNullOrWhiteSpace(entry.Date));
        _diary.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
        return _diary;
    }

    /// <summary>
    /// Читает файл; повреждённый откладывает рядом и отвечает <c>null</c>. Файл, зашифрованный
    /// не для этой учётной записи, не трогается: его владелец, может быть, ещё вернётся.
    /// </summary>
    private static T? Parse<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        if (FoxData.Read(path) is not { } text)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, AppJson.Options);
        }
        catch (JsonException)
        {
            try
            {
                File.Move(path, path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return null;
        }
    }

    private static FoxState Clone(FoxState state) =>
        JsonSerializer.Deserialize<FoxState>(JsonSerializer.Serialize(state, AppJson.Options), AppJson.Options) ?? new FoxState();
}
