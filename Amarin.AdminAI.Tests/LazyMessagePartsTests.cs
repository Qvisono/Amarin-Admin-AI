using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Части сообщения, которых почти никогда не видно, строятся по требованию: список вызовов
/// инструментов — по разворачиванию, разбивка счёта — по наведению.
/// </summary>
/// <remarks>
/// Для ответа с десятком вызовов это сотни объектов на сообщение, и платила за них каждая
/// перерисовка ленты — то есть каждое открытие чата и каждое изменение состояния вызова.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class LazyMessagePartsTests
{
    private readonly WpfFixture _wpf;

    public LazyMessagePartsTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static ChatDisplayMessage WithTools() => new()
    {
        Role = "assistant",
        Id = "a1",
        Text = "готово",
        ResolvedModelId = "grok-4-6",
        ToolRounds =
        [
            new ToolRound
            {
                Calls =
                [
                    new ToolCallRecord
                    {
                        Id = "c1",
                        Name = "registry",
                        ArgumentsJson = "{}",
                        Status = ToolCallStatus.Done,
                        Success = true
                    }
                ]
            }
        ]
    };

    [Fact]
    public void The_tool_list_is_empty_until_it_is_opened()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var view = ChatMessageViews.CreateAssistant(Window(), WithTools());
            var expander = (Expander)view.ToolsHost.Children[0];

            Assert.False(expander.IsExpanded);
            Assert.Null(expander.Content);

            expander.IsExpanded = true;
            var body = Assert.IsType<StackPanel>(expander.Content);
            Assert.NotEmpty(body.Children.OfType<Grid>());
            return null;
        });
    }

    [Fact]
    public void A_blocked_call_opens_the_list_and_fills_it()
    {
        // Остановленный защитником вызов человек обязан увидеть, не разворачивая ничего руками.
        _wpf.Ui.Invoke<object?>(() =>
        {
            var message = WithTools();
            message.ToolRounds[0].Calls[0].Status = ToolCallStatus.Failed;
            message.ToolRounds[0].Calls[0].Success = false;
            message.ToolRounds[0].Calls[0].ResultPreview = SynGuard.BlockedMarker + " незачем";

            var view = ChatMessageViews.CreateAssistant(Window(), message);
            var expander = (Expander)view.ToolsHost.Children[0];

            Assert.True(expander.IsExpanded);
            Assert.NotNull(expander.Content);
            return null;
        });
    }

    [Fact]
    public void Reopening_the_list_shows_the_state_it_has_now()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var message = WithTools();
            var view = ChatMessageViews.CreateAssistant(Window(), message);
            var expander = (Expander)view.ToolsHost.Children[0];
            expander.IsExpanded = true;
            var first = expander.Content;

            // Пришёл второй вызов: содержимое обязано пересобраться, а не остаться прежним.
            message.ToolRounds[0].Calls.Add(new ToolCallRecord
            {
                Id = "c2",
                Name = "powershell",
                ArgumentsJson = "{}",
                Status = ToolCallStatus.Done,
                Success = true
            });

            view.UpdateTools(message);
            Assert.NotSame(first, expander.Content);

            var body = Assert.IsType<StackPanel>(expander.Content);
            Assert.True(body.Children.OfType<Grid>().Count() >= 2);
            return null;
        });
    }

    [Fact]
    public void The_price_breakdown_is_filled_only_when_it_is_shown()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var message = WithTools();
            message.Status = AssistantStatus.Complete;
            message.Cost = new VeniceCost { Usd = 0.15m, HasData = true };
            message.ModelCost = new VeniceCost { Usd = 0.02m, HasData = true };

            var view = ChatMessageViews.CreateAssistant(Window(), message);
            var tip = Assert.IsAssignableFrom<ToolTip>(view.CostChip.ToolTip);

            Assert.Empty(Labels(tip));

            CostBreakdownTooltip.Fill(tip, Window(), message);
            Assert.NotEmpty(Labels(tip));
            return null;
        });
    }

    /// <summary>Подписи строк разбивки, какими они лежат в дереве подсказки.</summary>
    private static List<string> Labels(ToolTip tip)
    {
        var found = new List<string>();
        Walk(tip.Content as DependencyObject);
        return found;

        void Walk(DependencyObject? node)
        {
            switch (node)
            {
                case null:
                    return;
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    found.Add(text.Text);
                    return;
                case Panel panel:
                    foreach (UIElement child in panel.Children)
                    {
                        Walk(child);
                    }

                    return;
                case Border border:
                    Walk(border.Child);
                    return;
            }
        }
    }
}
