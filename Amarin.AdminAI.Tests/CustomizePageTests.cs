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

        Assert.True(height <= 176, $"{name}: высота {height} вместо потолка в 176");
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
            var page = Page(window);
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

            var page = Page(window);
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

    [Theory]
    [InlineData("MainPromptTextBox", "SaveMainPromptButton", null)]
    [InlineData("TechAiPromptTextBox", "SaveTechAiPromptButton", "ResetTechAiPromptButton")]
    [InlineData("TechAgentPromptTextBox", "SaveTechAgentPromptButton", "ResetTechAgentPromptButton")]
    public void Each_prompt_keeps_its_own_buttons_inside_its_own_frame(string box, string save, string? reset)
    {
        // Кнопки стояли над полями, в столбик: три одинаковые надписи «Сохранить» на три поля,
        // и какая к какому относится, приходилось угадывать. Теперь каждая лежит в рамке
        // своего поля — проверяем именно это, а не то, что она просто существует.
        var (sharesFrame, resetSharesFrame) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindName("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindName("NavCustomize")!).IsChecked = true;
            window.UpdateLayout();

            var frame = Frame((TextBox)window.FindName(box)!);
            var withSave = ReferenceEquals(frame, Frame((FrameworkElement)window.FindName(save)!));
            var withReset = reset is null ||
                            ReferenceEquals(frame, Frame((FrameworkElement)window.FindName(reset)!));

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindName("NavBehavior")!).IsChecked = true;
            return (withSave, withReset);
        });

        Assert.True(sharesFrame, $"«Сохранить» у {box} стоит вне рамки поля");
        Assert.True(resetSharesFrame, $"«Сбросить» у {box} стоит вне рамки поля");
    }

    [Fact]
    public void Only_the_technical_prompts_can_be_reset()
    {
        // У основного промпта заводского текста нет — его пишет сам человек, и возвращать
        // такое поле не к чему.
        var names = _wpf.Ui.Invoke(() => (
            Main: Window().FindName("ResetMainPromptButton"),
            Ai: Window().FindName("ResetTechAiPromptButton"),
            Agent: Window().FindName("ResetTechAgentPromptButton")));

        Assert.Null(names.Main);
        Assert.NotNull(names.Ai);
        Assert.NotNull(names.Agent);
    }

    /// <summary>Рамка-карточка, в которой лежит элемент.</summary>
    private static Border Frame(FrameworkElement element)
    {
        DependencyObject? node = element;
        while (node is not null)
        {
            node = VisualTreeHelper.GetParent(node);
            if (node is Border { Name: "" } border && border.CornerRadius.TopLeft > 0)
            {
                return border;
            }
        }

        throw new InvalidOperationException($"у {element.GetType().Name} нет рамки-карточки");
    }

    /// <summary>
    /// Столбец страницы Customize.
    /// </summary>
    /// <remarks>
    /// От прокрутки, а не от родителя поля промпта: поля переехали внутрь карточек со своими
    /// кнопками, и их прямой родитель — уже не страница.
    /// </remarks>
    private static Panel Page(MainWindow window) =>
        (Panel)((ScrollViewer)window.FindName("CustomizePageScroll")!).Content;

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
