using System.Diagnostics.CodeAnalysis;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Подделки для тестов автомата обновлений: часы, очередь потока окна и порты.
/// </summary>
/// <remarks>
/// Всё ручное: время двигает тест, очередь разбирает тест, сетевые шаги кончаются, когда тест
/// завершит их задачу. Поэтому гонки воспроизводятся порядком вызовов, а не удачей планировщика.
/// </remarks>
internal static class UpdateTestKit
{
    public static readonly ReleaseVersion Current = new Version(1, 29, 0);

    public static ReleaseInfo Release(string version = "1.30.0", bool checksum = true, bool build = true) =>
        new(
            "v" + version,
            new Version(version.Split('-')[0]),
            "https://github.com/Qvisono/Amarin-Admin-AI/releases/tag/v" + version,
            "Release " + version,
            null,
            build
                ?
                [
                    new ReleaseAsset(
                        $"Amarin-Admin-AI-v{version}-win-x64.exe",
                        $"https://github.com/Qvisono/Amarin-Admin-AI/releases/download/v{version}/Amarin-Admin-AI-v{version}-win-x64.exe",
                        1024,
                        checksum ? new string('a', 64) : null)
                ]
                : []);

    public static UpdatePlan Plan(ReleaseInfo release, bool elevated = false) =>
        new(
            release.WindowsBuild ?? throw new ArgumentException("У выпуска нет сборки.", nameof(release)),
            "/apps/Amarin Admin AI.exe",
            "/apps/.update",
            elevated);

    public static StagedUpdate Staged(ReleaseInfo release, bool elevated = false)
    {
        var plan = Plan(release, elevated);
        return new(plan, "/apps/.update/" + plan.Asset.Name, release.Release);
    }

    public static UpdateContext Context(
        bool auto = true,
        bool turns = false,
        ReleaseVersion? declined = null,
        DateTime? now = null) =>
        new(Current, auto, declined, turns, now ?? new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc));

    public static UpdateCheckResult Found(ReleaseInfo release) => new() { Latest = release, UpdateAvailable = true };

    /// <summary>
    /// Завершить сетевой шаг так, как его завершает сеть, — вне потока окна.
    /// </summary>
    /// <remarks>
    /// Продолжение <c>await … ConfigureAwait(false)</c> не встраивается туда, где стоит свой
    /// контекст синхронизации (<c>IsValidLocationForInlining</c>), а уходит в пул потоков. Завершай
    /// тест шаг прямо на потоке с ручной очередью, итог появлялся бы в ней когда придётся, и
    /// <see cref="UiQueue.Run"/> то успевал бы его разобрать, то нет.
    /// </remarks>
    public static void OffUiThread(Action complete)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            complete();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
}

/// <summary>Часы, которые двигает тест. Таймеры срабатывают при сдвиге.</summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        while (true)
        {
            ManualTimer? due;
            lock (_timers)
            {
                due = _timers.Where(timer => timer.Due is { } at && at <= _now).OrderBy(timer => timer.Due).FirstOrDefault();
            }

            if (due is null)
            {
                return;
            }

            due.Fire();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_timers)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTime time, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : time.GetUtcNow() + dueTime;
            return true;
        }

        public void Fire()
        {
            Due = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : Due + _period;
            callback(state);
        }

        public void Dispose() => time.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Очередь «потока окна». И <c>post</c> автомата, и продолжения <c>await</c> (когда тест ставит
/// её контекстом) встают сюда; разбирает их тест.
/// </summary>
internal sealed class UiQueue : SynchronizationContext
{
    private readonly Queue<Action> _work = new();
    private readonly SemaphoreSlim _arrived = new(0);

    public void Post(Action action)
    {
        lock (_work)
        {
            _work.Enqueue(action);
        }

        _arrived.Release();
    }

    public override void Post(SendOrPostCallback d, object? state) => Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state) => d(state);

    /// <summary>Разобрать всё, что уже стоит, и то, что встанет по ходу.</summary>
    public void Run()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            while (true)
            {
                Action? next;
                lock (_work)
                {
                    if (!_work.TryDequeue(out next))
                    {
                        return;
                    }
                }

                next();
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// Разбирать очередь, пока условие не станет верным. Фоновая работа (подмена идёт через
    /// <c>Task.Run</c>) приходит в очередь не сразу — её ждём, но не дольше пяти секунд.
    /// </summary>
    public void RunUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            Run();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Условие так и не выполнилось.");
            }

            _arrived.Wait(TimeSpan.FromMilliseconds(50));
        }
    }

    /// <summary>Сделать очередь контекстом этого потока на время теста.</summary>
    public IDisposable Install()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        return new Restore(previous);
    }

    private sealed class Restore(SynchronizationContext? previous) : IDisposable
    {
        public void Dispose() => SetSynchronizationContext(previous);
    }
}

/// <summary>GitHub, который отвечает, когда тест скажет.</summary>
internal sealed class FakeUpdateSource : IUpdateSource
{
    public List<(bool Beta, TaskCompletionSource<UpdateCheckResult> Answer, CancellationToken Token)> Calls { get; } = [];

