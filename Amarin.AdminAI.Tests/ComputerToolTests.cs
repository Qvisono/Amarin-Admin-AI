using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Инструмент мыши и клавиатуры на подделке экрана: куда он нажимает, когда отказывает и что
/// возвращает модели. Тексты отказов не сверяются дословно — язык словаря переключают соседние
/// тесты; проверяется главное: что действие не случилось и почему.
/// </summary>
public sealed class ComputerToolTests
{
    private const int Own = 1000;

    private readonly FakeComputer _computer = new();
    private readonly FakeStage _stage = new();
    private readonly ComputerLease _lease = new();

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static ComputerElement Element(string key, string role, string name, ScreenRect bounds, int depth = 1) =>
        new(key, depth, role, name, null, bounds);

    private ComputerSession Session(bool unattended = false) =>
        new(_computer, _stage, new ComputerStart(unattended, "task"), _lease, Own);

    private static Task<ToolResult> Run(ComputerSession session, object arguments, bool canSee = true) =>
        new ComputerTool(session, canSee).ExecuteAsync(Args(arguments));

    // ───────────────────────── куда нажимает ─────────────────────────

    [Fact]
    public async Task A_point_of_a_screenshot_of_the_second_monitor_lands_on_its_own_pixels()
    {
        // Второй монитор правее первого и крупнее: снимок ужат вдвое, и точка снимка обязана
        // пересчитаться по его смещению и масштабу, а не по первому монитору.
        var window = _computer.AddWindow(0x2A, new ScreenRect(1920, 0, 2560, 1440));
        using var session = Session();

        var shot = await Run(session, new { action = "screenshot", window = window.Id });
        var click = await Run(session, new { action = "click", x = 640, y = 360, explanation = "press" });

        Assert.True(shot.Success, shot.Output);
        Assert.Contains("1280x720", shot.Output, StringComparison.Ordinal);
        Assert.True(click.Success, click.Output);
        Assert.Contains("click 3200,720", _computer.Log);
    }

