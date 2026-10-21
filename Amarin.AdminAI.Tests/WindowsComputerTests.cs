using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using Amarin.Computer;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Исполнение на настоящей Windows — только чтением и через UI Automation: окно теста стоит за
/// краем экрана, нажатие идёт паттерном элемента, а настоящие мышь и клавиатура человека не
/// трогаются ни одним тестом.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class WindowsComputerTests
{
    private readonly WpfFixture _wpf;

    public WindowsComputerTests(WpfFixture wpf) => _wpf = wpf;

    private sealed record Probe(Window Window, long Handle, Func<int> Clicks);

    private Probe Open()
    {
        return _wpf.Ui.Invoke(() =>
        {
            var clicks = 0;
            var button = new Button { Content = "Probe" };
            button.Click += (_, _) => clicks++;
            var field = new TextBox { Text = "hello" };
            AutomationProperties.SetName(field, "Field");
            var remember = new CheckBox { Content = "Remember", IsChecked = true };

            // Безымянные контейнеры: в выдаче их быть не должно, их дети встают на их место.
            var inner = new Grid();
            inner.Children.Add(new StackPanel { Children = { button, field, remember } });
            var window = new Window
            {
                Title = "Computer probe " + Guid.NewGuid().ToString("N")[..6],
                Width = 320,
                Height = 240,
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = new Border { Child = inner }
            };
            window.Show();
            return new Probe(window, new WindowInteropHelper(window).Handle.ToInt64(), () => clicks);
        });
    }

    private void Close(Probe probe) => _wpf.Ui.Invoke<object?>(() =>
    {
        probe.Window.Close();
        return null;
    });

    [Fact]
    public async Task The_tree_names_controls_by_role_and_skips_nameless_containers()
    {
        var probe = Open();
        try
        {
            var computer = new WindowsComputer();
            var tree = (await computer.InspectAsync(probe.Handle, 100, TimeSpan.FromSeconds(5), CancellationToken.None)).Elements;

            Assert.Single(tree, element => element.Name == "Probe");
            Assert.Contains(tree, element => element is { Role: "button", Name: "Probe" });
            Assert.Contains(tree, element => element is { Role: "edit", Name: "Field", Value: "hello" });
            Assert.Contains(tree, element => element is { Role: "check box", Name: "Remember", Value: "on" });
            Assert.DoesNotContain(tree, element => element.Role is "pane" or "group" or "custom" && element.Name.Length == 0);
            Assert.All(tree, element => Assert.False(string.IsNullOrEmpty(element.Key)));
        }
        finally
        {
            Close(probe);
        }
    }

    [Fact]
    public async Task An_element_is_found_again_where_it_is_now_and_pressed_without_the_mouse()
    {
        var probe = Open();
        try
        {
            var computer = new WindowsComputer();
            var tree = (await computer.InspectAsync(probe.Handle, 100, TimeSpan.FromSeconds(5), CancellationToken.None)).Elements;
            var button = tree.Single(element => element is { Role: "button", Name: "Probe" });
            var before = await computer.ElementAsync(probe.Handle, button.Key, CancellationToken.None);

            _wpf.Ui.Invoke(() => probe.Window.Left = -31000);
            var after = await computer.ElementAsync(probe.Handle, button.Key, CancellationToken.None);
            var pressed = await computer.InvokeAsync(probe.Handle, button.Key, CancellationToken.None);

            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.NotEqual(before.Bounds.Left, after.Bounds.Left);
            Assert.True(pressed);

            // Нажатие паттерном WPF доводит до Click через очередь окна.
            for (var i = 0; i < 40 && _wpf.Ui.Invoke(probe.Clicks) == 0; i++)
            {
                await Task.Delay(50);
            }

            Assert.Equal(1, _wpf.Ui.Invoke(probe.Clicks));
        }
        finally
        {
            Close(probe);
        }
    }

    [Fact]
    public async Task A_found_element_carries_its_value_read_in_one_request()
    {
        // Значение поля нужно SynGuard: по одному Enter не понять, что уйдёт. Прежде свежий элемент
        // читался свойствами по одному и без значения.
        var probe = Open();
        try
        {
            var computer = new WindowsComputer();
            var tree = (await computer.InspectAsync(probe.Handle, 100, TimeSpan.FromSeconds(5), CancellationToken.None)).Elements;
            var field = tree.Single(element => element is { Role: "edit", Name: "Field" });

            var fresh = await computer.ElementAsync(probe.Handle, field.Key, CancellationToken.None);

            Assert.NotNull(fresh);
            Assert.Equal("hello", fresh.Value);
        }
        finally
        {
            Close(probe);
        }
    }

    [Fact]
    public async Task A_huge_window_is_read_only_up_to_the_limit_and_says_so()
    {
        // Окно браузера с тысячами элементов кэшировалось целиком на каждое нажатие — секундами.
        var probe = _wpf.Ui.Invoke(() =>
        {
            var panel = new StackPanel();
            for (var i = 0; i < 2000; i++)
            {
                panel.Children.Add(new Button { Content = "Item " + i });
            }

            var window = new Window
            {
                Title = "Huge probe " + Guid.NewGuid().ToString("N")[..6],
                Width = 320,
                Height = 240,
                Left = -32000,
                Top = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = panel
            };
            window.Show();
            return new Probe(window, new WindowInteropHelper(window).Handle.ToInt64(), () => 0);
        });
        try
        {
            var computer = new WindowsComputer();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var tree = await computer.InspectAsync(probe.Handle, 50, TimeSpan.FromSeconds(3), CancellationToken.None);
            clock.Stop();

            Assert.Equal(50, tree.Elements.Count);
            Assert.True(tree.Cut);
            Assert.Equal("Item 0", tree.Elements[0].Name);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), clock.Elapsed.ToString());
        }
        finally
        {
            Close(probe);
        }
    }

    [Fact]
    public async Task A_closed_window_leaves_nothing_to_press()
    {
        var probe = Open();
        var computer = new WindowsComputer();
        var tree = (await computer.InspectAsync(probe.Handle, 100, TimeSpan.FromSeconds(5), CancellationToken.None)).Elements;
        var key = tree.Single(element => element is { Role: "button", Name: "Probe" }).Key;

        Close(probe);

        Assert.Null(computer.Window(probe.Handle));
        Assert.Null(await computer.ElementAsync(probe.Handle, key, CancellationToken.None));
    }

    [Fact]
    public void The_elements_of_a_closed_window_are_let_go_on_the_next_tree()
    {
        // Программа живёт днями: без уборки каждое осмотренное окно держало бы свои элементы
        // UI Automation до выхода.
        var tree = new UiaTree();
        var first = Open();
        _ = tree.Inspect(new IntPtr(first.Handle), 100, TimeSpan.FromSeconds(5));
        Close(first);

        var second = Open();
        try
        {
            _ = tree.Inspect(new IntPtr(second.Handle), 100, TimeSpan.FromSeconds(5));

            Assert.Equal(1, tree.KnownWindows);
        }
        finally
        {
            Close(second);
        }
    }

    [Fact]
    public void Our_own_window_is_described_as_ours_and_not_elevated()
    {
        var probe = Open();
        try
        {
            var window = new WindowsComputer().Window(probe.Handle);

            Assert.NotNull(window);
            Assert.Equal(Environment.ProcessId, window.ProcessId);
            Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, window.ProcessName);
            Assert.False(window.Elevated);
            Assert.Equal(ComputerWindowKind.Normal, window.Kind);
            Assert.StartsWith("Computer probe", window.Title, StringComparison.Ordinal);
        }
        finally
        {
            Close(probe);
        }
    }

    [Fact]
    public void Monitors_are_numbered_from_the_primary_one()
    {
        var monitors = new WindowsComputer().Monitors();

        Assert.NotEmpty(monitors);
        Assert.True(monitors[0].Primary);
        Assert.Single(monitors, monitor => monitor.Primary);
        Assert.Equal(Enumerable.Range(1, monitors.Count), monitors.Select(monitor => monitor.Index));
        Assert.All(monitors, monitor => Assert.False(monitor.Bounds.IsEmpty));
    }
}

