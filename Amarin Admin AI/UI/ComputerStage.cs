using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Amarin.Computer;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Что видит человек, пока агент держит мышь: окно программы уходит в плашку, плашка показывает
/// шаг, «Стоп», отсчёт без человека, вопросы о нажатиях и подсказки «Показать».
/// </summary>
/// <remarks>
/// <para>
/// Методы <see cref="IComputerStage"/> зовёт агент с рабочих потоков; всё, что трогает окна,
/// уходит в поток интерфейса. <see cref="Avoid"/> — синхронно: окно под точкой проверяется сразу
/// после него, и плашка к этому моменту обязана уже уйти.
/// </para>
/// <para>
/// Вопрос о нажатии берёт плашка (<see cref="ConfirmationQueue.PanelTakes"/>): разворачивать окно
/// ради каждого «Да» значило бы отнимать фокус у окна, в котором агент работает. Прочие вопросы
/// того же хода (PowerShell, запись в реестр) показывает окно: оно разворачивается на время
/// вопроса и сворачивается обратно, пока управление идёт.
/// </para>
/// </remarks>
internal sealed class ComputerStage : IComputerStage
{
    /// <summary>Сколько ждёт отсчёт без человека, прежде чем агент возьмёт мышь.</summary>
    internal static readonly TimeSpan Countdown = TimeSpan.FromSeconds(10);

    private readonly Window _main;
    private readonly ConfirmationQueue _confirmations;
    private readonly bool _show;
    private ComputerBar? _bar;
    private ComputerHighlight? _highlight;

    /// <summary>Свечение по краям экранов и курсор Amarin. Null — в тестах: на настоящий экран их не ставим.</summary>
    private readonly ComputerGlow? _glow;
    private readonly ComputerCursor? _cursor;

    private volatile ComputerStart? _active;
    private volatile bool _counting;
    private TaskCompletionSource<bool>? _decision;
    private ConfirmationRequest? _question;
    private bool _minimizedByUs;
    private bool _raisedForQuestion;
    private IntPtr _focusBeforeQuestion;
    private WindowState _restoreTo = WindowState.Normal;
    private int _step;
    private string _stepText = "";

    /// <param name="show">
    /// Показывать ли окна. В тестах — нет: плашка ведёт себя так же, но не встаёт в угол
    /// настоящего экрана поверх того, чем занят человек.
    /// </param>
    public ComputerStage(Window main, ConfirmationQueue confirmations, bool show = true)
    {
        _main = main;
        _confirmations = confirmations;
        _show = show;
        if (show)
        {
            _glow = new ComputerGlow();
            _cursor = new ComputerCursor();
        }

        _confirmations.PanelTakes = Takes;
        _confirmations.Changed += OnQueueChanged;
    }

    /// <summary>Плашка — для тестов и снимков.</summary>
    internal ComputerBar? Bar => _bar;

    /// <summary>Длина отсчёта; тесты укорачивают её, чтобы не ждать настоящие десять секунд.</summary>
    internal TimeSpan CountdownTime { get; init; } = Countdown;

    /// <summary>Идёт ли сейчас управление.</summary>
    internal bool Active => _active is not null && !_counting;

    /// <summary>Идёт ли отсчёт перед началом без человека — тестам.</summary>
    internal bool Counting => _counting;

    /// <summary>Отписывается от очереди: окно сменило службы (другой профиль).</summary>
    public void Detach()
    {
        _confirmations.Changed -= OnQueueChanged;
        if (_confirmations.PanelTakes == Takes)
        {
            _confirmations.PanelTakes = null;
        }

        Ui(() =>
        {
            _bar?.Close();
            _highlight?.Close();
            _glow?.Hide();
            _cursor?.Restore();
        });
    }

