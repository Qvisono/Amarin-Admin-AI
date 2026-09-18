using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Боковая панель перестраивается только когда меняется состав списка. Открытый чат, идущий
/// ход и метка внимания — признаки на уже стоящих строках.
/// </summary>
/// <remarks>
/// Прежде каждое из этих состояний входило в слепок списка, и любое их изменение означало
/// <c>Children.Clear()</c> с пересозданием всех кнопок и их шаблонов. Переключение чата попадало
/// под это правило всегда.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ChatListRefreshTests
{
    private readonly WpfFixture _wpf;

    public ChatListRefreshTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static Button BuildRow(Panel panel, string id)
    {
        var row = new Button
        {
            Style = (Style)panel.FindResource("ChatItem"),
            Content = "Разговор про диски",
            Tag = id
        };

        panel.Children.Add(row);
        return row;
    }

    [Fact]
    public void The_open_chat_is_a_flag_on_the_row_not_a_second_style()
    {
        var (idle, active) = _wpf.Ui.Invoke(() =>
        {
            var panel = (Panel)Window().FindName("ChatListPanel")!;
            var row = BuildRow(panel, "test");
            try
            {
                row.ApplyTemplate();
                var plate = (Border)row.Template.FindName("Bg", row)!;
                var before = plate.Background;

                ChatRowState.SetIsActive(row, true);
                row.UpdateLayout();
                return (before, plate.Background);
            }
            finally
            {
                panel.Children.Remove(row);
            }
        });

        Assert.NotEqual(idle, active);
    }

    [Fact]
    public void The_pulse_is_tied_to_the_state_and_not_to_loading()
    {
        // Вечная анимация заводилась на Loaded самой точки, а Loaded случается и у свёрнутой:
        // на каждой строке списка тикал свой такт, и после каждой перерисовки панели заводился
        // новый их комплект. Проверяется проводка, а не сама анимация: в тестовом окне такты
        // не идут, и снимок в произвольный момент поймал бы что угодно.
        var (ownTriggers, enters, exits) = _wpf.Ui.Invoke(() =>
        {
            var panel = (Panel)Window().FindName("ChatListPanel")!;
            var row = BuildRow(panel, "test");
            try
            {
                row.ApplyTemplate();
                var dot = (FrameworkElement)row.Template.FindName("Working", row)!;

                var pulse = row.Template.Triggers
                    .OfType<Trigger>()
                    .Single(trigger => trigger.Property == ChatRowState.IsWorkingProperty);

                return (dot.Triggers.Count,
                    pulse.EnterActions.OfType<BeginStoryboard>().Count(),
                    pulse.ExitActions.OfType<StopStoryboard>().Count());
            }
            finally
            {
                panel.Children.Remove(row);
            }
        });

        Assert.Equal(0, ownTriggers);
        Assert.Equal(1, enters);
        Assert.Equal(1, exits);
    }

    [Fact]
    public void The_dot_shows_up_only_while_the_chat_answers()
    {
        var (idle, working) = _wpf.Ui.Invoke(() =>
        {
            var panel = (Panel)Window().FindName("ChatListPanel")!;
            var row = BuildRow(panel, "test");
            try
            {
                row.ApplyTemplate();
                var dot = (FrameworkElement)row.Template.FindName("Working", row)!;
                var before = dot.Visibility;

                ChatRowState.SetIsWorking(row, true);
                row.UpdateLayout();
                return (before, dot.Visibility);
            }
            finally
            {
                panel.Children.Remove(row);
            }
        });

        Assert.Equal(Visibility.Collapsed, idle);
        Assert.Equal(Visibility.Visible, working);
    }

    [Fact]
    public void Switching_chats_is_not_in_the_signature()
    {
        // Слепок описывает состав списка. Открытый чат в него не входит — иначе переключение
        // означало бы пересборку панели целиком ради переезда подсветки на соседнюю строку.
        var same = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var build = typeof(MainWindow).GetMethod(
                "BuildChatListSignature", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var session = typeof(MainWindow).GetField(
                "_session", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var items = new List<Amarin.Core.ChatIndexEntry>
            {
                new() { Id = "один", Title = "Первый", UpdatedAt = new DateTime(2026, 1, 1) },
                new() { Id = "два", Title = "Второй", UpdatedAt = new DateTime(2026, 1, 2) }
            };

            var original = session.GetValue(window);
            try
            {
                session.SetValue(window, new Amarin.Core.ChatSession { Id = "один" });
                var first = (string)build.Invoke(window, ["", items])!;

                session.SetValue(window, new Amarin.Core.ChatSession { Id = "два" });
                return first == (string)build.Invoke(window, ["", items])!;
            }
            finally
            {
                session.SetValue(window, original);
            }
        });

        Assert.True(same);
    }

}
