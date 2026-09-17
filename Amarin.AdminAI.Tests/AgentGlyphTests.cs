using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Значок строки <c>init_agent</c> в списке инструментов.
/// </summary>
/// <remarks>
/// Уровень агента выбирает отдельный запрос, и первые секунды его модель неизвестна. Прежде на
/// это время подставлялся <c>AgentHost.ForcedAgentModelId</c>, и любой агент начинал работу под
/// логотипом Grok — кем бы он потом ни оказался. Логотип здесь утверждение, а не украшение:
/// пока утверждать нечего, стоит обычная шестерёнка.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class AgentGlyphTests
{
    private readonly WpfFixture _wpf;

    public AgentGlyphTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static ChatDisplayMessage Turn(AgentRunRecord? agent) => new()
    {
        Role = "assistant",
        Id = "a1",
        ResolvedModelId = "grok-4-6",
        Status = AssistantStatus.Streaming,
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
                        ArgumentsJson = """{"prompt":"почини звук"}""",
                        Status = ToolCallStatus.Running,
                        NestedAgent = agent
                    }
                ]
            }
        ]
    };

    /// <summary>
    /// Значок строки вызова. Читается по логическому дереву: список инструментов свёрнут, и до
    /// визуального дерева его содержимое не доходит.
    /// </summary>
    private (ImageSource? Logo, string Glyph) Icon(AgentRunRecord? agent) => _wpf.Ui.Invoke(() =>
    {
        var view = ChatMessageViews.CreateAssistant(Window(), Turn(agent));
        var body = ((Expander)view.ToolsHost.Children[0]).Content;

        ImageSource? logo = null;
        var glyph = "";
        Walk(body as DependencyObject);
        return (logo, glyph);

        void Walk(DependencyObject? node)
        {
            switch (node)
            {
                case null:
                    return;
                case Image { Source: { } source }:
                    logo ??= source;
                    break;
                case TextBlock { Text: "⚙" or "🔍" } text:
                    glyph = text.Text;
                    break;
            }

            foreach (var child in Children(node))
            {
                Walk(child);
            }
        }

        static IEnumerable<DependencyObject> Children(DependencyObject node) => node switch
        {
            Panel panel => panel.Children.OfType<DependencyObject>(),
            Border { Child: DependencyObject child } => [child],
            Expander { Content: DependencyObject child } => [child],
            _ => []
        };
    });

    [Fact]
    public void An_agent_whose_level_is_not_chosen_yet_wears_no_logo_at_all()
    {
        // Запись агента появляется раньше, чем становится известен его уровень: маршрутизатор
        // уровня в этот момент ещё в сети.
        foreach (var agent in new AgentRunRecord?[]
                 {
                     null,
                     new() { Status = AgentRunStatus.Running },
                     new() { Status = AgentRunStatus.Running, ModelId = "   " }
                 })
        {
            var (logo, glyph) = Icon(agent);
            Assert.Null(logo);
            Assert.Equal("⚙", glyph);
        }
    }

    [Fact]
    public void Once_the_model_is_known_the_row_shows_that_model_and_not_grok()
    {
        var (logo, _) = Icon(new AgentRunRecord
        {
            Status = AgentRunStatus.Running,
            ModelId = "openai-gpt-56-luna"
        });

        var (openai, grok) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            return ((ImageSource)window.FindResource("OpenAI"), (ImageSource)window.FindResource("Grok"));
        });

        Assert.Same(openai, logo);
        Assert.NotSame(grok, logo);
    }

    [Fact]
    public void The_header_of_a_just_started_agent_is_not_a_dangling_word()
    {
        // «Агент » с хвостовым пробелом — обрывок строки: имени у записи ещё нет.
        var header = _wpf.Ui.Invoke(() =>
        {
            var view = ChatMessageViews.CreateAssistant(
                Window(),
                Turn(new AgentRunRecord { Status = AgentRunStatus.Running }));

            var body = (StackPanel)((Expander)view.ToolsHost.Children[0]).Content;
            var nested = body.Children.OfType<Expander>().Single();
            return ((TextBlock)((StackPanel)nested.Header).Children[0]).Text;
        });

        Assert.Equal("Агент", header);
    }
}