    /// <remarks>
    /// Не началось — отменил человек на отсчёте, отменили ход или что-то упало — сцена убирает за
    /// собой сама: <see cref="ComputerSession"/> зовёт <see cref="End"/> только начатому управлению,
    /// и плашка с отсчётом осталась бы на экране до следующего прогона.
    /// </remarks>
    public async Task<bool> BeginAsync(ComputerStart start, CancellationToken cancellationToken)
    {
        _active = start;
        _counting = start.Unattended;
        try
        {
            Ui(() =>
            {
                var bar = EnsureBar();
                bar.PlaceOn(WorkAreaOf(new WindowInteropHelper(_main).Handle));
                if (_show && !bar.IsVisible)
                {
                    bar.Show();
                }
            });

            if (start.Unattended && !await CountDownAsync(start, cancellationToken).ConfigureAwait(false))
            {
                End();
                return false;
            }

            _counting = false;
            Ui(() =>
            {
                _step = 0;
                _stepText = Loc.Get("S.ComputerBar.Starting");
                _bar?.ShowStep(_step, _stepText);
                MinimizeMain();

                // Мышь теперь у агента: края экранов светятся, курсор — курсор Amarin.
                _glow?.Show();
                _cursor?.Apply();
            });
            return true;
        }
        catch
        {
            End();
            throw;
        }
    }

    public void Step(int step, string text)
    {
        _step = step;
        _stepText = text;
        Later(() =>
        {
            if (_bar is { Mode: ComputerBarMode.Step })
            {
                _bar.ShowStep(step, text);
            }

            _glow?.KeepOnTop();
        });
    }

    public void Avoid(ScreenPoint point) =>
        Ui(() => _bar?.Avoid(point, WorkAreaAt(point)));

    public async Task<bool> ShowAsync(ScreenRect area, string caption, TimeSpan wait, CancellationToken cancellationToken)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decision = decision;
        Ui(() =>
        {
            _highlight ??= new ComputerHighlight();
            if (_show)
            {
                _highlight.Frame(area);
            }

            _bar?.ShowHint(caption);
        });

        try
        {
            var finished = await Task.WhenAny(decision.Task, Task.Delay(wait, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return finished == decision.Task && decision.Task.Result;
        }
        finally
        {
            _decision = null;
            Ui(() =>
            {
                _highlight?.Hide();
                if (_bar is { Mode: ComputerBarMode.Show })
                {
                    _bar.ShowStep(_step, _stepText);
                }
            });
        }
    }

    public void End()
    {
        _active = null;
        _counting = false;
        _decision?.TrySetResult(false);
        Ui(() =>
        {
            _question = null;
            _highlight?.Hide();
            _bar?.Hide();
            _glow?.Hide();
            _cursor?.Restore();
            RestoreMain();
        });
    }

    // ───────────────────────── отсчёт ─────────────────────────

    private async Task<bool> CountDownAsync(ComputerStart start, CancellationToken cancellationToken)
    {
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _decision = decision;
        try
        {
            for (var left = (int)Math.Ceiling(CountdownTime.TotalSeconds); left > 0; left--)
            {
                var shown = left;
                Ui(() => _bar?.ShowCountdown(shown, start.Task));
                var tick = await Task.WhenAny(decision.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken))
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (tick == decision.Task)
                {
                    return decision.Task.Result;
                }
            }

            return true;
        }
        finally
        {
            _decision = null;
        }
    }

    // ───────────────────────── вопросы ─────────────────────────

