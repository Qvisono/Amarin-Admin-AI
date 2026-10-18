using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ядро управления компьютером без экрана: разбор сочетаний клавиш, распознавание командной
/// строки и окно картинок в истории оператора.
/// </summary>
public sealed class ComputerCoreTests
{
    // ───────────────────────── клавиши ─────────────────────────

    [Theory]
    [InlineData("ctrl+s", "ctrl+s")]
    [InlineData("Control + Shift + S", "ctrl+shift+s")]
    [InlineData("shift+ctrl+s", "ctrl+shift+s")]
    [InlineData("Return", "enter")]
    [InlineData("esc", "escape")]
    [InlineData("alt+F4", "alt+f4")]
    [InlineData("cmd+r", "win+r")]
    [InlineData("super", "win")]
    [InlineData("PgDn", "pagedown")]
    [InlineData("ctrl+ctrl+a", "ctrl+a")]
    public void Combinations_are_read_into_one_spelling(string text, string expected)
    {
        var chord = ComputerKeys.Parse(text);

        Assert.NotNull(chord);
        Assert.Equal(expected, ComputerKeys.Join(chord));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl+a+b")]
    [InlineData("f25")]
    [InlineData("hello")]
    [InlineData("ctrl+%")]
    public void A_combination_that_cannot_be_pressed_at_once_is_not_read(string text)
    {
        Assert.Null(ComputerKeys.Parse(text));
    }

    // ───────────────────────── командная строка ─────────────────────────

    [Theory]
    [InlineData("cmd /c del x", true)]
    [InlineData("powershell -Command Get-Date", true)]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\" /k", true)]
    [InlineData("pwsh.exe -nop", true)]
    [InlineData("curl https://example.invalid/x", true)]
    [InlineData("%COMSPEC% /c echo", true)]
    [InlineData("powershell", false)]
    [InlineData("python tutorial", false)]
    [InlineData("net income for the year", false)]
    [InlineData("Hello, world", false)]
    [InlineData("", false)]
    public void A_command_line_is_a_launcher_with_an_argument(string text, bool expected)
    {
        Assert.Equal(expected, ComputerSafety.IsCommandLine(text));
    }

    [Fact]
    public void Window_ids_round_trip_and_reject_foreign_text()
    {
        var window = new ComputerWindow(0x1A2B, "t", "p", 1, default, 1);

        Assert.Equal("w1A2B", window.Id);
        Assert.Equal(0x1A2B, ComputerWindow.ParseId(window.Id));
        Assert.Equal(0x1A2B, ComputerWindow.ParseId(" w1a2b "));
        Assert.Null(ComputerWindow.ParseId("e12"));
        Assert.Null(ComputerWindow.ParseId("w"));
        Assert.Null(ComputerWindow.ParseId("w0"));
        Assert.Null(ComputerWindow.ParseId("wZZ"));
    }

    // ───────────────────────── окно картинок ─────────────────────────

    private static ChatMessage Shot(string label) => new()
    {
        Role = "user",
        Content = ChatContent.VisionMultiple(label, [new ImageAttachment("iVBORw0KGgo=", "image/png")])
    };

    private static ChatMessage Tool(string id, string text) => new()
    {
        Role = "tool",
        Name = ComputerTool.ToolName,
        ToolCallId = id,
        Content = ChatContent.Text(text)
    };

    private static bool HasImage(ChatMessage message) =>
        message.Content is { ValueKind: JsonValueKind.Array } content &&
        content.EnumerateArray().Any(part => part.TryGetProperty("type", out var type) && type.GetString() == "image_url");

    [Fact]
    public void Only_the_latest_screenshots_stay_pictures()
    {
        List<ChatMessage> history = [Shot("one"), Shot("two"), Shot("three")];

        var trimmed = ComputerHistory.Trim(history);

        Assert.False(HasImage(trimmed[0]));
        Assert.Equal(ComputerHistory.ImageStub, ChatContent.ReadText(trimmed[0].Content));
        Assert.True(HasImage(trimmed[1]));
        Assert.True(HasImage(trimmed[2]));
        Assert.True(HasImage(history[0]));
    }

    [Fact]
    public void An_older_tree_of_a_window_keeps_its_heading_and_loses_its_ids()
    {
        List<ChatMessage> history =
        [
            Tool("a", "Done.\n" + ComputerTool.TreeMark + "w7] \"Notes\" notes, 1 elements\n  e1 button \"Save\""),
            Tool("b", ComputerTool.TreeMark + "w9] \"Other\" other, 1 elements\n  e2 edit \"Name\""),
            Tool("c", "Typed.\n" + ComputerTool.TreeMark + "w7] \"Notes\" notes, 1 elements\n  e3 button \"Save\"")
        ];

        var trimmed = ComputerHistory.Trim(history);

        Assert.Equal("Done.\n" + ComputerHistory.TreeStub, ChatContent.ReadText(trimmed[0].Content));
        Assert.Equal("a", trimmed[0].ToolCallId);
        Assert.Same(history[1], trimmed[1]);
        Assert.Same(history[2], trimmed[2]);
    }

    [Fact]
    public void The_same_history_always_trims_the_same_way()
    {
        List<ChatMessage> history = [Shot("one"), Tool("a", ComputerTool.TreeMark + "w7] x"), Shot("two"), Shot("three")];

        var first = ComputerHistory.Trim(history).Select(message => message.Content?.GetRawText()).ToList();
        var second = ComputerHistory.Trim(history).Select(message => message.Content?.GetRawText()).ToList();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Other_tools_and_messages_are_left_alone()
    {
        var other = new ChatMessage { Role = "tool", Name = "run_powershell", ToolCallId = "x", Content = ChatContent.Text(ComputerTool.TreeMark + "w7] x") };
        var user = new ChatMessage { Role = "user", Content = ChatContent.Text("hello") };
        List<ChatMessage> history = [other, user, Tool("a", ComputerTool.TreeMark + "w7] y")];

        var trimmed = ComputerHistory.Trim(history);

        Assert.Same(other, trimmed[0]);
        Assert.Same(user, trimmed[1]);
    }
}
