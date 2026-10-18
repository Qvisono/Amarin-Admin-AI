using System.Diagnostics.CodeAnalysis;

namespace Amarin.Core;

/// <summary>
/// Кто сейчас держит мышь. Одна на процесс: мышь и клавиатура у машины одни, и два оператора
/// разом нажимали бы друг другу в окна.
/// </summary>
/// <remarks>Объектом, а не статикой: тесты берут свой и не мешают соседним.</remarks>
internal sealed class ComputerLease
{
    public static ComputerLease Shared { get; } = new();

    private readonly Lock _gate = new();
    private object? _holder;

    public bool TryTake(object owner)
    {
        lock (_gate)
        {
            if (_holder is not null && !ReferenceEquals(_holder, owner))
            {
                return false;
            }

            _holder = owner;
            return true;
        }
    }

    public void Release(object owner)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_holder, owner))
            {
                _holder = null;
            }
        }
    }

    public bool Held
    {
        get
        {
            lock (_gate)
            {
                return _holder is not null;
            }
        }
    }
}

/// <summary>
/// Управление в одном прогоне оператора: когда оно началось, какие id выданы элементам, какой
/// снимок последний и что показывать на плашке.
/// </summary>
/// <remarks>
/// <para>
/// Начало — первое пишущее действие или «Показать», а не первый взгляд: посмотреть на окна можно
/// и не сворачивая программу, а мышь берётся, только когда её правда пора брать. Без человека
/// перед началом идёт отсчёт, и отмена на нём закрывает управление до конца прогона.
/// </para>
/// <para>
/// Id элементов — сквозные на прогон: новое дерево окна заменяет id этого окна новыми, а не
/// начинает счёт заново. Иначе «e3» из дерева одного окна совпал бы с «e3» другого, и модель
/// нажала бы не туда.
/// </para>
/// </remarks>
internal sealed class ComputerSession : IDisposable
{
    private readonly IComputerStage? _stage;
    private readonly ComputerStart _start;
    private readonly ComputerLease _lease;
    private readonly Dictionary<string, Remembered> _elements = new(StringComparer.OrdinalIgnoreCase);
    private int _nextId;
    private Frame? _shot;
    private bool _cancelled;
    private IDisposable? _hold;

    private sealed record Remembered(long Window, ComputerElement Element);

    /// <param name="Window">Окно, которое снимали; null — снимали монитор.</param>
    private sealed record Frame(ComputerShot Shot, long? Window, ScreenRect WindowBounds);

    /// <param name="ownProcessId">Свой процесс. Параметром — ради тестов; по умолчанию этот.</param>
    public ComputerSession(
        IComputer computer,
        IComputerStage? stage,
        ComputerStart start,
        ComputerLease? lease = null,
        int? ownProcessId = null)
    {
        Computer = computer;
        _stage = stage;
        _start = start;
        _lease = lease ?? ComputerLease.Shared;
        OwnProcessId = ownProcessId ?? Environment.ProcessId;
    }

    public IComputer Computer { get; }

    public ComputerMode Mode => _start.Mode;

    public int OwnProcessId { get; }

    /// <summary>Сколько действий уже сделано — номер шага на плашке.</summary>
    public int Steps { get; private set; }

    public bool Started { get; private set; }

    /// <summary>Есть ли кому показывать: плашка и подсветка.</summary>
    public bool CanShow => _stage is not null;