    /// <summary>Вопрос о нажатии этого чата, пока идёт управление, — плашке.</summary>
    private bool Takes(string? sessionId, DangerousActionInfo info) =>
        Active &&
        _active is { } start &&
        info.ToolName.Equals(ComputerTool.ToolName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(sessionId, start.SessionId, StringComparison.Ordinal);

    private void OnQueueChanged() => Later(ShowQuestions);

    private void ShowQuestions()
    {
        if (!Active || _bar is null)
        {
            return;
        }

        if (_confirmations.TryPeekPanel(out var request))
        {
            if (!ReferenceEquals(request, _question))
            {
                _question = request;
                _bar.ShowQuestion(QuestionLine(request.Info));
            }
        }
        else if (_bar.Mode == ComputerBarMode.Question)
        {
            // Вопроса больше нет — ответили на плашке, в окне или его сняла отмена хода.
            _question = null;
            _bar.ShowStep(_step, _stepText);
        }

        // Вопрос не о нажатии показывает окно: развернуть на время вопроса и свернуть обратно.
        var windowQuestion = _confirmations.TryPeek(out _);
        if (windowQuestion && _minimizedByUs && _main.WindowState == WindowState.Minimized)
        {
            _focusBeforeQuestion = ComputerNative.GetForegroundWindow();
            _main.WindowState = _restoreTo;
            _raisedForQuestion = true;
        }
        else if (!windowQuestion && _raisedForQuestion)
        {
            _raisedForQuestion = false;
            if (_main.WindowState != WindowState.Minimized)
            {
                _main.WindowState = WindowState.Minimized;
            }

            // Фокус — обратно окну, в котором агент работал: свёрнутое окно отдаёт его кому
            // придётся, и следующий type без элемента ушёл бы в чужое окно.
            if (_focusBeforeQuestion != IntPtr.Zero)
            {
                _ = ComputerNative.SetForegroundWindow(_focusBeforeQuestion);
                _focusBeforeQuestion = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Строка вопроса: пояснение самого вызова, а нет его — пояснение вопроса или сводка действия.
    /// </summary>
    /// <remarks>
    /// У вопроса SynGuard пояснение — о самой проверке, одинаковое для любого нажатия; что делает
    /// именно это нажатие, написано в аргументах вызова.
    /// </remarks>
    internal static string QuestionLine(DangerousActionInfo info) =>
        ExplanationOf(info.CodeText) ??
        (string.IsNullOrWhiteSpace(info.Explanation) ? info.ChangeSummary : info.Explanation.Trim());

    private static string? ExplanationOf(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        try
        {
            return ToolArguments.Parse(arguments) is { ValueKind: System.Text.Json.JsonValueKind.Object } parsed &&
                   parsed.TryGetProperty("explanation", out var value) &&
                   value.ValueKind == System.Text.Json.JsonValueKind.String &&
                   value.GetString()?.Trim() is { Length: > 0 } text
                ? text
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ───────────────────────── кнопки плашки ─────────────────────────

    private void OnPrimary()
    {
        switch (_bar?.Mode)
        {
            case ComputerBarMode.Question when _question is { } request:
                _question = null;
                _ = _confirmations.Complete(request, approved: true);
                break;
            case ComputerBarMode.Show:
                _decision?.TrySetResult(true);
                break;
        }
    }

    private void OnSecondary()
    {
        switch (_bar?.Mode)
        {
            case ComputerBarMode.Countdown:
                _decision?.TrySetResult(false);
                break;
            case ComputerBarMode.Question when _question is { } request:
                _question = null;
                _ = _confirmations.Complete(request, approved: false);
                break;
            default:
                // «Стоп» — остановка агента целиком, тем же путём, что из чата.
                _decision?.TrySetResult(false);
                _active?.Stop?.Invoke();
                break;
        }
    }

    // ───────────────────────── окно программы ─────────────────────────

    private void MinimizeMain()
    {
        if (!_main.IsVisible || _main.WindowState == WindowState.Minimized)
        {
            return;
        }

        _restoreTo = _main.WindowState;
        _minimizedByUs = true;
        _main.WindowState = WindowState.Minimized;
    }

    /// <summary>Возвращает окно, только если свернули его мы и человек не развернул его сам.</summary>
    private void RestoreMain()
    {
        if (_minimizedByUs && _main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = _restoreTo;
        }

        _minimizedByUs = false;
        _raisedForQuestion = false;
    }

    private ComputerBar EnsureBar()
    {
        if (_bar is not null)
        {
            return _bar;
        }

        _bar = new ComputerBar();
        _bar.Primary += OnPrimary;
        _bar.Secondary += OnSecondary;
        _bar.ShowStep(0, "");
        return _bar;
    }

    private void Ui(Action action)
    {
        if (_main.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _main.Dispatcher.Invoke(action);
        }
    }

    private void Later(Action action) => _main.Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    // ───────────────────────── мониторы ─────────────────────────

    private static ScreenRect WorkAreaOf(IntPtr window) =>
        window == IntPtr.Zero
            ? WorkAreaAt(default)
            : WorkArea(ComputerNative.MonitorFromWindow(window, ComputerNative.MonitorDefaultToNearest));

    private static ScreenRect WorkAreaAt(ScreenPoint point) =>
        WorkArea(ComputerNative.MonitorFromPoint(new ComputerNative.Point { X = point.X, Y = point.Y }, ComputerNative.MonitorDefaultToNearest));

    private static ScreenRect WorkArea(IntPtr monitor)
    {
        var info = new ComputerNative.MonitorInfo { Size = Marshal.SizeOf<ComputerNative.MonitorInfo>() };
        return monitor != IntPtr.Zero && ComputerNative.GetMonitorInfoW(monitor, ref info)
            ? new ScreenRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top)
            : default;
    }
}
