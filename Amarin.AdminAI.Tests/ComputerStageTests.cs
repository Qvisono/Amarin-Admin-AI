using System.Windows;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Плашка управления компьютером на живом WPF: что она показывает, куда уходят вопросы о
/// нажатиях, когда окно программы сворачивается и возвращается, что делают её кнопки. Плашка в
/// тестах не встаёт на экран (кроме проверки места — за краем экрана).
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ComputerStageTests
{
    private readonly WpfFixture _wpf;

    public ComputerStageTests(WpfFixture wpf) => _wpf = wpf;

    private sealed record Rig(Window Main, ConfirmationQueue Queue, ComputerStage Stage);

    private Rig Open(AppSettings? settings = null)
    {
        var queue = new ConfirmationQueue(() => settings ?? new AppSettings());
        return _wpf.Ui.Invoke(() =>
        {
            var main = new Window
            {
                Width = 200,
                Height = 120,
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None
            };
            main.Show();
            return new Rig(main, queue, new ComputerStage(main, queue, show: false) { CountdownTime = TimeSpan.FromSeconds(1) });
        });
    }

    private void Close(Rig rig) => _wpf.Ui.Invoke<object?>(() =>
    {
        rig.Stage.Detach();
        rig.Main.Close();
        return null;
    });

    /// <summary>
    /// Читает состояние, дождавшись всего, что сцена отложила в очередь окна: <c>Invoke</c> теста
    /// идёт с наивысшим приоритетом и обогнал бы отложенные обновления плашки.
    /// </summary>
    private T Ui<T>(Func<T> read)
    {
        Application.Current.Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return _wpf.Ui.Invoke(read);
    }

    private void Press(Rig rig, bool primary) => _wpf.Ui.Invoke<object?>(() =>
    {
        var bar = rig.Stage.Bar!;
        (primary ? bar.PrimaryButton : bar.SecondaryButton).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        return null;
    });

    private static ComputerStart Start(bool unattended = false, Action? stop = null) =>
        new(unattended, "save the file", stop, "chat-1");

    private static DangerousActionInfo Ask(string tool, string explanation = "Press Save") =>
        new(tool, "summary", "details", DangerousRiskLevel.Low, explanation);

    [Fact]
    public async Task Taking_the_mouse_folds_the_window_into_the_panel_and_the_end_brings_it_back()
    {
        var rig = Open();
        try
        {
            Assert.True(await rig.Stage.BeginAsync(Start(), CancellationToken.None));
            Assert.Equal(WindowState.Minimized, Ui(() => rig.Main.WindowState));
            Assert.Equal(ComputerBarMode.Step, Ui(() => rig.Stage.Bar!.Mode));

            rig.Stage.Step(3, "Open the menu");
            Assert.Equal("Open the menu", Ui(() => rig.Stage.Bar!.BodyText.Text));

            rig.Stage.End();
            Assert.Equal(WindowState.Normal, Ui(() => rig.Main.WindowState));
            Assert.False(Ui(() => rig.Stage.Active));
        }
        finally
        {
            Close(rig);
        }
    }

    [Fact]
    public async Task A_window_the_person_restored_is_left_alone_at_the_end()
    {
        var rig = Open();
        try
        {
            _ = await rig.Stage.BeginAsync(Start(), CancellationToken.None);
            _wpf.Ui.Invoke(() => rig.Main.WindowState = WindowState.Maximized);

            rig.Stage.End();

            Assert.Equal(WindowState.Maximized, Ui(() => rig.Main.WindowState));
        }
        finally
        {
            Close(rig);
        }
    }

    [Fact]
    public async Task Without_the_person_a_countdown_comes_first_and_can_be_cancelled()
    {
        var rig = Open();
        try
        {
            var begin = rig.Stage.BeginAsync(Start(unattended: true), CancellationToken.None);

            // Под нагрузкой полного прогона плашка доходит до отсчёта не за 0,4 с — ждём до двух.
            for (var i = 0; i < 200 && Ui(() => rig.Stage.Bar?.Mode) != ComputerBarMode.Countdown; i++)
            {
                await Task.Delay(10);
            }

            Assert.Equal(ComputerBarMode.Countdown, Ui(() => rig.Stage.Bar!.Mode));
            Press(rig, primary: false);

            Assert.False(await begin);
            Assert.NotEqual(WindowState.Minimized, Ui(() => rig.Main.WindowState));
        }
        finally
        {
            Close(rig);
        }
    }

    [Fact]
    public async Task A_run_cancelled_during_the_countdown_takes_the_panel_away()
    {
        // Сессия зовёт End только начатому управлению: без своей уборки сцена оставалась «на
        // отсчёте», и плашка с ним висела на экране после отменённого хода.
        var rig = Open();
        using var cancel = new CancellationTokenSource();
        try
        {
            var begin = rig.Stage.BeginAsync(Start(unattended: true), cancel.Token);
            for (var i = 0; i < 200 && Ui(() => rig.Stage.Bar?.Mode) != ComputerBarMode.Countdown; i++)
            {
                await Task.Delay(10);
            }

            Assert.True(Ui(() => rig.Stage.Counting));
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);
            Assert.False(Ui(() => rig.Stage.Counting));
            Assert.False(Ui(() => rig.Stage.Active));
            Assert.NotEqual(WindowState.Minimized, Ui(() => rig.Main.WindowState));
        }
        finally
        {
            Close(rig);
        }
    }

    [Fact]
    public async Task An_unanswered_countdown_lets_the_agent_go_on()
    {
        var rig = Open();
        try
        {
            Assert.True(await rig.Stage.BeginAsync(Start(unattended: true), CancellationToken.None));
            Assert.Equal(ComputerBarMode.Step, Ui(() => rig.Stage.Bar!.Mode));
        }
        finally
        {
            rig.Stage.End();
            Close(rig);
        }
    }

    [Fact]
    public async Task A_question_about_a_press_is_answered_on_the_panel()
    {
        var rig = Open();
        try
        {
            _ = await rig.Stage.BeginAsync(Start(), CancellationToken.None);

            var answer = rig.Queue.ConfirmDetailedAsync("operator", Ask(ComputerTool.ToolName), "chat-1");
            Assert.Equal(ComputerBarMode.Question, Ui(() => rig.Stage.Bar!.Mode));
            Assert.Equal("Press Save", Ui(() => rig.Stage.Bar!.BodyText.Text));
            Assert.False(rig.Queue.TryPeek(out _));

            Press(rig, primary: true);

            Assert.True((await answer).Approved);
            Assert.Equal(ComputerBarMode.Step, Ui(() => rig.Stage.Bar!.Mode));
        }
        finally
        {
            rig.Stage.End();
            Close(rig);
        }
    }

    [Fact]
    public async Task Other_questions_bring_the_window_up_and_fold_it_again_after_the_answer()
    {
        var rig = Open();
        try
        {
            _ = await rig.Stage.BeginAsync(Start(), CancellationToken.None);

            var script = rig.Queue.ConfirmDetailedAsync("agent", Ask("run_powershell"), "chat-1");
            Assert.Equal(WindowState.Normal, Ui(() => rig.Main.WindowState));
            Assert.True(rig.Queue.TryPeek(out var shown));
            Assert.False(rig.Queue.TryPeekPanel(out _));

            _ = rig.Queue.Complete(shown, approved: true);
            Assert.True((await script).Approved);
            Assert.Equal(WindowState.Minimized, Ui(() => rig.Main.WindowState));
        }
        finally
        {
            rig.Stage.End();
            Close(rig);
        }
    }

    [Fact]
    public async Task A_press_question_from_another_chat_stays_with_the_window()
    {
        var rig = Open();
        try
        {
            _ = await rig.Stage.BeginAsync(Start(), CancellationToken.None);

            _ = rig.Queue.ConfirmDetailedAsync("operator", Ask(ComputerTool.ToolName), "chat-2");

            Assert.False(rig.Queue.TryPeekPanel(out _));
            Assert.True(rig.Queue.TryPeek(out _));
        }
        finally
        {
            rig.Stage.End();
            rig.Queue.CancelAll();
            Close(rig);
        }
    }

    [Fact]
    public async Task Stop_on_the_panel_stops_the_agent()
    {
        var rig = Open();
        var stopped = 0;
        try
        {
            _ = await rig.Stage.BeginAsync(Start(stop: () => stopped++), CancellationToken.None);

            Press(rig, primary: false);

            Assert.Equal(1, stopped);
        }
        finally
        {
            rig.Stage.End();
            Close(rig);
        }
    }

    [Fact]
    public async Task Showing_waits_for_Done()
    {
        var rig = Open();
        try
        {
            _ = await rig.Stage.BeginAsync(Start(), CancellationToken.None);

            var shown = rig.Stage.ShowAsync(new ScreenRect(10, 10, 50, 20), "Press Save", TimeSpan.FromSeconds(10), CancellationToken.None);
            Assert.Equal(ComputerBarMode.Show, Ui(() => rig.Stage.Bar!.Mode));
            Assert.Equal("Press Save", Ui(() => rig.Stage.Bar!.BodyText.Text));

            Press(rig, primary: true);

            Assert.True(await shown);
            Assert.Equal(ComputerBarMode.Step, Ui(() => rig.Stage.Bar!.Mode));
        }
        finally
        {
            rig.Stage.End();
            Close(rig);
        }
    }

    [Fact]
    public void The_panel_steps_aside_from_the_point_it_covers()
    {
        var work = new ScreenRect(-32000, 0, 1600, 900);
        var (atTop, atBottom, crossed) = _wpf.Ui.Invoke(() =>
        {
            var bar = new ComputerBar();
            bar.ShowStep(1, "Working");
            bar.PlaceOn(work);
            bar.Show();
            bar.UpdateLayout();
            bar.PlaceOn(work);
            var top = bar.Bounds();
            bar.Avoid(top.Center, work);
            var bottom = bar.Bounds();
            var crossedBack = bar.AtBottom;
            bar.Close();
            return (top, bottom, crossedBack);
        });

        Assert.True(atTop.Top < work.Top + 100, atTop.ToString());
        Assert.True(atBottom.Bottom > work.Bottom - 100, atBottom.ToString());
        Assert.True(crossed);
    }

    [Fact]
    public void The_panel_is_kept_out_of_screenshots()
    {
        var affinity = _wpf.Ui.Invoke(() =>
        {
            var bar = new ComputerBar();
            bar.PlaceOn(new ScreenRect(-32000, 0, 1600, 900));
            bar.Show();
            _ = Amarin.Computer.ComputerNative.GetWindowDisplayAffinity(new System.Windows.Interop.WindowInteropHelper(bar).Handle, out var value);
            bar.Close();
            return value;
        });

        Assert.Equal(0x11u, affinity);
    }
}

