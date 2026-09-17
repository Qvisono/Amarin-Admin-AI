using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Страница Customize: порядок разделов и потолок у полей системных промптов.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CustomizePageTests
{
    private readonly WpfFixture _wpf;

    public CustomizePageTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    [Theory]
    [InlineData("MainPromptTextBox")]
    [InlineData("TechAiPromptTextBox")]
    [InlineData("TechAgentPromptTextBox")]
    public void A_long_prompt_scrolls_inside_its_own_box(string name)
    {
        // Поле росло вместе с текстом: системный промпт на сотню строк растягивал страницу
        // настроек на тысячи пикселей, и до соседних полей приходилось прокручивать его целиком.
        var (height, scrollable) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindName("NavCustomize")!).IsChecked = true;

            var box = (TextBox)window.FindName(name)!;
            var restore = box.Text;
            box.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 200).Select(i => $"строка промпта {i}"));
            window.UpdateLayout();

            var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
            var result = (box.ActualHeight, host.ScrollableHeight);

            box.Text = restore;
            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;
            return result;
        });

        Assert.True(height <= 120, $"{name}: высота {height} вместо потолка в 120");
        Assert.True(scrollable > 0, $"{name}: текст не прокручивается внутри поля");
    }

    [Fact]
    public void Service_models_come_before_the_system_prompts()
    {
        // Порядок задан только разметкой, перепутать его правкой соседней строки легко.
        var order = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindName("NavCustomize")!).IsChecked = true;
            window.UpdateLayout();

            // Разделы опознаются по первому полю каждого: заголовки — безымянные TextBlock.
            var page = (Panel)VisualTreeHelper.GetParent((TextBox)window.FindName("MainPromptTextBox")!);
            var answer = IndexOf(page, (UIElement)window.FindName("LiteModelPicker")!);
            var service = IndexOf(page, (UIElement)window.FindName("TitleModelPicker")!);
            var prompts = IndexOf(page, (UIElement)window.FindName("MainPromptTextBox")!);

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;
            return (answer, service, prompts);
        });

        Assert.True(order.answer < order.service, "«Служебные модели» оказались выше «Моделей ответа»");
        Assert.True(order.service < order.prompts, "«Служебные модели» оказались ниже системных промптов");
    }

    [Fact]
    public void The_guard_model_stays_folded_away_until_its_label_is_clicked()
    {
        // Слева внизу — голый идентификатор модели и стрелка; выбор модели и режима рассуждения
        // прячутся за ним, потому что нужны редко, а места рядом с тумблером уже нет.
        var (folded, unfolded, label) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindName("NavCustomize")!).IsChecked = true;
            window.UpdateLayout();

            var toggle = (ToggleButton)window.FindName("SynGuardModelToggle")!;
            var picker = (FrameworkElement)window.FindName("SynGuardModelPicker")!;
            var name = ((TextBlock)window.FindName("SynGuardModelLabel")!).Text;

            var before = picker.IsVisible;
            toggle.IsChecked = true;
            window.UpdateLayout();
            var after = picker.IsVisible;

            toggle.IsChecked = false;
            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;
            return (before, after, name);
        });

        Assert.False(folded, "выбор модели защиты виден, не раскрывая пункт");
        Assert.True(unfolded, "пункт раскрыли, а выбора модели под ним нет");

        // Именно голый ID: строка служебная, и в ней важно точно знать, что стоит в настройке.
        Assert.Equal("deepseek-v4-flash-0731", label);
    }

    [Fact]
    public void The_guard_sits_between_the_service_models_and_the_system_prompts()
    {
        // Порядок задан только разметкой, и защите место рядом с моделями, которые она проверяет,
        // а не среди системных промптов.
        var order = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindName("NavCustomize")!).IsChecked = true;
            window.UpdateLayout();

            var page = (Panel)VisualTreeHelper.GetParent((TextBox)window.FindName("MainPromptTextBox")!);
            var service = IndexOf(page, (UIElement)window.FindName("AgentHeavyModelPicker")!);
            var guard = IndexOf(page, (UIElement)window.FindName("SynGuardToggle")!);
            var prompts = IndexOf(page, (UIElement)window.FindName("MainPromptTextBox")!);

            // Тумблер — тот же, что у всех остальных переключателей настроек.
            var shared = ReferenceEquals(
                ((CheckBox)window.FindName("SynGuardToggle")!).Style,
                ((CheckBox)window.FindName("AutoScrollToggle")!).Style);

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;
            return (service, guard, prompts, shared);
        });

        Assert.True(order.service < order.guard, "SynGuard оказался выше служебных моделей");
        Assert.True(order.guard < order.prompts, "SynGuard оказался ниже системных промптов");
        Assert.True(order.shared, "у тумблера защиты свой стиль вместо общего SettingsToggle");
    }

    /// <summary>Номер строки страницы, в которой лежит элемент.</summary>
    private static int IndexOf(Panel page, UIElement element)
    {
        DependencyObject? node = element;
        while (node is not null && !page.Children.Contains(node as UIElement))
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return page.Children.IndexOf(node as UIElement);
    }
}
