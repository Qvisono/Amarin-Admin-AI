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
[Trait(WpfCollection.Category, WpfCollection.Trait)]
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
    public void The_settings_show_the_gauge_on_every_reasoning_field()
    {
        // Спидометр — единственная картинка, по которой уровень виден, не открывая попап.
        // На «Моделях» таких полей восемь подряд, и он был выключен у всех; у модели защиты —
        // на «Безопасности».
        var without = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var security = (SettingsSecurityPage)window.FindSetting("SecurityPage")!;
            string[] names =
            [
                "LiteReasoningPicker", "HeavyReasoningPicker", "RouterReasoningPicker",
                "TitleReasoningPicker", "SummaryReasoningPicker", "AgentFastReasoningPicker",
                "AgentLiteReasoningPicker", "AgentHeavyReasoningPicker"
            ];

            return names
                .Select(name => (name, picker: (ReasoningPicker)window.FindSetting(name)!))
                .Append((name: "SynGuardReasoningPicker", picker: (ReasoningPicker)security.FindName("SynGuardReasoningPicker")!))
                .Where(item => !item.picker.ShowGaugeIcon)
                .Select(item => item.name)
                .ToList();
        });

        Assert.True(without.Count == 0, "без спидометра: " + string.Join(", ", without));
    }
}
