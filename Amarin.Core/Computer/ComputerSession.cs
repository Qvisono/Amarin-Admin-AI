using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Кто сейчас держит мышь. Одна на процесс: мышь и клавиатура у машины одни, и два агента
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
/// Управление в одном прогоне агента: когда оно началось, какие id выданы элементам, какой
/// снимок последний и что показывать на плашке.
/// </summary>
/// <remarks>
/// <para>
/// Начало — первое пишущее действие или «Показать», а не первый взгляд: посмотреть на окна можно
/// и не сворачивая программу, а мышь берётся, только когда её правда пора брать. Без человека
/// перед началом идёт отсчёт, и отмена на нём закрывает управление до конца прогона.
/// </para>
/// <para>
/// Один на весь прогон агента, а не на попытку: пересадка агента на другую модель не должна
/// возвращать окно программы и заново показывать отсчёт.
/// </para>
/// <para>
/// Id элементов — сквозные на прогон: новое дерево окна заменяет id этого окна новыми, а не
/// начинает счёт заново. Иначе «e3» из дерева одного окна совпал бы с «e3» другого, и модель
/// нажала бы не туда. Запоминание под замком: чтения раунда агента идут одновременно.
/// </para>
/// </remarks>
internal sealed class ComputerSession : IDisposable
{
    private readonly IComputerStage? _stage;
    private readonly ComputerStart _start;
    private readonly ComputerLease _lease;
    private readonly Lock _gate = new();
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

    public int OwnProcessId { get; }

    /// <summary>Сколько действий уже сделано — номер шага на плашке.</summary>
    public int Steps { get; private set; }

    public bool Started { get; private set; }

    /// <summary>
    /// Есть ли кому показывать: плашка с подсветкой и человек у компьютера. Без человека
    /// «Показать» ждало бы «Готово» до конца срока на каждом шаге.
    /// </summary>
    public bool CanShow => _stage is not null && !_start.Unattended;

    /// <summary>
    /// Что сейчас на экране и на что нацелены вызовы раунда — для SynGuard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По вызову <c>{"action":"key","keys":"enter"}</c> не понять, что уйдёт, а по
    /// <c>"element":"e12"</c> — на что нажимают. Поэтому защитник получает окно впереди, поле с
    /// фокусом вместе с его текстом и запомненные элементы, на которые указывают вызовы. Текст
    /// поля пароля не передаётся никогда.
    /// </para>
    /// <para>
    /// Окна самой программы не описываются: мышь их не трогает, а в начале прогона впереди стоит
    /// как раз окно чата, и защитник принимал его поле за то, по чему бьёт нажатие. Цели берутся
    /// из запомненного, без UI Automation: проверка раунда не должна ждать чужое окно.
    /// </para>
    /// </remarks>
    /// <param name="calls">Аргументы вызовов <c>computer</c> раунда.</param>
    public string? DescribeForGuard(IEnumerable<JsonElement> calls)
    {
        var text = new StringBuilder();
        if (Computer.Foreground() is { } window && window.ProcessId != OwnProcessId)
        {
            text.Append("foreground window \"").Append(TextClip.Clip(window.Title, 160, 0))
                .Append("\" (").Append(window.ProcessName).Append(')');
            if (Computer.Focused() is { } focus)
            {
                text.Append("; keyboard focus: ").Append(focus.Role).Append(" \"").Append(TextClip.Clip(focus.Name, 120, 0)).Append('"');
                if (!focus.IsPassword && !string.IsNullOrEmpty(focus.Value))
                {
                    text.Append(" holding \"").Append(TextClip.Clip(focus.Value, 1000, 0)).Append('"');
                }
            }
        }

        foreach (var id in calls.SelectMany(TargetIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryElement(id, out var handle, out var element))
            {
                continue;
            }

            text.Append(text.Length == 0 ? "" : "; ")
                .Append(id).Append(" = ").Append(element.Role).Append(" \"").Append(TextClip.Clip(element.Name, 120, 0)).Append('"');
            if (Computer.Window(handle) is { } owner)
            {
                text.Append(" in window \"").Append(TextClip.Clip(owner.Title, 120, 0)).Append('"');
            }
        }

        return text.Length == 0 ? null : text.ToString();
    }

    /// <summary>Id элементов, на которые указывает вызов, в том числе в шагах цепочки.</summary>
    private static IEnumerable<string> TargetIds(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var field in new[] { "element", "to_element" })
        {
            if (arguments.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } id)
            {
                yield return id.Trim();
            }
        }

        if (arguments.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in steps.EnumerateArray().SelectMany(TargetIds))
            {
                yield return id;
            }
        }
    }

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
        lock (_gate)
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
    }

    public bool TryElement(string? id, out long window, [NotNullWhen(true)] out ComputerElement? element)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(id) && _elements.TryGetValue(id.Trim(), out var remembered))
            {
                window = remembered.Window;
                element = remembered.Element;
                return true;
            }
        }

        window = 0;
        element = null;
        return false;
    }

    public void RememberShot(ComputerShot shot, ComputerWindow? window)
    {
        lock (_gate)
        {
            _shot = new Frame(shot, window?.Handle, window?.Bounds ?? default);
        }
    }

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
        Frame? frame;
        lock (_gate)
        {
            frame = _shot;
        }

        if (frame is null)
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
