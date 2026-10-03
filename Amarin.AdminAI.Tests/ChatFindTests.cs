using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Поиск по открытому чату (D1): что находится и как лента показывает найденное.</summary>
public sealed class ChatFindTests
{
    private static ChatDisplayMessage Message(string id, string text, string role = "user") =>
        new() { Id = id, Role = role, Text = text, Status = AssistantStatus.Complete };

    [Fact]
    public void Every_occurrence_is_a_hit_in_order_and_case_does_not_matter()
    {
        var hits = ChatFind.Find([Message("a", "Диск C почти полон"), Message("b", "диск D и диск E")], "ДИСК");

        Assert.Equal([("a", 0), ("b", 0), ("b", 1)], hits.Select(hit => (hit.MessageId, hit.Occurrence)));
    }

    [Fact]
    public void Tool_blocks_are_searched_as_one_hit_per_message()
    {
        var message = Message("t", "Готово.", "assistant");
        message.ToolRounds.Add(new ToolRound
        {
            Calls =
            [
                new ToolCallRecord { Name = "run_powershell", ArgumentsJson = "{\"command\":\"Get-Service Spooler\"}" },
                new ToolCallRecord { Name = "windows_service", ResultPreview = "Spooler stopped" }
            ]
        });

        var hit = Assert.Single(ChatFind.Find([message], "spooler"));

        Assert.True(hit.InTools);
    }

    [Fact]
    public void An_empty_query_finds_nothing() =>
        Assert.Empty(ChatFind.Find([Message("a", "текст")], "   "));

    [Fact]
    public void The_nested_agent_report_is_searched_too()
    {
        var message = Message("t", "", "assistant");
        message.ToolRounds.Add(new ToolRound
        {
            Calls = [new ToolCallRecord { Name = "init_agent", NestedAgent = new AgentRunRecord { ReportText = "SMART: warning" } }]
        });

        Assert.Single(ChatFind.Find([message], "smart"));
    }
}

/// <summary>Панель поиска на живом окне.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatFindUiTests
{
    private readonly WpfFixture _wpf;

    public ChatFindUiTests(WpfFixture wpf) => _wpf = wpf;

    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void An_occurrence_is_highlighted_in_the_rendered_message()
    {
        var painted = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var box = new RichTextBox();
            box.Document = new FlowDocument(new Paragraph(new Run("Первый диск и второй ДИСК")));
            var view = new StackPanel();
            view.Children.Add(box);

            var range = window.HighlightOccurrence(view, "диск", occurrence: 1);
            return (range?.Text, range?.GetPropertyValue(TextElement.BackgroundProperty));
        });

        Assert.Equal("ДИСК", painted.Item1);
        Assert.NotNull(painted.Item2);
    }

    [Fact]
    public void Ctrl_f_opens_the_bar_and_a_query_counts_from_the_bottom()
    {
        var (visible, count) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var original = (ChatSession)window.GetType().GetField("_session", Hidden)!.GetValue(window)!;
            var session = new ChatSession { Id = "find", Title = "Поиск" };
            session.Messages.Add(new ChatDisplayMessage { Id = "m1", Role = "user", Text = "первый диск", CreatedAt = DateTime.Now });
            session.Messages.Add(new ChatDisplayMessage { Id = "m2", Role = "assistant", Text = "второй диск", Status = AssistantStatus.Complete });
            try
            {
                window.GetType().GetField("_session", Hidden)!.SetValue(window, session);
                window.GetType().GetMethod("RenderSession", Hidden)!.Invoke(window, null);

                window.OpenFind();
                var bar = (ChatFindBar)window.FindName("FindBar");
                ((TextBox)bar.FindName("QueryBox")).Text = "диск";
                window.GetType().GetMethod("OnFindQueryChanged", Hidden)!.Invoke(window, ["диск"]);
                var text = ((TextBlock)bar.FindName("CountText")).Text;
                return (bar.Visibility, text);
            }
            finally
            {
                window.GetType().GetMethod("CloseFind", Hidden)!.Invoke(window, null);
                window.GetType().GetField("_session", Hidden)!.SetValue(window, original);
                window.GetType().GetMethod("RenderSession", Hidden)!.Invoke(window, null);
            }
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal("2/2", count);
    }
}
