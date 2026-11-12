using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using Amarin.Core;
using Amarin.UI;
using ShapePath = System.Windows.Shapes.Path;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ModelPickerReasoningTests
{
    private readonly WpfFixture _wpf;

    public ModelPickerReasoningTests(WpfFixture wpf) => _wpf = wpf;

    private static VeniceModelInfo Model(bool mandatory, List<string>? effortOptions) => new()
    {
        Id = "claude-sonnet-5",
        ModelSpec = new VeniceModelSpec
        {
            Name = "Claude Sonnet 5",
            Capabilities = new VeniceModelCapabilities
            {
                SupportsReasoning = true,
                SupportsReasoningEffort = true,
                SupportsReasoningEffortWithTools = true,
                ReasoningMandatory = mandatory,
                ReasoningEffortOptions = effortOptions ?? ["none", "low", "medium", "high", "max"],
                DefaultReasoningEffort = "medium"
            }
        }
    };

    private T WithPicker<T>(ReasoningChoice choice, Func<ModelPickerPanel, T> read, bool mandatory = false,
        string selected = "claude-sonnet-5", List<string>? effortOptions = null) => _wpf.Ui.Invoke(() =>
    {
        var window = Application.Current.Windows.OfType<MainWindow>().Single();
        var host = (Panel)window.FindName("MessagesPanel")!;
        var picker = new ModelPickerPanel();
        host.Children.Add(picker);
        try
        {
            picker.SetKeys([new ApiKeyEntry("v1", "Test", "fake", ApiKeySource.Stored, true, LlmProvider.Venice)]);
            picker.SetCatalog(LlmProvider.Venice, [Model(mandatory, effortOptions), new VeniceModelInfo { Id = "plain-model" }]);
            picker.SetSelected(selected, null);
            picker.SetReasoning(choice);
            picker.ShowSelectedProvider();
            picker.RebuildNow();
            host.UpdateLayout();
            return read(picker);
        }
        finally
        {
            host.Children.Remove(picker);
        }
    });

    private static T Part<T>(ModelPickerPanel picker, string name) where T : FrameworkElement =>
        (T)picker.FindName(name)!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_brain_indicates_the_reasoning_state_and_preserves_checkbox_accessibility(bool disableThinking)
    {
        WithPicker(new ReasoningChoice(disableThinking, "high"), picker =>
        {
            var toggle = Part<CheckBox>(picker, "ThinkingToggle");
            var peer = new CheckBoxAutomationPeer(toggle);
            var automation = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            var brain = Assert.IsType<ShapePath>(toggle.Template.FindName("Brain", toggle));
            Assert.Equal(Loc.Get("S.Reasoning.Toggle"), peer.GetName());
            Assert.Equal(Loc.Get("S.Reasoning.Toggle"), toggle.ToolTip);
            Assert.Equal(!disableThinking, toggle.IsChecked);
            Assert.Equal(disableThinking ? ToggleState.Off : ToggleState.On, automation.ToggleState);
            if (disableThinking)
            {
                Assert.Null(brain.Fill);
                Assert.NotNull(brain.Stroke);
            }
            else
            {
                var accent = Assert.IsType<SolidColorBrush>(toggle.FindResource("Accent.Fill"));
                Assert.Equal(accent.Color, Assert.IsType<SolidColorBrush>(brain.Fill).Color);
            }

            return true;
        });
    }

    [Fact]
    public void Turning_thinking_on_and_using_slider_commands_reports_the_model_levels()
    {
        var choices = WithPicker(ReasoningChoice.Disabled, picker =>
        {
            var changes = new List<ReasoningChoice>();
            picker.ReasoningChanged += (_, choice) => changes.Add(choice);
            Part<CheckBox>(picker, "ThinkingToggle").IsChecked = true;
            var slider = Part<Slider>(picker, "EffortSlider");
            Assert.True(slider.IsEnabled);
            Slider.IncreaseSmall.Execute(null, slider);
            Assert.Equal("high", picker.SelectedReasoning?.Effort);
            Slider.MaximizeValue.Execute(null, slider);
            Assert.Equal("max", picker.SelectedReasoning?.Effort);
            Assert.Equal(ReasoningPolicy.EffortLabel("max"), AutomationProperties.GetItemStatus(slider));
            Assert.EndsWith(" · max", Part<TextBlock>(picker, "ChoiceModelLabel").Text, StringComparison.Ordinal);
            return changes;
        });

        Assert.Collection(choices,
            enabled => Assert.False(enabled.DisableThinking),
            high => Assert.Equal(new ReasoningChoice(false, "high"), high),
            max => Assert.Equal(new ReasoningChoice(false, "max"), max));
    }

    [Fact]
    public void Effort_dots_mark_each_allowed_thumb_position_including_the_endpoints()
    {
        var selectedIndex = WithPicker(new ReasoningChoice(false, "high"), picker =>
        {
            var slider = Part<Slider>(picker, "EffortSlider");
            var pane = Part<FrameworkElement>(picker, "ChoicePane");
            Assert.True(slider.ActualWidth > 0);
            var sliderLeft = slider.TranslatePoint(new Point(0, 0), pane).X;
            var sliderRight = slider.TranslatePoint(new Point(slider.ActualWidth, 0), pane).X;
            Assert.InRange(Math.Abs(sliderLeft), 0, 1);
            Assert.InRange(Math.Abs(pane.ActualWidth - sliderRight), 0, 1);
            var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            var thumb = Assert.IsType<Thumb>(track.Thumb);
            var ticks = Assert.IsAssignableFrom<TickBar>(slider.Template.FindName("EffortTicks", slider));
            var drawing = Assert.IsType<DrawingGroup>(VisualTreeHelper.GetDrawing(ticks));
            var centers = drawing.Children.OfType<GeometryDrawing>()
                .Select(part => part.Geometry).OfType<EllipseGeometry>()
                .Select(dot => dot.Center.X)
                .Order()
                .ToArray();

            Assert.Equal(4, centers.Length);
            Assert.True(slider.IsSnapToTickEnabled);
            Assert.True(slider.IsMoveToPointEnabled);
            var inset = thumb.ActualWidth / 2;
            var first = track.TranslatePoint(new Point(inset, 0), ticks).X;
            var last = track.TranslatePoint(new Point(track.ActualWidth - inset, 0), ticks).X;
            Assert.True(last > first);
            for (var i = 0; i < centers.Length; i++)
            {
                var expected = first + (last - first) * i / (centers.Length - 1);
                Assert.InRange(Math.Abs(centers[i] - expected), 0, 1);
            }

            var selectedCenter = thumb.TranslatePoint(new Point(inset, 0), ticks).X;
            Assert.InRange(Math.Abs(centers[(int)slider.Value] - selectedCenter), 0, 1);
            Assert.Equal(slider.Maximum, track.ValueFromPoint(new Point(track.ActualWidth - inset, 0)), precision: 5);
            return slider.Value;
        });

        Assert.Equal(2, selectedIndex);
    }

    [Fact]
    public void Dragging_previews_effort_until_release_and_discards_the_preview_when_the_model_changes()
    {
        var changes = WithPicker(new ReasoningChoice(false, "low"), picker =>
        {
            var choices = new List<ReasoningChoice>();
            picker.ReasoningChanged += (_, choice) => choices.Add(choice);
            var slider = Part<ModelEffortSlider>(picker, "EffortSlider");
            var effortLabel = Part<TextBlock>(picker, "EffortValue");
            var modelLabel = Part<TextBlock>(picker, "ChoiceModelLabel");

            slider.BeginPreview(0);
            slider.PreviewAt(73, 120, 20);
            Assert.Equal(2d, slider.GetAnimationBaseValue(ModelEffortSlider.DisplayValueProperty));
            Assert.Equal("High", effortLabel.Text);
            Assert.Equal("Claude Sonnet 5 · high", modelLabel.Text);
            Assert.Equal(0, slider.Value);
            Assert.Equal("low", picker.SelectedReasoning?.Effort);
            Assert.Empty(choices);

            picker.RebuildNow();
            Assert.Equal("High", effortLabel.Text);
            Assert.Equal("Claude Sonnet 5 · high", modelLabel.Text);

            slider.CommitPreview();
            Assert.Equal(2, slider.Value);
            Assert.Equal(ReasoningPolicy.EffortLabel("high"), AutomationProperties.GetItemStatus(slider));

            var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            slider.BeginPreview(0);
            slider.PreviewAt(14, 120, 20);
            Assert.Equal("Low", effortLabel.Text);
            Assert.Equal("Claude Sonnet 5 · low", modelLabel.Text);
            track.Thumb.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled: true));
            Assert.Equal(2, slider.Value);
            Assert.Equal(slider.Value, slider.DisplayValue);
            Assert.Equal("High", effortLabel.Text);
            Assert.Equal("Claude Sonnet 5 · high", modelLabel.Text);

            slider.BeginPreview(0);
            slider.PreviewAt(14, 120, 20);
            picker.SetSelected("plain-model", null);
            slider.CommitPreview();
            Assert.Equal(slider.Value, slider.DisplayValue);
            return choices;
        });

        Assert.Equal(new ReasoningChoice(false, "high"), Assert.Single(changes));
    }

    [Fact]
    public void A_single_effort_level_shows_a_centered_fixed_marker()
    {
        var status = WithPicker(new ReasoningChoice(false, "high"), picker =>
        {
            var slider = Part<Slider>(picker, "EffortSlider");
            var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            var marker = Assert.IsType<Ellipse>(slider.Template.FindName("FixedEffortMarker", slider));

            Assert.False(slider.IsEnabled);
            Assert.NotEqual(Visibility.Visible, track.Visibility);
            Assert.Equal(Visibility.Visible, marker.Visibility);
            Assert.True(marker.ActualWidth > 0);
            var center = marker.TranslatePoint(new Point(marker.ActualWidth / 2, 0), slider).X;
            Assert.InRange(Math.Abs(center - slider.ActualWidth / 2), 0, 1);
            return AutomationProperties.GetItemStatus(slider);
        }, effortOptions: ["none", "high"]);

        Assert.Equal(ReasoningPolicy.EffortLabel("high"), status);
    }

    [Fact]
    public void Mandatory_reasoning_keeps_its_slider_and_hides_the_off_switch()
    {
        var (toggle, slider) = WithPicker(ReasoningChoice.Disabled, picker =>
            (Part<FrameworkElement>(picker, "ThinkingControl").Visibility,
             Part<FrameworkElement>(picker, "EffortControl").Visibility), mandatory: true);

        Assert.Equal(Visibility.Collapsed, toggle);
        Assert.Equal(Visibility.Visible, slider);
    }

    [Fact]
    public void Picking_a_reasoning_model_updates_the_selection_and_shows_the_slider()
    {
        var (picked, choicePane, effort) = WithPicker(ReasoningChoice.Disabled, picker =>
        {
            Assert.Equal(Visibility.Collapsed, Part<FrameworkElement>(picker, "EffortControl").Visibility);
            var list = Part<ItemsControl>(picker, "ModelItems");
            ModelBinding? binding = null;
            picker.ModelPicked += (_, selection) => binding = selection;
            var row = Descendants(list).OfType<Button>().Single(button => Equals(button.Tag, "claude-sonnet-5"));
            row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            picker.RebuildNow();
            return (binding, Part<FrameworkElement>(picker, "ChoicePane").Visibility,
                Part<FrameworkElement>(picker, "EffortControl").Visibility);
        }, selected: "plain-model");

        Assert.Equal("claude-sonnet-5", picked?.ModelId);
        Assert.Equal(Visibility.Visible, choicePane);
        Assert.Equal(Visibility.Visible, effort);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
