using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Лента: прогон оператора мыши виден под строкой <c>use_computer</c> внутри блока агента — так
/// же, как агент под <c>init_agent</c>. До 1.37.0 вложенный в агента прогон лента не рисовала.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ComputerFeedTests
{
    private readonly WpfFixture _wpf;

    public ComputerFeedTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_operator_run_shows_under_its_call_inside_the_agent()
    {
        var operatorRun = new AgentRunRecord
        {
            Status = AgentRunStatus.Complete,
            ModelId = "vision-model",
            DisplayName = "PC operator probe",
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "o1",
                            Name = ComputerTool.ToolName,
                            ArgumentsJson = """{"action":"key","keys":"ctrl+s","explanation":"save"}""",
                            Status = ToolCallStatus.Done,
                            Success = true
                        }
                    ]
                }
            ]
        };
        var agent = new AgentRunRecord
        {
            Status = AgentRunStatus.Complete,
            ModelId = "agent-model",
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "a1",
                            Name = UseComputerTool.ToolName,
                            ArgumentsJson = """{"task":"save"}""",
                            Status = ToolCallStatus.Done,
                            Success = true,
                            NestedAgent = operatorRun
                        }
                    ]
                }
            ]
        };
        var turn = new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "m1",
            ResolvedModelId = "grok-4-6",
            Status = AssistantStatus.Complete,
            ToolRounds =
            [
                new ToolRound
                {
                    Calls =
                    [
                        new ToolCallRecord
                        {
                            Id = "c1",
                            Name = "init_agent",
                            ArgumentsJson = """{"prompt":"save"}""",
                            Status = ToolCallStatus.Done,
                            Success = true,
                            NestedAgent = agent
                        }
                    ]
                }
            ]
        };

        var headers = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var view = ChatMessageViews.CreateAssistant(window, turn);
            var tools = (Expander)view.ToolsHost.Children[0];
            tools.IsExpanded = true;

            var found = new List<string>();
            Walk(tools.Content as DependencyObject);
            return found;

            void Walk(DependencyObject? node)
            {
                switch (node)
                {
                    case null:
                        return;
                    case Expander expander:
                        found.Add(Text(expander.Header as DependencyObject));
                        Walk(expander.Content as DependencyObject);
                        return;
                    case Panel panel:
                        foreach (var child in panel.Children.OfType<DependencyObject>())
                        {
                            Walk(child);
                        }

                        return;
                    case Border { Child: DependencyObject child }:
                        Walk(child);
                        return;
                }
            }

            static string Text(DependencyObject? node) => node switch
            {
                TextBlock text => text.Text,
                Panel panel => string.Join(" ", panel.Children.OfType<DependencyObject>().Select(Text)),
                _ => ""
            };
        });

        Assert.Contains(headers, header => header.Contains("PC operator probe", StringComparison.Ordinal));
    }
}
