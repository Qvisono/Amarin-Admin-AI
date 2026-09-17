using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Тумблер размышления в попапе.
/// </summary>
/// <remarks>
/// Он назывался «Disable thinking»: включённый тумблер означал выключенное размышление, а поле
/// рядом писало «Выкл». Двойное отрицание нельзя было прочитать — состояние выясняли, открывая
/// попап и вспоминая, в какую сторону считать. Теперь тумблер положительный: включён — думает.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ReasoningToggleTests
{
    private readonly WpfFixture _wpf;

    public ReasoningToggleTests(WpfFixture wpf) => _wpf = wpf;

    private static VeniceModelInfo Model() => new()
    {
        Id = "claude-sonnet-5",
        ModelSpec = new VeniceModelSpec
        {
            Capabilities = new VeniceModelCapabilities
            {
                SupportsReasoning = true,
                SupportsReasoningEffort = true,
                SupportsReasoningEffortWithTools = true,
                ReasoningEffortOptions = ["none", "low", "medium", "high", "max"],
                DefaultReasoningEffort = "medium"
            }
        }
    };

    private ReasoningPicker Build(bool disableThinking) => _wpf.Ui.Invoke(() =>
    {
        var picker = new ReasoningPicker();
        picker.SetUsesTools(true);
        picker.SetCatalog([Model()]);
        picker.SetModel("claude-sonnet-5");
        picker.SetChoice(disableThinking, "high");
        picker.Measure(new Size(240, 40));
        picker.Arrange(new Rect(0, 0, 240, 40));
        picker.UpdateLayout();
        return picker;
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_toggle_is_on_exactly_when_the_model_thinks(bool disableThinking)
    {
        var picker = Build(disableThinking);
        var checkedState = _wpf.Ui.Invoke(() =>
            ((CheckBox)picker.FindName("ThinkingToggle")!).IsChecked);

        Assert.Equal(!disableThinking, checkedState);
    }

    [Fact]
    public void Flipping_the_toggle_on_turns_thinking_on()
    {
        var picker = Build(disableThinking: true);
        var raised = 0;
        ReasoningChoiceChangedEventArgs? last = null;

        _wpf.Ui.Invoke<object?>(() =>
        {
            picker.ChoiceChanged += (_, e) =>
            {
                raised++;
                last = e;
            };

            var toggle = (CheckBox)picker.FindName("ThinkingToggle")!;
            toggle.IsChecked = true;
            picker.UpdateLayout();
            return null;
        });

        Assert.Equal(1, raised);
        Assert.False(last!.DisableThinking);
        Assert.False(picker.DisableThinking);
    }

    [Fact]
    public void The_hint_under_the_toggle_says_what_the_current_position_means()
    {
        // Подпись описывает состояние, а не действие: «модель обдумывает ответ», а не
        // «нажмите, чтобы включить». Именно этого не хватало, чтобы понять положение сразу.
        var (off, on) = _wpf.Ui.Invoke(() =>
        {
            var disabled = new ReasoningPicker();
            disabled.SetChoice(true, null);
            var enabled = new ReasoningPicker();
            enabled.SetChoice(false, null);
            return (((TextBlock)disabled.FindName("ThinkingHint")!).Text,
                    ((TextBlock)enabled.FindName("ThinkingHint")!).Text);
        });

        Assert.Equal(Loc.Get("S.Reasoning.ToggleOff"), off);
        Assert.Equal(Loc.Get("S.Reasoning.ToggleOn"), on);
        Assert.NotEqual(off, on);
    }

    [Fact]
    public void The_customize_page_shows_the_gauge_on_every_reasoning_field()
    {
        // Спидометр — единственная картинка, по которой уровень виден, не открывая попап.
        // В Customize таких полей семь подряд, и он там был выключен у всех.
        var without = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            string[] names =
            [
                "LiteReasoningPicker", "HeavyReasoningPicker", "RouterReasoningPicker",
                "TitleReasoningPicker", "AgentFastReasoningPicker",
                "AgentLiteReasoningPicker", "AgentHeavyReasoningPicker",
                "SynGuardReasoningPicker"
            ];

            return names
                .Where(name => ((ReasoningPicker)window.FindName(name)!).ShowGaugeIcon == false)
                .ToList();
        });

        Assert.True(without.Count == 0, "без спидометра: " + string.Join(", ", without));
    }
}