/// <summary>
/// Флаги блокировки сеанса читаются по верному смещению WTSINFOEX. Окна не нужно, поэтому
/// идёт и в CI.
/// </summary>
public sealed class ComputerSessionInfoTests
{
    [Fact]
    public void The_session_record_is_read_at_the_offsets_windows_lays_it_out()
    {
        // Номер сеанса лежит в том же объединении, что и флаги блокировки: сошёлся номер —
        // сошлось и смещение флагов. Со смещением от четвёртого байта здесь читался 0, и
        // активный сеанс выдавал себя за заблокированный.
        var info = WindowsComputer.SessionInfo();

        Assert.NotNull(info);
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().SessionId, info.Value.SessionId);
    }
}

/// <summary>
/// Имя программы окна спрашивается у процесса, а не берётся из кэша по номеру: Windows раздаёт
/// номера заново, и запомненное имя досталось бы чужому процессу. Окна не нужно — идёт и в CI.
/// </summary>
public sealed class ComputerProcessNameTests
{
    [Fact]
    public void The_name_comes_from_the_process_itself()
    {
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().ProcessName, WindowsComputer.ProcessName(Environment.ProcessId));
    }

    [Fact]
    public void A_process_that_cannot_be_opened_has_no_name()
    {
        // Номер 0 — «Бездействие системы»: открыть его нельзя никому.
        Assert.Equal("?", WindowsComputer.ProcessName(0));
    }
}