    [Fact]
    public async Task An_element_is_pressed_where_it_is_now_not_where_it_was_in_the_tree()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "button", "Save", new ScreenRect(100, 100, 80, 30)));
        using var session = Session();
        var tree = await Run(session, new { action = "inspect", window = window.Id });
        Assert.Contains("e1 button \"Save\"", tree.Output, StringComparison.Ordinal);

        // Окно прокрутили: элемент тот же, место другое.
        _computer.Trees[7] = [Element("k1", "button", "Save", new ScreenRect(100, 300, 80, 30))];
        var click = await Run(session, new { action = "click", element = "e1", explanation = "save" });

        Assert.True(click.Success, click.Output);
        Assert.Contains("click 140,315", _computer.Log);
    }

    [Fact]
    public async Task A_gone_element_is_refused_instead_of_pressing_at_random()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "button", "Save", new ScreenRect(100, 100, 80, 30)));
        using var session = Session();
        _ = await Run(session, new { action = "inspect", window = window.Id });
        _computer.Trees[7] = [];

        var click = await Run(session, new { action = "click", element = "e1", explanation = "save" });

        Assert.False(click.Success);
        Assert.Contains("e1", click.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("click", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_tree_of_a_window_retires_its_old_ids()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "button", "Save", new ScreenRect(100, 100, 80, 30)));
        using var session = Session();
        _ = await Run(session, new { action = "inspect", window = window.Id });
        var second = await Run(session, new { action = "inspect", window = window.Id });

        var stale = await Run(session, new { action = "click", element = "e1", explanation = "save" });

        Assert.Contains("e2 button", second.Output, StringComparison.Ordinal);
        Assert.False(stale.Success);
    }

    [Fact]
    public async Task A_window_moved_after_the_screenshot_makes_its_points_stale()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();
        _ = await Run(session, new { action = "screenshot", window = window.Id });
        _computer.Move(7, new ScreenRect(50, 50, 800, 600));

        var click = await Run(session, new { action = "click", x = 10, y = 10, explanation = "press" });

        Assert.False(click.Success);
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("click", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_point_outside_the_screenshot_is_refused()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();
        _ = await Run(session, new { action = "screenshot", window = window.Id });

        var click = await Run(session, new { action = "click", x = 900, y = 10, explanation = "press" });

        Assert.False(click.Success);
        Assert.Contains("800", click.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_screenshot_x_and_y_mean_nothing()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var click = await Run(session, new { action = "click", x = 10, y = 10, explanation = "press" });

        Assert.False(click.Success);
        Assert.Empty(_computer.Log);
    }

    [Fact]
    public async Task The_panel_steps_aside_before_the_window_under_the_point_is_checked()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();
        _ = await Run(session, new { action = "screenshot", window = window.Id });

        _ = await Run(session, new { action = "click", x = 400, y = 20, explanation = "press" });

        Assert.Equal(new ScreenPoint(400, 20), Assert.Single(_stage.Avoided));
    }

    // ───────────────────────── чего не делает ─────────────────────────

    [Fact]
    public async Task Every_write_needs_an_explanation()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var typed = await Run(session, new { action = "type", text = "hello" });

        Assert.False(typed.Success);
        Assert.Empty(_computer.Log);
        Assert.False(session.Started);
    }

    [Fact]
    public async Task Amarin_itself_is_never_clicked()
    {
        var own = _computer.AddWindow(9, new ScreenRect(0, 0, 800, 600), processId: Own, title: "Amarin");
        using var session = Session();

        var inspect = await Run(session, new { action = "inspect", window = own.Id });
        var shot = await Run(session, new { action = "screenshot", monitor = 1 });
        var click = await Run(session, new { action = "click", x = 100, y = 100, explanation = "press" });

        Assert.False(inspect.Success);
        Assert.True(shot.Success);
        Assert.False(click.Success);
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("click", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_window_list_leaves_out_Amarin()
    {
        _computer.AddWindow(9, new ScreenRect(0, 0, 800, 600), processId: Own, title: "Amarin Admin AI");
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600), title: "Notes");
        using var session = Session();

        var list = await Run(session, new { action = "windows" });

        Assert.Contains("\"Notes\"", list.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Amarin Admin AI", list.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_typed_into_a_password_field()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        _computer.FocusedElement = new ComputerElement("p", 1, "edit", "Password", null, new ScreenRect(10, 10, 100, 20), IsPassword: true);
        using var session = Session();

        var typed = await Run(session, new { action = "type", text = "secret", explanation = "fill" });

        Assert.False(typed.Success);
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(nameof(ComputerWindowKind.Console))]
    [InlineData(nameof(ComputerWindowKind.RunDialog))]
    public async Task Windows_that_run_commands_take_no_input(string kind)
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600), kind: Enum.Parse<ComputerWindowKind>(kind));
        using var session = Session();

        var typed = await Run(session, new { action = "type", text = "dir", explanation = "list" });
        var keys = await Run(session, new { action = "key", keys = "enter", explanation = "run" });

        Assert.False(typed.Success);
        Assert.False(keys.Success);
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("type", StringComparison.Ordinal) ||
                                                     line.StartsWith("keys", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_administrator_window_is_refused_by_name()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600), title: "Device Settings", elevated: true);
        using var session = Session();

        var keys = await Run(session, new { action = "key", keys = "tab", explanation = "next" });

        Assert.False(keys.Success);
        Assert.Contains("Device Settings", keys.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("win+r")]
    [InlineData("Windows+X")]
    [InlineData("super+r")]
    public async Task Command_launcher_combinations_are_never_pressed(string keys)
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var result = await Run(session, new { action = "key", keys, explanation = "open" });

        Assert.False(result.Success);
        Assert.Empty(_computer.Log);
    }

    [Theory]
    [InlineData(nameof(ComputerWindowKind.Explorer), "delete", false)]
    [InlineData(nameof(ComputerWindowKind.Explorer), "shift+del", false)]
    [InlineData(nameof(ComputerWindowKind.FileDialog), "ctrl+d", false)]
    [InlineData(nameof(ComputerWindowKind.Normal), "delete", true)]
    [InlineData(nameof(ComputerWindowKind.Explorer), "ctrl+c", true)]
    public async Task Delete_is_refused_only_where_it_deletes_files(string kind, string keys, bool allowed)
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600), kind: Enum.Parse<ComputerWindowKind>(kind));
        using var session = Session();

        var result = await Run(session, new { action = "key", keys, explanation = "press" });

        Assert.Equal(allowed, result.Success);
    }

    [Fact]
    public async Task A_command_line_is_not_typed_anywhere()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var typed = await Run(session, new { action = "type", text = "powershell -NoProfile -Command Get-Date", explanation = "run" });

        Assert.False(typed.Success);
        Assert.Empty(_computer.Log);
    }

    [Theory]
    [InlineData(nameof(ComputerDesk.Locked))]
    [InlineData(nameof(ComputerDesk.Secure))]
    public async Task A_locked_or_secure_desktop_stops_every_action(string desk)
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        _computer.DeskState = Enum.Parse<ComputerDesk>(desk);
        using var session = Session();

        var list = await Run(session, new { action = "windows" });
        var keys = await Run(session, new { action = "key", keys = "enter", explanation = "go" });

        Assert.False(list.Success);
        Assert.False(keys.Success);
        Assert.Empty(_stage.Begins);
    }

    // ───────────────────────── «Показать» ─────────────────────────

    [Fact]
    public async Task Showing_frames_the_place_and_presses_nothing()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "button", "Save", new ScreenRect(100, 100, 80, 30)));
        using var session = Session();
        _ = await Run(session, new { action = "inspect", window = window.Id });

        var show = await Run(session, new { action = "show", element = "e1", caption = "Press Save" });

        Assert.True(show.Success, show.Output);
        Assert.Equal((new ScreenRect(100, 100, 80, 30), "Press Save"), Assert.Single(_stage.Shown));
        Assert.DoesNotContain(_computer.Log, line => line.StartsWith("click", StringComparison.Ordinal));
    }

    [Fact]
    public async Task There_is_no_one_to_show_without_a_person_or_a_panel()
    {
        // Без человека «Показать» ждало бы «Готово» до конца срока на каждом шаге.
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "button", "Save", new ScreenRect(100, 100, 80, 30)));
        using var unattended = Session(unattended: true);
        using var noPanel = new ComputerSession(_computer, null, new ComputerStart(false, "task"), new ComputerLease(), Own);
        _ = await Run(unattended, new { action = "inspect", window = window.Id });
        _ = await Run(noPanel, new { action = "inspect", window = window.Id });

        var alone = await Run(unattended, new { action = "show", element = "e1", caption = "Press Save" });
        var bare = await Run(noPanel, new { action = "show", element = "e1", caption = "Press Save" });

        Assert.False(alone.Success);
        Assert.False(bare.Success);
        Assert.Empty(_stage.Shown);
        Assert.Empty(_stage.Begins);
    }

    [Fact]
    public async Task A_model_that_cannot_see_is_refused_a_screenshot_and_nothing_is_captured()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var shot = await Run(session, new { action = "screenshot", window = "w7" }, canSee: false);

        Assert.False(shot.Success);
        Assert.False(shot.HasImages);
        Assert.Empty(_computer.Log);
    }

    // ───────────────────────── цепочка ─────────────────────────

    [Fact]
    public async Task A_sequence_runs_its_steps_in_order_and_reads_the_window_once_at_the_end()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "edit", "Message", new ScreenRect(10, 10, 200, 20)));
        using var session = Session();
        _ = await Run(session, new { action = "inspect", window = window.Id });
        _computer.Inspected.Clear();

        var result = await Run(session, new
        {
            action = "sequence",
            explanation = "send the note",
            steps = new object[]
            {
                new { action = "click", element = "e1" },
                new { action = "type", text = "hello" },
                new { action = "key", keys = "enter" }
            }
        });

        Assert.True(result.Success, result.Output);
        Assert.Equal(["click 110,20", "type hello", "keys enter"], _computer.Log);
        Assert.Equal([ComputerTool.AfterActionTree], _computer.Inspected);
        Assert.Contains(ComputerTool.TreeMark + "w7]", result.Output, StringComparison.Ordinal);
        Assert.Contains(_stage.Steps, step => step.EndsWith(": send the note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sequence_stops_at_the_first_refusal_and_says_where()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var result = await Run(session, new
        {
            action = "sequence",
            explanation = "do it",
            steps = new object[]
            {
                new { action = "type", text = "hello" },
                new { action = "key", keys = "win+r" },
                new { action = "key", keys = "enter" }
            }
        });

        Assert.False(result.Success);
        Assert.Equal(["type hello"], _computer.Log);
        Assert.Contains("win+r", result.Output, StringComparison.Ordinal);
        Assert.Contains(ComputerTool.TreeMark + "w7]", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sequence_stops_when_another_window_comes_to_the_front()
    {
        // Цепочка писала бы не туда, куда модель рассчитывала: окно впереди уже другое.
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        _computer.AddWindow(8, new ScreenRect(0, 0, 400, 300), title: "Dialog");
        _computer.AfterKeys = _ => _computer.ForegroundHandle = 8;
        using var session = Session();

        var result = await Run(session, new
        {
            action = "sequence",
            explanation = "do it",
            steps = new object[] { new { action = "key", keys = "enter" }, new { action = "type", text = "more" } }
        });

        Assert.True(result.Success, result.Output);
        Assert.Equal(["keys enter"], _computer.Log);
        Assert.Contains(ComputerTool.TreeMark + "w8]", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_focus_step_may_bring_another_window_forward()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var other = _computer.AddWindow(8, new ScreenRect(0, 0, 400, 300), title: "Other");
        using var session = Session();

        var result = await Run(session, new
        {
            action = "sequence",
            explanation = "switch and type",
            steps = new object[] { new { action = "focus", window = other.Id }, new { action = "type", text = "x" } }
        });

        Assert.True(result.Success, result.Output);
        Assert.Equal(["focus window 8", "type x"], _computer.Log);
    }

    [Fact]
    public async Task A_sequence_takes_only_actions_and_pauses()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session();

        var looking = await Run(session, new
        {
            action = "sequence",
            explanation = "x",
            steps = new object[] { new { action = "key", keys = "tab" }, new { action = "inspect" } }
        });
        var empty = await Run(session, new { action = "sequence", explanation = "x", steps = Array.Empty<object>() });
        var tooLong = await Run(session, new
        {
            action = "sequence",
            explanation = "x",
            steps = Enumerable.Repeat<object>(new { action = "key", keys = "tab" }, ComputerTool.MaxSequence + 1).ToArray()
        });

        Assert.False(looking.Success);
        Assert.False(empty.Success);
        Assert.False(tooLong.Success);
        Assert.Empty(_computer.Log);
    }

    // ───────────────────────── вставка ─────────────────────────

    [Theory]
    [InlineData("ctrl+v", "powershell -NoProfile -Command Get-Date", false)]
    [InlineData("shift+insert", "cmd /c del x", false)]
    [InlineData("ctrl+shift+v", "cmd /c del x", false)]
    [InlineData("ctrl+v", "see you at six", true)]
    [InlineData("ctrl+c", "cmd /c del x", true)]
    public async Task A_command_line_is_not_pasted_either(string keys, string clipboard, bool allowed)
    {
        // write_clipboard и клавиатура у агента вместе: вставка обошла бы запрет печатать команду.
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        _computer.Clipboard = clipboard;
        using var session = Session();

        var result = await Run(session, new { action = "key", keys, explanation = "paste" });

        Assert.Equal(allowed, result.Success);
    }

    // ───────────────────────── сеанс ─────────────────────────

    [Fact]
    public async Task Looking_does_not_take_the_mouse_and_the_first_write_does_once()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var session = Session(unattended: true);

        _ = await Run(session, new { action = "windows" });
        _ = await Run(session, new { action = "inspect", window = window.Id });
        Assert.Empty(_stage.Begins);

        _ = await Run(session, new { action = "key", keys = "ctrl+s", explanation = "save" });
        _ = await Run(session, new { action = "key", keys = "tab", explanation = "next" });

        var start = Assert.Single(_stage.Begins);
        Assert.True(start.Unattended);
        Assert.Contains("keys ctrl+s", _computer.Log);
        Assert.Contains(_stage.Steps, step => step.EndsWith(": save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancelling_the_countdown_closes_the_mouse_for_the_whole_run()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        _stage.Proceed = false;
        using var session = Session(unattended: true);

        var first = await Run(session, new { action = "key", keys = "enter", explanation = "go" });
        var second = await Run(session, new { action = "key", keys = "enter", explanation = "go" });

        Assert.False(first.Success);
        Assert.False(second.Success);
        Assert.Single(_stage.Begins);
        Assert.Empty(_computer.Log);
        Assert.False(_lease.Held);
    }

    [Fact]
    public async Task One_mouse_one_operator()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        using var first = Session();
        using var second = Session();

        var mine = await Run(first, new { action = "key", keys = "enter", explanation = "go" });
        var theirs = await Run(second, new { action = "key", keys = "enter", explanation = "go" });

        Assert.True(mine.Success, mine.Output);
        Assert.False(theirs.Success);

        first.Dispose();
        var after = await Run(second, new { action = "key", keys = "enter", explanation = "go" });
        Assert.True(after.Success, after.Output);
    }

    [Fact]
    public async Task The_end_of_the_run_gives_the_mouse_back_and_closes_the_panel()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600));
        var session = Session();
        _ = await Run(session, new { action = "windows" });
        Assert.Equal(0, _computer.Holds);

        _ = await Run(session, new { action = "key", keys = "enter", explanation = "go" });
        _ = await Run(session, new { action = "key", keys = "tab", explanation = "next" });
        Assert.Equal(1, _computer.HoldsOpen);

        session.Dispose();

        Assert.Equal(1, _stage.Ends);
        Assert.False(_lease.Held);

        // Экран и систему держит только сеанс: с его концом удержание отпущено.
        Assert.Equal(1, _computer.Holds);
        Assert.Equal(0, _computer.HoldsOpen);
    }

    [Fact]
    public async Task An_action_answers_with_a_fresh_tree_of_the_foreground_window()
    {
        _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: Element("k1", "edit", "Name", new ScreenRect(10, 10, 200, 20)));
        using var session = Session();

        var typed = await Run(session, new { action = "type", text = "report", explanation = "name it" });

        Assert.True(typed.Success, typed.Output);
        Assert.Contains(ComputerTool.TreeMark + "w7]", typed.Output, StringComparison.Ordinal);
        Assert.Contains("e1 edit \"Name\"", typed.Output, StringComparison.Ordinal);
        Assert.Equal(1, _computer.Settles);
    }

    [Fact]
    public async Task An_element_without_a_place_on_screen_is_clicked_through_automation()
    {
        var window = _computer.AddWindow(7, new ScreenRect(0, 0, 800, 600),
            elements: new ComputerElement("k1", 1, "button", "Hidden", null, default, Offscreen: true));
        using var session = Session();
        _ = await Run(session, new { action = "inspect", window = window.Id });

        var click = await Run(session, new { action = "click", element = "e1", explanation = "press" });
        var right = await Run(session, new { action = "right_click", element = "e1", explanation = "menu" });

        Assert.True(click.Success, click.Output);
        Assert.Contains("invoke k1", _computer.Log);
        Assert.False(right.Success);
    }

    // ───────────────────────── шлюз ─────────────────────────

    [Theory]
    [InlineData("windows", ToolEffect.Read)]
    [InlineData("inspect", ToolEffect.Read)]
    [InlineData("find", ToolEffect.Read)]
    [InlineData("screenshot", ToolEffect.Read)]
    [InlineData("wait", ToolEffect.Read)]
    [InlineData("show", ToolEffect.Read)]
    [InlineData("click", ToolEffect.Write)]
    [InlineData("type", ToolEffect.Write)]
    [InlineData("key", ToolEffect.Write)]
    [InlineData("drag", ToolEffect.Write)]
    [InlineData("focus", ToolEffect.Write)]
    [InlineData("sequence", ToolEffect.Write)]
    [InlineData("anything_new", ToolEffect.Write)]
    public void Looking_is_a_read_and_everything_else_a_write(string action, ToolEffect expected)
    {
        Assert.Equal(expected, ToolEffects.Classify(ComputerTool.ToolName, Args(new { action })));
    }

    [Fact]
    public void A_press_goes_without_a_question_in_the_normal_mode_even_when_the_guard_would_ask()
    {
        // Просьба человека — уже согласие. В beta.1 нажатие спрашивалось всегда, а осторожная модель
        // защиты («если не уверен — спроси») оставляла вопрос про Enter после «отправь».
        var press = Args(new { action = "key", keys = "enter", explanation = "press" });
        var normal = new AppSettings { ApprovalMode = ApprovalMode.Normal };

        var check = ToolGate.Check(ComputerTool.ToolName, press, normal);
        var guardWouldAsk = ToolGate.ApplyNormalReview(check, normal, withoutApproval: false);
        var guardLifts = ToolGate.ApplyNormalReview(check, normal, withoutApproval: true);

        Assert.Equal(ToolEffect.Write, check.Effect);
        Assert.Null(check.Question);
        Assert.Null(guardWouldAsk.Question);
        Assert.Null(guardLifts.Question);
    }

    [Fact]
    public void Asking_about_everything_still_asks_about_a_press_and_read_only_refuses_it()
    {
        var press = Args(new { action = "click", element = "e1", explanation = "press" });

        Assert.NotNull(ToolGate.Check(ComputerTool.ToolName, press, new AppSettings { ApprovalMode = ApprovalMode.AskAll }).Question);
        Assert.NotNull(ToolGate.Check(ComputerTool.ToolName, press, new AppSettings { ApprovalMode = ApprovalMode.ReadOnly }).Refusal);
        Assert.Null(ToolGate.Check(ComputerTool.ToolName, Args(new { action = "inspect" }), new AppSettings { ApprovalMode = ApprovalMode.AskAll }).Question);
    }

    [Fact]
    public void The_screen_is_never_used_by_a_parallel_batch_not_even_to_look()
    {
        // Два взгляда разом выдавали бы id наперебой: сеанс помнит одно дерево окна.
        var press = Args(new { action = "click", element = "e1", explanation = "press" });
        var look = Args(new { action = "inspect" });

        Assert.False(Agent.IsParallelSafeToolCall(ComputerTool.ToolName, press));
        Assert.False(Agent.IsParallelSafeToolCall(ComputerTool.ToolName, look));
    }
}