    /// <summary>
    /// Берёт мышь, если ещё не взята. Null — можно действовать; иначе отказ для модели.
    /// </summary>
    public async Task<string?> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_cancelled)
        {
            return Loc.Get("S.Computer.Cancelled");
        }

        if (Started)
        {
            return null;
        }

        if (!_lease.TryTake(this))
        {
            return Loc.Get("S.Computer.Busy");
        }

        bool go;
        try
        {
            go = _stage is null || await _stage.BeginAsync(_start, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _lease.Release(this);
            throw;
        }

        if (!go)
        {
            // Отмена на отсчёте — ответ человека на всё управление в этом прогоне, а не на один шаг:
            // иначе следующий же вызов снова показал бы отсчёт.
            _cancelled = true;
            _lease.Release(this);
            return Loc.Get("S.Computer.Cancelled");
        }

        Started = true;
        _hold = Computer.Hold();
        return null;
    }

    /// <summary>Следующий шаг на плашке.</summary>
    public void Step(string text)
    {
        Steps++;
        if (Started)
        {
            _stage?.Step(Steps, text);
        }
    }

    public void Avoid(ScreenPoint point) => _stage?.Avoid(point);

    /// <summary>Рамка у области и ожидание «Готово». Null — показывать некому.</summary>
    public async Task<bool?> ShowAsync(ScreenRect area, string caption, TimeSpan wait, CancellationToken cancellationToken) =>
        _stage is null ? null : await _stage.ShowAsync(area, caption, wait, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Выдаёт элементам окна новые id. Прежние id этого окна перестают действовать: дерево могло
    /// поменяться, и старый id указал бы на другой элемент.
    /// </summary>
    public IReadOnlyList<(string Id, ComputerElement Element)> Remember(long window, IEnumerable<ComputerElement> elements)
    {
        foreach (var stale in _elements.Where(pair => pair.Value.Window == window).Select(pair => pair.Key).ToList())
        {
            _ = _elements.Remove(stale);
        }

        var named = new List<(string, ComputerElement)>();
        foreach (var element in elements)
        {
            var id = "e" + (++_nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _elements[id] = new Remembered(window, element);
            named.Add((id, element));
        }

        return named;
    }

    public bool TryElement(string? id, out long window, [NotNullWhen(true)] out ComputerElement? element)
    {
        if (!string.IsNullOrWhiteSpace(id) && _elements.TryGetValue(id.Trim(), out var remembered))
        {
            window = remembered.Window;
            element = remembered.Element;
            return true;
        }

        window = 0;
        element = null;
        return false;
    }

    public void RememberShot(ComputerShot shot, ComputerWindow? window) =>
        _shot = new Frame(shot, window?.Handle, window?.Bounds ?? default);

    /// <summary>
    /// Точка последнего снимка — в пиксели экрана. Null — можно нажимать; иначе отказ.
    /// </summary>
    /// <remarks>
    /// Окно, сдвинутое после снимка, — отказ, а не пересчёт: модель решала по картинке, на которой
    /// под этой точкой было одно, а сейчас там другое.
    /// </remarks>
    public string? FromShot(double x, double y, out ScreenPoint point)
    {
        point = default;
        if (_shot is not { } frame)
        {
            return Loc.Get("S.Computer.NoShot");
        }

        var shot = frame.Shot;
        if (double.IsNaN(x) || double.IsNaN(y) || x < 0 || y < 0 || x >= shot.Width || y >= shot.Height)
        {
            return Loc.Format("S.Computer.OutsideShot", shot.Width, shot.Height);
        }

        if (frame.Window is { } handle)
        {
            if (Computer.Window(handle) is not { } current)
            {
                return Loc.Get("S.Computer.WindowGone");
            }

            if (current.Bounds != frame.WindowBounds)
            {
                return Loc.Get("S.Computer.ShotStale");
            }
        }

        var source = shot.Source;
        var screenX = source.Left + (int)Math.Floor(x * source.Width / shot.Width);
        var screenY = source.Top + (int)Math.Floor(y * source.Height / shot.Height);
        point = new ScreenPoint(Math.Clamp(screenX, source.Left, source.Right - 1), Math.Clamp(screenY, source.Top, source.Bottom - 1));
        return null;
    }

    public void Dispose()
    {
        if (Started)
        {
            Started = false;
            _stage?.End();
        }

        _hold?.Dispose();
        _hold = null;
        _lease.Release(this);
    }
}