/// <summary>Чистые части исполнения: клавиши, имена типов, виды окон по классу.</summary>
public sealed class ComputerMappingTests
{
    [Theory]
    [InlineData("ctrl", 0x11)]
    [InlineData("win", 0x5B)]
    [InlineData("a", 0x41)]
    [InlineData("z", 0x5A)]
    [InlineData("7", 0x37)]
    [InlineData("f1", 0x70)]
    [InlineData("f24", 0x87)]
    [InlineData("enter", 0x0D)]
    [InlineData("delete", 0x2E)]
    public void Key_names_map_to_virtual_keys(string name, int expected)
    {
        Assert.Equal(expected, ComputerKeyMap.VirtualKey(name));
    }

    [Fact]
    public void Every_combination_the_core_accepts_can_be_pressed()
    {
        string[] samples =
        [
            "enter", "escape", "tab", "space", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
            "up", "down", "left", "right", "printscreen", "apps", "capslock", "numlock", "ctrl+alt+shift+win+q", "f13"
        ];

        foreach (var sample in samples)
        {
            var chord = ComputerKeys.Parse(sample);
            Assert.NotNull(chord);
            Assert.All(chord, key => ComputerKeyMap.VirtualKey(key));
        }
    }

    [Fact]
    public void Text_is_typed_in_batches_with_line_breaks_as_enter()
    {
        // По знаку с паузой 4000 знаков печатались полминуты; пачкой — доли секунды.
        var batches = WindowsComputer.TypingBatches("ab\ncd", 2).ToList();

        Assert.Equal(3, batches.Count);
        Assert.Equal([4, 4, 2], batches.Select(batch => batch.Length));
        Assert.Equal('a', (char)batches[0][0].Data.Keyboard.Scan);
        Assert.Equal(0x0D, batches[1][0].Data.Keyboard.Vk);
        Assert.Equal('c', (char)batches[1][2].Data.Keyboard.Scan);
        Assert.Equal('d', (char)batches[2][0].Data.Keyboard.Scan);
        Assert.All(batches.SelectMany(batch => batch), input => Assert.Equal(ComputerNative.InputKeyboard, input.Type));
    }

    [Fact]
    public void Arrows_and_editing_keys_are_sent_as_extended()
    {
        Assert.True(ComputerKeyMap.IsExtended(ComputerKeyMap.VirtualKey("left")));
        Assert.True(ComputerKeyMap.IsExtended(ComputerKeyMap.VirtualKey("delete")));
        Assert.False(ComputerKeyMap.IsExtended(ComputerKeyMap.VirtualKey("a")));
        Assert.False(ComputerKeyMap.IsExtended(ComputerKeyMap.VirtualKey("enter")));
    }

    [Fact]
    public void Roles_read_as_words_not_as_programmatic_names()
    {
        Assert.Equal("button", UiaTree.RoleOf(ControlType.Button));
        Assert.Equal("list item", UiaTree.RoleOf(ControlType.ListItem));
        Assert.Equal("check box", UiaTree.RoleOf(ControlType.CheckBox));
        Assert.Equal("element", UiaTree.RoleOf(null));
    }

    [Theory]
    [InlineData("ConsoleWindowClass", "conhost", nameof(ComputerWindowKind.Console))]
    [InlineData("CASCADIA_HOSTING_WINDOW_CLASS", "WindowsTerminal", nameof(ComputerWindowKind.Console))]
    [InlineData("Whatever", "WindowsTerminal", nameof(ComputerWindowKind.Console))]
    [InlineData("CabinetWClass", "explorer", nameof(ComputerWindowKind.Explorer))]
    [InlineData("Progman", "explorer", nameof(ComputerWindowKind.Explorer))]
    [InlineData("Chrome_WidgetWin_1", "chrome", nameof(ComputerWindowKind.Normal))]
    [InlineData("#32770", "notepad", nameof(ComputerWindowKind.Normal))]
    public void Windows_are_told_apart_by_class_and_process(string className, string process, string kind)
    {
        Assert.Equal(Enum.Parse<ComputerWindowKind>(kind), WindowsComputer.KindOf(IntPtr.Zero, className, process));
    }
}
