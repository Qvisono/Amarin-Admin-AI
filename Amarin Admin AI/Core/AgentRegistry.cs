using System.Collections.Concurrent;

namespace Amarin.Core;

/// <summary>Что сделать с уже работающим агентом.</summary>
internal enum AgentInterruptKind
{
    /// <summary>Ничего: агент доводит начатое до конца.</summary>
    None,

    /// <summary>Остановить и не перезапускать.</summary>
    Stop,

    /// <summary>Остановить и тут же поднять заново на другой модели.</summary>
    Switch,

    /// <summary>Ничего не останавливать: передать агенту новую вводную и дать работать дальше.</summary>
    Tell
}

/// <param name="Complexity">Уровень для перезапуска: <c>fast</c>, <c>lite</c> или <c>heavy</c>.</param>
/// <param name="Prompt">Задание для перезапуска. Пусто — берётся прежнее.</param>
internal sealed record AgentInterrupt(
    AgentInterruptKind Kind,
    string Complexity = "",
    string Prompt = "");

/// <summary>
/// Агент, который работает прямо сейчас: чем он занят и чем его прервать.
/// </summary>
internal sealed class RunningAgent
{
    /// <summary>
    /// Уточнения, дописанные человеком уже после запуска. Агент забирает их на границе раунда.
    /// </summary>
    /// <remarks>
    /// Concurrent: кладёт их ход чата, забирает поток самого агента.
    /// </remarks>
    private readonly ConcurrentQueue<string> _notes = new();

    public required string Id { get; init; }

    /// <summary>Чат, из которого его подняли. Соседний чат своего агента остановить не может.</summary>
    public required string SessionId { get; init; }

    public required string Prompt { get; set; }

    public required string Complexity { get; set; }

    public required string ModelId { get; set; }

    public DateTime StartedAt { get; init; } = DateTime.Now;

    /// <summary>
    /// Своя отмена на попытку, связанная с отменой хода. Прерывание не должно выглядеть как
    /// отмена всего хода: ход после него продолжается, а агент — нет.
    /// </summary>
    internal required CancellationTokenSource Interrupt { get; init; }

    internal AgentInterrupt Pending { get; private set; } = new(AgentInterruptKind.None);

    /// <summary>
    /// Передаёт агенту новую вводную, не прерывая его: он подхватит её, закончив текущий шаг.
    /// </summary>
    internal void Tell(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            _notes.Enqueue(text.Trim());
        }
    }

    /// <summary>Забирает накопленное. Пусто — обычное дело, так бывает на каждом раунде.</summary>
    internal IReadOnlyList<string> TakeNotes()
    {
        var taken = new List<string>();
        while (_notes.TryDequeue(out var note))
        {
            taken.Add(note);
        }

        return taken;
    }

    /// <summary>Просит агента остановиться. Возврат управления — уже дело <c>AgentHost</c>.</summary>
    internal void Request(AgentInterrupt what)
    {
        Pending = what;
        try
        {
            Interrupt.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Агент успел закончить сам, пока решение ехало сюда. Это обычная гонка, не сбой.
        }
    }
}

/// <summary>
/// Список работающих агентов — чтобы их можно было остановить или пересадить на другую модель,
/// пока они работают.
/// </summary>
/// <remarks>
/// Раньше запущенный агент был недостижим: <c>init_agent</c> ждёт его внутри вызова инструмента,
/// и модель, занятая этим ожиданием, ничего сделать не могла. Просьба «быстрее» или «отмени»
/// доходила до неё только после того, как агент уже отработал, — то есть впустую.
/// </remarks>
internal sealed class AgentRegistry
{
    private readonly ConcurrentDictionary<string, RunningAgent> _running = new(StringComparer.Ordinal);

    private int _counter;

    /// <summary>Короткий идентификатор: модель называет агента по нему, поэтому не GUID.</summary>
    public RunningAgent Register(
        string sessionId,
        string prompt,
        string complexity,
        string modelId,
        CancellationTokenSource interrupt)
    {
        var agent = new RunningAgent
        {
            Id = "a" + Interlocked.Increment(ref _counter),
            SessionId = sessionId ?? "",
            Prompt = prompt ?? "",
            Complexity = complexity ?? "",
            ModelId = modelId ?? "",
            Interrupt = interrupt
        };

        _running[agent.Id] = agent;
        return agent;
    }

    public void Unregister(string id) => _running.TryRemove(id, out _);

    /// <summary>Агенты этого чата. Пустой идентификатор чата не совпадает ни с чем.</summary>
    public IReadOnlyList<RunningAgent> ListFor(string sessionId) =>
        string.IsNullOrEmpty(sessionId)
            ? []
            : _running.Values
                .Where(agent => string.Equals(agent.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(agent => agent.StartedAt)
                .ToList();
}