/// <summary>Очередь вопросов и плашка: чей вопрос куда идёт и что можно разрешить впрок.</summary>
public sealed class ConfirmationPanelTests
{
    private static DangerousActionInfo Ask(string tool) => new(tool, "summary", "details", DangerousRiskLevel.Low);

    [Fact]
    public void Without_a_panel_every_question_is_the_windows()
    {
        var queue = new ConfirmationQueue(() => new AppSettings());

        _ = queue.ConfirmDetailedAsync("operator", Ask(ComputerTool.ToolName), "chat");

        Assert.True(queue.TryPeek(out _));
        Assert.False(queue.TryPeekPanel(out _));
    }

    [Fact]
    public void The_panel_takes_only_what_it_claims_and_the_window_skips_it()
    {
        var queue = new ConfirmationQueue(() => new AppSettings())
        {
            PanelTakes = (_, info) => info.ToolName == ComputerTool.ToolName
        };

        _ = queue.ConfirmDetailedAsync("operator", Ask(ComputerTool.ToolName), "chat");
        _ = queue.ConfirmDetailedAsync("agent", Ask("registry"), "chat");

        Assert.True(queue.TryPeekPanel(out var panel));
        Assert.Equal(ComputerTool.ToolName, panel.Info.ToolName);
        Assert.True(queue.TryPeek(out var window));
        Assert.Equal("registry", window.Info.ToolName);
    }

    [Fact]
    public async Task Presses_are_allowed_ahead_only_until_the_end_of_the_turn()
    {
        var queue = new ConfirmationQueue(() => new AppSettings());
        var first = queue.ConfirmDetailedAsync("operator", Ask(ComputerTool.ToolName), "chat");
        Assert.True(queue.TryPeek(out var request));
        Assert.True(request.CanAllowAhead);
        Assert.False(request.CanAllowForChat);

        queue.Allow("chat", ComputerTool.ToolName, AllowanceScope.Chat);
        _ = queue.Complete(request, approved: true);
        _ = await first;

        Assert.Equal(ApprovalSource.AllowTurn, queue.AllowedAhead("chat", ComputerTool.ToolName));
        queue.EndTurn("chat");
        Assert.Null(queue.AllowedAhead("chat", ComputerTool.ToolName));
    }
}
