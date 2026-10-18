using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Экран без экрана: окна, деревья и снимки задаются тестом, а каждое действие пишется в журнал.
/// Настоящую мышь тесты не трогают никогда.
/// </summary>
internal sealed class FakeComputer : IComputer
{
    public ComputerDesk DeskState { get; set; } = ComputerDesk.Open;

    public List<ComputerMonitor> MonitorList { get; } =
    [
        new(1, new ScreenRect(0, 0, 1920, 1080), Primary: true),
        new(2, new ScreenRect(1920, 0, 2560, 1440), Primary: false, Scale: 1.5)
    ];

    /// <summary>Окна сверху вниз.</summary>
    public List<ComputerWindow> WindowList { get; } = [];

    public Dictionary<long, List<ComputerElement>> Trees { get; } = [];

    public long? ForegroundHandle { get; set; }

    public ComputerElement? FocusedElement { get; set; }

    /// <summary>Что сделано: «click 3200,720», «type …», «keys ctrl+s».</summary>
    public List<string> Log { get; } = [];

    public int Settles { get; private set; }

    public ComputerDesk Desk() => DeskState;

    public IReadOnlyList<ComputerMonitor> Monitors() => MonitorList;

    public IReadOnlyList<ComputerWindow> Windows() =>
        WindowList.Select(window => window with { Foreground = window.Handle == ForegroundHandle }).ToList();

    public ComputerWindow? Window(long handle) => Windows().FirstOrDefault(window => window.Handle == handle);

    public ComputerWindow? WindowAt(ScreenPoint point) =>
        Windows().FirstOrDefault(window => !window.Minimized && window.Bounds.Contains(point));

    public ComputerWindow? Foreground() => ForegroundHandle is { } handle ? Window(handle) : null;

    public ComputerElement? Focused() => FocusedElement;

    public Task<IReadOnlyList<ComputerElement>> InspectAsync(long window, int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ComputerElement>>(
            Trees.TryGetValue(window, out var tree) ? tree.Take(limit).ToList() : []);

    public Task<ComputerElement?> ElementAsync(long window, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Trees.TryGetValue(window, out var tree) ? tree.FirstOrDefault(element => element.Key == key) : null);

    public Task<ComputerShot> CaptureAsync(ScreenRect area, int maxSide, CancellationToken cancellationToken)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(area.Width, area.Height));
        var width = (int)Math.Round(area.Width * scale);
        var height = (int)Math.Round(area.Height * scale);
        Log.Add($"capture {area}");
        return Task.FromResult(new ComputerShot("iVBORw0KGgo=", width, height, area));
    }

    public Task PointerAsync(ComputerPointer action, CancellationToken cancellationToken)
    {
        Log.Add(action.Kind switch
        {
            ComputerPointerKind.Drag => $"drag {action.At.X},{action.At.Y}>{action.To?.X},{action.To?.Y}",
            ComputerPointerKind.Scroll => $"scroll {action.At.X},{action.At.Y} {action.ScrollX},{action.ScrollY}",
            _ => $"{action.Kind.ToString().ToLowerInvariant()} {action.At.X},{action.At.Y}"
        });
        return Task.CompletedTask;
    }

    public Task TypeAsync(string text, CancellationToken cancellationToken)
    {
        Log.Add("type " + text);
        return Task.CompletedTask;
    }

    public Task KeysAsync(IReadOnlyList<string> chord, CancellationToken cancellationToken)
    {
        Log.Add("keys " + ComputerKeys.Join(chord));
        return Task.CompletedTask;
    }

    public Task<bool> FocusWindowAsync(long window, CancellationToken cancellationToken)
    {
        Log.Add($"focus window {window}");
        ForegroundHandle = window;
        return Task.FromResult(true);
    }

    public Task<bool> FocusElementAsync(long window, string key, CancellationToken cancellationToken)
    {
        Log.Add($"focus element {key}");
        return Task.FromResult(true);
    }

    public Task<bool> InvokeAsync(long window, string key, CancellationToken cancellationToken)
    {
        Log.Add($"invoke {key}");
        return Task.FromResult(true);
    }

    public Task SettleAsync(TimeSpan max, CancellationToken cancellationToken)
    {
        Settles++;
        return Task.CompletedTask;
    }

    /// <summary>Окно с деревом. Возвращает его — чтобы тест мог назвать его по id.</summary>
    public ComputerWindow AddWindow(
        long handle,
        ScreenRect bounds,
        int processId = 4242,
        string title = "Notes",
        ComputerWindowKind kind = ComputerWindowKind.Normal,
        bool elevated = false,
        params ComputerElement[] elements)
    {
        var monitor = MonitorList.FirstOrDefault(item => item.Bounds.Contains(bounds.Center))?.Index ?? 1;
        var window = new ComputerWindow(handle, title, "notes", processId, bounds, monitor, kind, elevated);
        WindowList.Add(window);
        Trees[handle] = [.. elements];
        ForegroundHandle ??= handle;
        return window;
    }

    public void Move(long handle, ScreenRect bounds)
    {
        var index = WindowList.FindIndex(window => window.Handle == handle);
        WindowList[index] = WindowList[index] with { Bounds = bounds };
    }
}

/// <summary>Плашка без окна: что начинали, какие шаги показывали, что отвечает «Готово».</summary>
internal sealed class FakeStage : IComputerStage
{
    /// <summary>Что ответит отсчёт: false — человек отменил.</summary>
    public bool Proceed { get; set; } = true;

    public bool ShowAnswer { get; set; } = true;

    public List<ComputerStart> Begins { get; } = [];

    public List<string> Steps { get; } = [];

    public List<ScreenPoint> Avoided { get; } = [];

    public List<(ScreenRect Area, string Caption)> Shown { get; } = [];

    public int Ends { get; private set; }

    public Task<bool> BeginAsync(ComputerStart start, CancellationToken cancellationToken)
    {
        Begins.Add(start);
        return Task.FromResult(Proceed);
    }

    public void Step(int step, string text) => Steps.Add($"{step}: {text}");

    public void Avoid(ScreenPoint point) => Avoided.Add(point);

    public Task<bool> ShowAsync(ScreenRect area, string caption, TimeSpan wait, CancellationToken cancellationToken)
    {
        Shown.Add((area, caption));
        return Task.FromResult(ShowAnswer);
    }

    public void End() => Ends++;
}