    public Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, bool beta, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<UpdateCheckResult>();
        cancellationToken.Register(() => UpdateTestKit.OffUiThread(() => answer.TrySetCanceled(cancellationToken)));
        Calls.Add((beta, answer, cancellationToken));
        return answer.Task;
    }

    public void Answer(UpdateCheckResult result, int index = -1) =>
        UpdateTestKit.OffUiThread(() => Calls[index < 0 ? Calls.Count - 1 : index].Answer.TrySetResult(result));
}

/// <summary>Файлы обновления на бумаге: загрузки ждут теста, подмены записываются.</summary>
internal sealed class FakeUpdateFiles : IUpdateFiles
{
    public string? PlanError { get; set; }

    public bool Elevated { get; set; }

    public UpdateStepResult SwapResult { get; set; } = UpdateStepResult.Success;

    public List<Download> Downloads { get; } = [];

    public List<StagedUpdate> Swaps { get; } = [];

    public List<StagedUpdate> ElevatedSwaps { get; } = [];

    public int Plans { get; private set; }

    public bool TryPlan(ReleaseInfo release, [NotNullWhen(true)] out UpdatePlan? plan, out string error)
    {
        Plans++;
        plan = PlanError is null ? UpdateTestKit.Plan(release, Elevated) : null;
        error = PlanError ?? "";
        return plan is not null;
    }

    public Task<(UpdateStepResult Result, string? File)> DownloadAsync(
        UpdatePlan plan,
        IProgress<double> progress,
        bool allowUnverified,
        CancellationToken cancellationToken)
    {
        var download = new Download(plan, progress, allowUnverified, cancellationToken);
        cancellationToken.Register(() => UpdateTestKit.OffUiThread(() => download.Done.TrySetCanceled(cancellationToken)));
        Downloads.Add(download);
        return download.Done.Task;
    }

    public UpdateStepResult Swap(StagedUpdate staged)
    {
        lock (Swaps)
        {
            Swaps.Add(staged);
        }

        return SwapResult;
    }

    public Task<UpdateStepResult> SwapElevatedAsync(StagedUpdate staged)
    {
        ElevatedSwaps.Add(staged);
        return Task.FromResult(SwapResult);
    }

    public sealed record Download(UpdatePlan Plan, IProgress<double> Progress, bool AllowUnverified, CancellationToken Token)
    {
        public TaskCompletionSource<(UpdateStepResult, string?)> Done { get; } = new();

        public void Finish() =>
            UpdateTestKit.OffUiThread(() => Done.TrySetResult((UpdateStepResult.Success, Plan.WorkDirectory + "/" + Plan.Asset.Name)));

        public void Fail(string error) => UpdateTestKit.OffUiThread(() => Done.TrySetResult((UpdateStepResult.Failed(error), null)));
    }
}

/// <summary>Программа вокруг обновлений: настройки и ходы задаёт тест, действия записываются.</summary>
internal sealed class FakeUpdateApp : IUpdateApp
{
    public bool AutoUpdate { get; set; } = true;

    public bool Beta { get; set; }

    public ReleaseVersion? Declined { get; set; }

    public bool TurnsRunning { get; set; }

    public string? RestartError { get; set; }

    public List<DateTime> Checks { get; } = [];

    public int Saves { get; private set; }

    public List<string> Restarts { get; } = [];

    public void RememberCheck(DateTime utc) => Checks.Add(utc);

    public void BeforeSwap() => Saves++;

    public string? RestartInto(string exePath)
    {
        Restarts.Add(exePath);
        return RestartError;
    }
}

/// <summary>Окно вокруг выхода: что с ним сделали, записывается.</summary>
internal sealed class FakeExitHost : IExitHost
{
    public bool OwnsApplication { get; set; } = true;

    public bool UpdateForbidden { get; set; }

    public int Hidden { get; private set; }

    public int Shown { get; private set; }

    public int Shutdowns { get; private set; }

    public List<string> Successors { get; } = [];

    public Task YieldAsync() => Task.CompletedTask;

    public void HideForBackgroundExit() => Hidden++;

    public void ShowAgain() => Shown++;

    public void StartSuccessor(string exePath) => Successors.Add(exePath);

    public void Shutdown() => Shutdowns++;
}

/// <summary>Автомат с подделками вокруг — то, что собирает окно, только без окна.</summary>
internal sealed class UpdateRig : IDisposable
{
    private readonly IDisposable _context;

    public UpdateRig()
    {
        _context = Queue.Install();
        Controller = new UpdateController(Source, Files, App, UpdateTestKit.Current, Queue.Post, Time);
        Exit = new UpdateExit(Controller, Files, Host, Time);
    }

    public ManualTime Time { get; } = new();

    public UiQueue Queue { get; } = new();

    public FakeUpdateSource Source { get; } = new();

    public FakeUpdateFiles Files { get; } = new();

    public FakeUpdateApp App { get; } = new();

    public FakeExitHost Host { get; } = new();

    public UpdateController Controller { get; }

    public UpdateExit Exit { get; }

    public UpdateState State => Controller.State;

    /// <summary>Проверка при запуске нашла выпуск, и фоновая загрузка пошла.</summary>
    public FakeUpdateFiles.Download StartBackgroundDownload(ReleaseInfo release)
    {
        Controller.Start();
        Source.Answer(UpdateTestKit.Found(release));
        Queue.Run();
        return Files.Downloads[^1];
    }

    public void Dispose()
    {
        Controller.Dispose();
        _context.Dispose();
    }
}
