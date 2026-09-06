using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// One number in the meta row covers the model, every tool that charged for itself and every
/// sub-agent. What can quietly go wrong is the arithmetic: a part counted twice, or a part left
/// out, would make the rows disagree with the total the user is being asked to trust.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CostBreakdownTests
{
    private readonly WpfFixture _wpf;

    public CostBreakdownTests(WpfFixture wpf) => _wpf = wpf;

    private static VeniceCost Usd(decimal amount) => new() { Usd = amount, HasData = true };

    /// <summary>A turn that drew a picture and also ran a sub-agent.</summary>
    private static ChatDisplayMessage Turn() => new()
    {
        Role = "assistant",
        Id = "a1",
        ResolvedModelId = "claude-sonnet-5",
        Text = "готово",
        Status = AssistantStatus.Complete,
        ModelCost = Usd(0.02m),
        Cost = Usd(0.15m),
        ToolRounds =
        [
            new ToolRound
            {
                Calls =
                [
                    new ToolCallRecord
                    {
                        Id = "c1",
                        Name = "generate_image",
                        Status = ToolCallStatus.Done,
                        Success = true,
                        Cost = Usd(0.10m)
                    },
                    new ToolCallRecord
                    {
                        Id = "c2",
                        Name = "init_agent",
                        Status = ToolCallStatus.Done,
                        Success = true,
                        Cost = Usd(0.03m),
                        NestedAgent = new AgentRunRecord
                        {
                            ModelId = "grok-4-6",
                            DisplayName = "Агент grok-4-6",
                            Status = AgentRunStatus.Complete,
                            Cost = Usd(0.03m)
                        }
                    }
                ]
            }
        ]
    };

    private static List<string> Lines(ToolTip tip)
    {
        var texts = new List<string>();
        Walk(tip.Content as DependencyObject);
        return texts;

        void Walk(DependencyObject? node)
        {
            if (node is null)
            {
                return;
            }

            if (node is TextBlock text)
            {
                texts.Add(text.Text);
            }

            foreach (var child in System.Windows.Media.VisualTreeHelper.GetChildrenCount(node) > 0
                         ? Enumerable.Range(0, System.Windows.Media.VisualTreeHelper.GetChildrenCount(node))
                             .Select(i => System.Windows.Media.VisualTreeHelper.GetChild(node, i))
                         : Children(node))
            {
                Walk(child);
            }
        }

        static IEnumerable<DependencyObject> Children(DependencyObject node) => node switch
        {
            Panel panel => panel.Children.OfType<DependencyObject>(),
            Border { Child: DependencyObject child } => [child],
            _ => []
        };
    }

    [Fact]
    public void The_breakdown_names_the_model_every_paid_tool_and_every_agent()
    {
        var lines = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            return Lines(CostBreakdownTooltip.Build(window, Turn()));
        });

        Assert.Contains(lines, line => line.Contains("Sonnet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("generate_image", lines);
        Assert.Contains(lines, line => line.StartsWith("Агент", StringComparison.Ordinal));
        Assert.Contains("Итого", lines);

        // init_agent's own row would repeat the agent's charge under a second name.
        Assert.DoesNotContain("init_agent", lines);
    }

    [Fact]
    public void The_rows_add_up_to_the_total()
    {
        var message = Turn();
        var parts = new[] { message.ModelCost!.Usd }
            .Concat(message.ToolRounds
                .SelectMany(round => round.Calls)
                .Select(call => call.NestedAgent?.Cost?.Usd ?? call.Cost?.Usd ?? 0m))
            .Sum();

        Assert.Equal(message.Cost!.Usd, parts);
    }

    [Fact]
    public void A_turn_that_only_talked_gets_no_total_line()
    {
        // With a single row there is nothing to add up, and "Итого" under it would just repeat
        // the row above.
        var lines = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            return Lines(CostBreakdownTooltip.Build(window, new ChatDisplayMessage
            {
                Role = "assistant",
                ResolvedModelId = "claude-sonnet-5",
                ModelCost = Usd(0.004m),
                Cost = Usd(0.004m)
            }));
        });

        Assert.DoesNotContain("Итого", lines);
    }

    [Fact]
    public void The_price_chip_carries_the_breakdown_after_a_reload()
    {
        // The tooltip is built from the message, not from live turn state, so a chat reopened
        // from disk has to get the same one.
        var hasTooltip = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var view = ChatMessageViews.CreateAssistant(window, Turn());
            var chip = (Border)typeof(AssistantMessageView)
                .GetProperty("CostChip", BindingFlags.Instance | BindingFlags.Public)!
                .GetValue(view)!;
            return chip.ToolTip is ToolTip;
        });

        Assert.True(hasTooltip);
    }
}
