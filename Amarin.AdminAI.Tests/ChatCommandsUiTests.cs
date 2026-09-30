using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Команды «/» (D9) и отметка сжатия (D10) на живом окне со службами.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatCommandsUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-commands-ui-" + Guid.NewGuid().ToString("N"));

    public ChatCommandsUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static ChatSession Session(MainWindow window) =>
        (ChatSession)typeof(MainWindow).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private T With<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Typing_a_slash_opens_the_command_list_and_a_path_does_not()
    {
        var (slash, path) = With((window, _) =>
        {
            var box = (TextBox)window.FindName("MessageTextBox");
            var popup = (Popup)window.FindName("CommandSuggestPopup");
            box.Text = "/re";
            box.CaretIndex = box.Text.Length;
            var opened = popup.IsOpen;
            box.Text = "/usr/bin";
            box.CaretIndex = box.Text.Length;
            return (opened, popup.IsOpen);
        });

        Assert.True(slash);
        Assert.False(path);
    }

    [Fact]
    public void Readonly_toggles_the_chat_and_shows_the_chip()
    {
        var (on, chip, off) = With((window, _) =>
        {
            Call(window, "RunLocalCommand", new LocalCommand(ChatCommands.ReadOnly, ""));
            var first = Session(window).ReadOnly;
            var visible = ((FrameworkElement)window.FindName("ReadOnlyChip")).Visibility;
            Call(window, "RunLocalCommand", new LocalCommand(ChatCommands.ReadOnly, ""));
            return (first, visible, Session(window).ReadOnly);
        });

        Assert.True(on);
        Assert.Equal(Visibility.Visible, chip);
        Assert.False(off);
    }

    [Fact]
    public void The_compacted_part_is_marked_in_the_feed()
    {
        var marked = With((window, services) =>
        {
            var session = new ChatSession { Id = "compacted", Title = "t", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            for (var i = 1; i <= 4; i++)
            {
                session.Messages.Add(new ChatDisplayMessage { Id = "u" + i, Role = "user", Text = "q" + i, CreatedAt = DateTime.Now });
                session.Messages.Add(new ChatDisplayMessage
                {
                    Id = "a" + i, Role = "assistant", Text = "a" + i, Status = AssistantStatus.Complete, CreatedAt = DateTime.Now
                });
                session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("q" + i) });
                session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("a" + i) });
            }

            ContextCompaction.Apply(session, ContextCompaction.Plan(session)!, "summary");
            services.ChatStore.Save(session);
            services.ChatStore.Flush();
            Call(window, "OpenChat", "compacted");
            Call(window, "MaterializeHost", ((System.Collections.IEnumerable)typeof(MainWindow)
                    .GetField("_messageHosts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!)
                .Cast<ChatMessageHost>().Single(host => host.Id == "a2"));
            window.UpdateLayout();

            var text = Loc.Get("S.Compact.Mark");
            var host = ((System.Collections.IEnumerable)typeof(MainWindow)
                    .GetField("_messageHosts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!)
                .Cast<ChatMessageHost>().Single(item => item.Id == "a2");
            return host.Child is Panel panel && panel.Children.OfType<Grid>()
                .Any(grid => grid.Children.OfType<TextBlock>().Any(block => block.Text == text));
        });

        Assert.True(marked);
    }
}
