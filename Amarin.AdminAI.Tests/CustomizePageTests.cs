using System.Windows;
using System.Windows.Controls;
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
