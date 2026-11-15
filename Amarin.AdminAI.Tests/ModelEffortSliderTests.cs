using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ModelEffortSliderTests
{
    private readonly WpfFixture _wpf;

    public ModelEffortSliderTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Preview_targets_the_nearest_tick_and_holds_until_the_boundary_is_crossed() => _wpf.Ui.Invoke(() =>
    {
        var slider = new ModelEffortSlider();
        slider.SetLevel(0, 4);
        slider.BeginPreview(grabOffset: 5);

        slider.PreviewAt(64, 320, 20);
        Assert.Equal(0, slider.DisplayValue);
        slider.PreviewAt(65, 320, 20);
        Assert.Equal(1, slider.DisplayValue);
        slider.PreviewAt(105, 320, 20);
        Assert.Equal(1, slider.DisplayValue);
        slider.PreviewAt(164, 320, 20);
        Assert.Equal(1, slider.DisplayValue);
        slider.PreviewAt(165, 320, 20);
        Assert.Equal(2, slider.DisplayValue);
        slider.PreviewAt(0, 320, 20);
        Assert.Equal(0, slider.DisplayValue);
        slider.PreviewAt(400, 320, 20);
        Assert.Equal(3, slider.DisplayValue);
        Assert.Equal(0, slider.Value);

        slider.CancelPreview();
        Assert.Equal(0, slider.DisplayValue);
        return true;
    });

    [Fact]
    public void Same_level_refresh_preserves_preview_and_releasing_on_same_step_emits_no_change() => _wpf.Ui.Invoke(() =>
    {
        var slider = new ModelEffortSlider();
        slider.SetLevel(1, 4);
        var changes = 0;
        slider.ValueChanged += (_, _) => changes++;
        slider.BeginPreview(0);
        slider.PreviewAt(163, 320, 20);

        slider.SetLevel(1, 4);
        Assert.Equal(2, slider.DisplayValue);
        slider.PreviewAt(138, 320, 20);
        slider.CommitPreview();

        Assert.Equal(0, changes);
        Assert.Equal(1, slider.DisplayValue);
        return true;
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Committing_preserves_the_final_visual_position_and_discrete_automation_value(bool animationsEnabled) => _wpf.Ui.Invoke(() =>
    {
        var window = Application.Current.Windows.OfType<MainWindow>().Single();
        var host = (Panel)window.FindName("MessagesPanel")!;
        var slider = new ModelEffortSlider();
        var animations = UiMotion.Enabled;
        host.Children.Add(slider);
        try
        {
            UiMotion.Enabled = animationsEnabled;
            host.UpdateLayout();
            Assert.True(slider.IsVisible);
            slider.SetLevel(0, 4);
            var peer = new SliderAutomationPeer(slider);
            var range = Assert.IsAssignableFrom<IRangeValueProvider>(peer.GetPattern(PatternInterface.RangeValue));

            slider.BeginPreview(0);
            slider.PreviewAt(163, 320, 20);
            Assert.Equal(2d, slider.GetAnimationBaseValue(ModelEffortSlider.DisplayValueProperty));
            slider.PreviewAt(245, 320, 20);
            Assert.Equal(2d, slider.GetAnimationBaseValue(ModelEffortSlider.DisplayValueProperty));
            Assert.Equal(0, range.Value);
            slider.CommitPreview();
            Assert.Equal(2, range.Value);
            Assert.Equal(2d, slider.GetAnimationBaseValue(ModelEffortSlider.DisplayValueProperty));
            if (!animationsEnabled)
            {
                Assert.Equal(2, slider.DisplayValue);
            }

            range.SetValue(1.2);
            Assert.Equal(1, range.Value);
            Assert.Equal(1d, slider.GetAnimationBaseValue(ModelEffortSlider.DisplayValueProperty));
            if (!animationsEnabled)
            {
                Assert.Equal(1, slider.DisplayValue);
            }
            return true;
        }
        finally
        {
            host.Children.Remove(slider);
            UiMotion.Enabled = animations;
        }
    });
}
