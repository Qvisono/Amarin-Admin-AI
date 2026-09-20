using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Раздел «Hotkeys» на странице Behavior и поле записи сочетания.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class HotkeyFieldTests
{
    private readonly WpfFixture _wpf;

    public HotkeyFieldTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    [Fact]
    public void The_behavior_page_ends_with_a_row_per_action()
    {
        // Строки строятся кодом по HotkeyMap.All: вторая их копия в разметке разошлась бы с
        // ним молча — новое действие просто не показалось бы.
        var (rows, shown) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;

            // Ровно то, что делает заход в настройки: строки раздела строятся кодом по
            // HotkeyMap.All, а не лежат в разметке. Службы окну в тестах не выдаются, поэтому
            // зовём наполнение раздела напрямую — настройками по умолчанию.
            typeof(MainWindow)
                .GetMethod("LoadHotkeysUi", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [new AppSettings()]);
            window.UpdateLayout();

            var list = (Panel)window.FindName("HotkeyList")!;
            var fields = list.Children.OfType<Grid>()
                .SelectMany(row => row.Children.OfType<HotkeyField>())
                .ToList();

            var text = fields.Select(field => field.Gesture).ToList();
            overlay.Visibility = Visibility.Collapsed;
            return (list.Children.Count, text);
        });

        Assert.Equal(HotkeyMap.All.Count, rows);
        Assert.Equal(HotkeyMap.All.Select(a => a.DefaultGesture), shown);
    }

    [Fact]
    public void A_recorded_shortcut_replaces_the_old_one()
    {
        var (before, after) = _wpf.Ui.Invoke(() =>
        {
            var field = new HotkeyField();
            field.SetGesture("Ctrl+F", "Ctrl+F");
            var was = field.Gesture;

            StartRecording(field);
            Press(field, Key.N, ModifierKeys.Control | ModifierKeys.Shift);
            return (was, field.Gesture);
        });

        Assert.Equal("Ctrl+F", before);
        Assert.Equal("Ctrl+Shift+N", after);
    }

    [Fact]
    public void A_key_without_a_modifier_does_not_end_the_recording()
    {
        // Иначе одно нажатие «N» назначило бы сочетание, которым потом нельзя было бы
        // напечатать букву в сообщении.
        var gesture = _wpf.Ui.Invoke(() =>
        {
            var field = new HotkeyField();
            field.SetGesture("Ctrl+F", "Ctrl+F");

            StartRecording(field);
            Press(field, Key.N, ModifierKeys.None);
            Press(field, Key.LeftCtrl, ModifierKeys.Control);
            return field.Gesture;
        });

        Assert.Equal("Ctrl+F", gesture);
    }

    [Fact]
    public void Escape_leaves_the_shortcut_as_it_was()
    {
        var gesture = _wpf.Ui.Invoke(() =>
        {
            var field = new HotkeyField();
            field.SetGesture("Ctrl+Shift+N", "Ctrl+F");

            StartRecording(field);
            Press(field, Key.Escape, ModifierKeys.None);
            return field.Gesture;
        });

        Assert.Equal("Ctrl+Shift+N", gesture);
    }

    [Fact]
    public void The_reset_button_appears_only_when_there_is_something_to_undo()
    {
        var (atFactory, afterChange) = _wpf.Ui.Invoke(() =>
        {
            var field = new HotkeyField();
            field.SetGesture("Ctrl+F", "Ctrl+F");
            var quiet = Reset(field).Visibility;

            field.SetGesture("Ctrl+Shift+N", "Ctrl+F");
            return (quiet, Reset(field).Visibility);
        });

        Assert.Equal(Visibility.Collapsed, atFactory);
        Assert.Equal(Visibility.Visible, afterChange);
    }

    [Fact]
    public void The_reset_button_puts_the_factory_shortcut_back_and_says_so()
    {
        var (gesture, announced) = _wpf.Ui.Invoke(() =>
        {
            var field = new HotkeyField();
            field.SetGesture("Ctrl+Shift+N", "Ctrl+F");

            string? reported = null;
            field.GestureChanged += (_, value) => reported = value;
            Reset(field).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            return (field.Gesture, reported);
        });

        Assert.Equal("Ctrl+F", gesture);

        // Хозяин обязан узнать: иначе в настройках осталась бы прежняя запись.
        Assert.Equal("Ctrl+F", announced);
    }

    private static Button Reset(HotkeyField field) => (Button)field.FindName("ResetButton")!;

    private static void StartRecording(HotkeyField field) => field.BeginRecording();

    /// <remarks>
    /// Через Record, а не RaiseEvent: <c>Keyboard.Modifiers</c> в поднятом событии не
    /// подделать — на этом же спотыкались тесты лупы.
    /// </remarks>
    private static void Press(HotkeyField field, Key key, ModifierKeys modifiers) =>
        field.Record(key, modifiers);
}
