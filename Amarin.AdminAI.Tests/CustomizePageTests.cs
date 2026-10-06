using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Бывшая страница Customize, разнесённая на «Модели», «Промпты» и защиту на «Безопасности»:
/// порядок разделов и потолок у полей системных промптов.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class CustomizePageTests
{
    private readonly WpfFixture _wpf;

    public CustomizePageTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static SettingsSecurityPage Security(MainWindow window) =>
        (SettingsSecurityPage)window.FindSetting("SecurityPage")!;

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
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindSetting("NavPrompts")!).IsChecked = true;

            // Технические промпты — на подстранице: поле меряется там, где его видит человек.
            var technical = name.StartsWith("Tech", StringComparison.Ordinal);
            if (technical)
            {
                SettingsDrill.Open((FrameworkElement)window.FindSetting("PromptsTechSub")!);
            }

            var box = (TextBox)window.FindSetting(name)!;
            var restore = box.Text;
            box.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 200).Select(i => $"строка промпта {i}"));
            window.UpdateLayout();

            var host = (ScrollViewer)box.Template.FindName("PART_ContentHost", box);
            var result = (box.ActualHeight, host.ScrollableHeight);

            box.Text = restore;
            if (technical)
            {
                SettingsDrill.TryBackIn(overlay);
            }

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindSetting("NavGeneral")!).IsChecked = true;
            return result;
        });

        Assert.True(height <= 176, $"{name}: высота {height} вместо потолка в 176");
        Assert.True(scrollable > 0, $"{name}: текст не прокручивается внутри поля");
    }

    [Fact]
    public void Models_go_answer_then_agent_then_service_and_the_prompts_live_apart()
    {
        // Порядок задан только разметкой, перепутать его правкой соседней строки легко. Промпты —
        // своей страницей: три больших редактора стояли под десятью строками моделей.
        var order = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindSetting("NavModels")!).IsChecked = true;
            window.UpdateLayout();

            // Разделы опознаются по первому полю каждого: заголовки — безымянные TextBlock.
            var page = Page(window, "ModelsPageScroll");
            var answer = IndexOf(page, (UIElement)window.FindSetting("LiteModelPicker")!);
            var agent = IndexOf(page, (UIElement)window.FindSetting("AgentFastModelPicker")!);
            var service = IndexOf(page, (UIElement)window.FindSetting("TitleModelPicker")!);
            var prompts = IndexOf(page, (UIElement)window.FindSetting("MainPromptTextBox")!);

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindSetting("NavGeneral")!).IsChecked = true;
            return (answer, agent, service, prompts);
        });

        Assert.True(order.answer >= 0 && order.answer < order.agent, "«Агент» оказался выше «Ответа в чате»");
        Assert.True(order.agent < order.service, "«Служебные задачи» оказались выше «Агента»");
        Assert.Equal(-1, order.prompts);
    }

    [Fact]
    public void The_guard_model_stays_folded_away_until_its_label_is_clicked()
    {
        // Слева внизу — голый идентификатор модели и стрелка; выбор модели и режима рассуждения
        // прячутся за ним, потому что нужны редко, а места рядом с тумблером уже нет.
        var (folded, unfolded, label) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindSetting("NavSecurity")!).IsChecked = true;
            window.UpdateLayout();

            var security = Security(window);
            var toggle = (ToggleButton)security.FindName("SynGuardModelToggle")!;
            var picker = (FrameworkElement)security.FindName("SynGuardModelPicker")!;
            var name = ((TextBlock)security.FindName("SynGuardModelLabel")!).Text;

            var before = picker.IsVisible;
            toggle.IsChecked = true;
            window.UpdateLayout();
            var after = picker.IsVisible;

            toggle.IsChecked = false;
            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindSetting("NavGeneral")!).IsChecked = true;
            return (before, after, name);
        });

        Assert.False(folded, "выбор модели защиты виден, не раскрывая пункт");
        Assert.True(unfolded, "пункт раскрыли, а выбора модели под ним нет");

        // Именно голый ID: строка служебная, и в ней важно точно знать, что стоит в настройке.
        Assert.Equal("deepseek-v4-flash-0731", label);
    }

    [Fact]
    public void The_guard_sits_on_the_security_page_right_after_the_access_mode()
    {
        // SynGuard — защита, а не выбор модели: он на «Безопасности», сразу за режимом доступа и
        // перед планом агента. Порядок задан только разметкой.
        var order = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindSetting("NavSecurity")!).IsChecked = true;
            window.UpdateLayout();

            // Корень страницы — первый ребёнок хоста подстраниц в её прокрутке.
            var security = Security(window);
            var host = (Panel)((ScrollViewer)security.FindName("PageScroll")!).Content;
            var page = (Panel)host.Children[0];

            var mode = IndexOf(page, (UIElement)security.FindName("ModeCombo")!);
            var guard = IndexOf(page, (UIElement)security.FindName("SynGuardToggle")!);
            var plan = IndexOf(page, (UIElement)security.FindName("PlanLiteToggle")!);

            // Тумблер — тот же, что у всех остальных переключателей настроек.
            var shared = ReferenceEquals(
                ((CheckBox)security.FindName("SynGuardToggle")!).Style,
                ((CheckBox)security.FindName("EncryptChatsToggle")!).Style);

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindSetting("NavGeneral")!).IsChecked = true;
            return (mode, guard, plan, shared);
        });

        Assert.True(order.mode >= 0 && order.mode < order.guard, "SynGuard оказался выше режима доступа");
        Assert.True(order.guard < order.plan, "SynGuard оказался ниже плана перед изменениями");
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
            var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
            overlay.Visibility = Visibility.Visible;
            ((RadioButton)window.FindSetting("NavPrompts")!).IsChecked = true;
            window.UpdateLayout();

            var frame = Frame((TextBox)window.FindSetting(box)!);
            var withSave = ReferenceEquals(frame, Frame((FrameworkElement)window.FindSetting(save)!));
            var withReset = reset is null ||
                            ReferenceEquals(frame, Frame((FrameworkElement)window.FindSetting(reset)!));

            overlay.Visibility = Visibility.Collapsed;
            ((RadioButton)window.FindSetting("NavGeneral")!).IsChecked = true;
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
            Main: Window().FindSetting("ResetMainPromptButton"),
            Ai: Window().FindSetting("ResetTechAiPromptButton"),
            Agent: Window().FindSetting("ResetTechAgentPromptButton")));

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
    /// Столбец страницы настроек — содержимое её прокрутки.
    /// </summary>
    /// <remarks>
    /// От прокрутки, а не от родителя поля: поля лежат внутри карточек, и их прямой родитель —
    /// уже не страница.
    /// </remarks>
    private static Panel Page(MainWindow window, string scroll) =>
        (Panel)((ScrollViewer)window.FindSetting(scroll)!).Content;

    /// <summary>Номер строки страницы, в которой лежит элемент; −1 — элемента на странице нет.</summary>
    private static int IndexOf(Panel page, UIElement element)
    {
        DependencyObject? node = element;
        while (node is not null && !page.Children.Contains(node as UIElement))
        {
            node = VisualTreeHelper.GetParent(node);
        }

        return node is null ? -1 : page.Children.IndexOf(node as UIElement);
    }
}
