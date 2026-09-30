using System.Windows.Input;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Сочетания D8: новые действия, Ctrl+Tab, шпаргалка и «↑ — последнее отправленное».</summary>
public sealed class HotkeyShortcutTests
{
    [Fact]
    public void No_two_actions_share_a_factory_shortcut()
    {
        var defaults = HotkeyMap.All.Select(action => action.DefaultGesture).ToList();

        Assert.Equal(defaults.Count, defaults.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData(HotkeyMap.SearchChats, "Ctrl+K")]
    [InlineData(HotkeyMap.NextChat, "Ctrl+Tab")]
    [InlineData(HotkeyMap.PreviousChat, "Ctrl+Shift+Tab")]
    [InlineData(HotkeyMap.OpenSettings, "Ctrl+OemComma")]
    [InlineData(HotkeyMap.Cheatsheet, "Ctrl+OemQuestion")]
    public void The_new_actions_have_their_familiar_keys(string id, string gesture) =>
        Assert.Equal(gesture, HotkeyMap.Find(id)!.DefaultGesture);

    [Fact]
    public void Service_key_names_are_shown_as_their_symbols()
    {
        Assert.Equal("Ctrl + ,", HotkeyMap.Display("Ctrl+OemComma"));
        Assert.Equal("Ctrl + /", HotkeyMap.Display("Ctrl+OemQuestion"));
        Assert.Equal("Ctrl + Shift + Tab", HotkeyMap.Display("Ctrl+Shift+Tab"));
    }

    [Fact]
    public void Tab_counts_only_together_with_ctrl()
    {
        Assert.True(Hotkeys.Matches("Ctrl+Tab", Key.Tab, ModifierKeys.Control));
        Assert.True(Hotkeys.Matches("Ctrl+Shift+Tab", Key.Tab, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.True(Hotkeys.TryRecord(Key.Tab, ModifierKeys.Control, out var recorded));
        Assert.Equal("Ctrl+Tab", recorded);

        // Alt+Tab — переключение окон Windows, его отнимать нельзя.
        Assert.False(Hotkeys.TryRecord(Key.Tab, ModifierKeys.Alt, out _));
        Assert.False(Hotkeys.Matches("Ctrl+Tab", Key.Tab, ModifierKeys.None));
    }

    [Fact]
    public void The_cheat_sheet_lists_every_action_and_the_fixed_keys()
    {
        var rows = HotkeySheetOverlay.BuildRows(new Dictionary<string, string> { [HotkeyMap.NewChat] = "Ctrl+Alt+N" });

        Assert.Equal(HotkeyMap.All.Count + 4, rows.Count);
        Assert.Contains(rows, row => row.Gesture == "Ctrl + Alt + N");
        Assert.Contains(rows, row => row.Gesture == "Esc");
        Assert.Contains(rows, row => row.Gesture == "↑");
    }

    [Fact]
    public void Arrow_up_brings_back_the_last_message_of_this_chat()
    {
        var session = new ChatSession { Id = "c" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "first" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Text = "answer" });
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "second" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Text = "answer 2" });

        Assert.Equal("second", MainWindow.LastSentText(session));
        Assert.Null(MainWindow.LastSentText(new ChatSession()));
    }
}
