using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Выбор провайдера в интерфейсе: диалог ключа и строки списка.
/// </summary>
/// <remarks>
/// Разметку никто не собирает, пока её не покажут: опечатка в имени стиля или ресурса живёт
/// до первого открытия диалога, то есть до человека. Отсюда проверки на живом окне.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ProviderKeyDialogTests
{
    private readonly WpfFixture _wpf;

    public ProviderKeyDialogTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Part<T>(string name) where T : class =>
        (T)Window().FindSetting(name)!;

    private static T Dialog<T>(string name) where T : class =>
        (T)Window().KeyDialog.FindName(name)!;

    /// <summary>Открывает окно, даёт сделать своё и закрывает его, что бы ни случилось.</summary>
    private T WithDialog<T>(Func<KeyAddDialog, T> body, LlmProvider? provider = null) => _wpf.Ui.Invoke(() =>
    {
        var window = Window();
        window.OpenKeyDialog(provider);
        try
        {
            window.KeyDialog.UpdateLayout();
            return body(window.KeyDialog);
        }
        finally
        {
            Part<Grid>("KeyOverlay").Visibility = Visibility.Collapsed;
        }
    });

    /// <summary>
    /// В списке — все встроенные провайдеры по порядку и последним свой сервер; список взял
    /// стиль полей диалога, а не строки настроек.
    /// </summary>
    [Fact]
    public void The_provider_list_holds_every_provider_and_a_custom_server()
    {
        var (names, styled) = WithDialog(dialog =>
        {
            var box = Dialog<ComboBox>("ProviderBox");
            return (box.Items.OfType<ComboBoxItem>().Select(item => item.Content?.ToString()).ToList(),
                    ReferenceEquals(box.Style, Window().FindResource("DialogComboBox")));
        });

        Assert.Equal([.. ProviderSpec.All.Select(spec => spec.Name), Loc.Get("S.Key.CustomServer")], names);
        Assert.True(styled, "список провайдеров не взял стиль полей диалога");
    }

    /// <summary>Окно начинает скрытым, а открывается на Venice — так было до остальных провайдеров.</summary>
    [Fact]
    public void The_dialog_starts_hidden_and_opens_on_venice()
    {
        var hidden = _wpf.Ui.Invoke(() => Part<Grid>("KeyOverlay").Visibility);
        var provider = WithDialog(dialog => dialog.Provider);

        Assert.Equal(Visibility.Collapsed, hidden);
        Assert.Equal(LlmProvider.Venice, provider);
    }

    /// <summary>«Добавить ключ» у провайдера в плашке открывает окно сразу на нём.</summary>
    [Fact]
    public void The_dialog_opens_on_the_provider_it_was_asked_for() =>
        Assert.Equal(LlmProvider.Anthropic, WithDialog(dialog => dialog.Provider, LlmProvider.Anthropic));

    /// <summary>
    /// «Где взять» идёт за выбором: отправлять человека на venice.ai за ключом OpenRouter значило
    /// бы врать ему прямо в диалоге.
    /// </summary>
    [Fact]
    public void The_key_link_follows_the_chosen_provider()
    {
        var (venice, openRouter) = WithDialog(_ =>
        {
            var link = Dialog<Button>("KeysLink");
            var first = (string)link.ToolTip;
            Select(ProviderSpec.For(LlmProvider.OpenRouter).Name);
            return (first, (string)link.ToolTip);
        });

        Assert.Contains("venice", venice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("openrouter", openRouter, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Адрес встроенного провайдера виден, но не правится: иначе ключ провайдера ушёл бы на чужой
    /// сервер. У своего сервера поле открыто, а ссылке «Где взять» вести некуда.
    /// </summary>
    [Fact]
    public void The_address_is_fixed_for_a_provider_and_typed_for_a_server()
    {
        var (fixedText, fixedReadOnly, serverReadOnly, serverText, link, custom) = WithDialog(dialog =>
        {
            var address = Dialog<TextBox>("AddressValue");
            Select(ProviderSpec.For(LlmProvider.OpenAI).Name);
            var text = address.Text;
            var readOnly = address.IsReadOnly;

            Select(Loc.Get("S.Key.CustomServer"));
            return (text, readOnly, address.IsReadOnly, address.Text, Dialog<Button>("KeysLink").Visibility, dialog.IsCustomServer);
        });

        Assert.Equal(ProviderSpec.For(LlmProvider.OpenAI).BaseUrl, fixedText);
        Assert.True(fixedReadOnly);
        Assert.False(serverReadOnly);
        Assert.Equal("", serverText);
        Assert.Equal(Visibility.Hidden, link);
        Assert.True(custom);
    }

    /// <summary>Выбор провайдера не меняет высоту окна: оно стоит по центру и подпрыгивало бы.</summary>
    [Fact]
    public void Choosing_a_server_keeps_the_dialog_height()
    {
        var (provider, server) = WithDialog(_ =>
        {
            var card = Dialog<FrameworkElement>("Card");
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var first = card.DesiredSize.Height;
            Select(Loc.Get("S.Key.CustomServer"));
            card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return (first, card.DesiredSize.Height);
        });

        Assert.Equal(provider, server, 1);
    }

    /// <summary>
    /// Вставленный ключ Anthropic сам выбирает Anthropic; выбранный вручную провайдер догадка
    /// не перебивает.
    /// </summary>
    [Fact]
    public void A_pasted_key_picks_its_provider_until_one_is_chosen_by_hand()
    {
        var (guessed, kept) = WithDialog(dialog =>
        {
            Dialog<TextBox>("KeyValue").Text = "sk-ant-api03-abcdef";
            var first = dialog.Provider;

            Select(ProviderSpec.For(LlmProvider.DeepSeek).Name);
            Dialog<TextBox>("KeyValue").Text = "gsk_abcdef";
            return (first, dialog.Provider);
        });

        Assert.Equal(LlmProvider.Anthropic, guessed);
        Assert.Equal(LlmProvider.DeepSeek, kept);
    }

    /// <summary>Ни одна подпись диалога не осталась пустой — иначе поле выглядит безымянным.</summary>
    [Fact]
    public void No_label_in_the_dialog_is_blank()
    {
        // Только подписи самого окна: у полей в шаблоне есть своя подсказка, и пустой она бывает
        // законно — у поля без подсказки.
        var blank = WithDialog(dialog => Labels(dialog)
            .Where(text => text.Name.Length > 0 && ReferenceEquals(dialog.FindName(text.Name), text))
            .Where(text => string.IsNullOrWhiteSpace(text.Text))
            .Select(text => text.Name)
            .Where(name => name is not "Status")
            .ToList());

        Assert.True(blank.Count == 0, "пустые подписи: " + string.Join(", ", blank));
    }

    /// <summary>Выбор пункта списка — тем же путём, что щелчок человека: через выбранный элемент.</summary>
    private static void Select(string name)
    {
        var box = Dialog<ComboBox>("ProviderBox");
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Content, name));
    }

    private static IEnumerable<TextBlock> Labels(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                yield return text;
            }

            foreach (var nested in Labels(child))
            {
                yield return nested;
            }
        }
    }
}

/// <summary>
/// Поле выбора модели на странице «Customize».
/// </summary>
/// <remarks>
/// Поля заполняются при открытии настроек, а выбор меняется и мимо них — лечением каталога.
/// Пока их никто не перечитывал, в них оставалось имя модели, которой в новом списке нет
/// вовсе: человек читал это как сброшенную настройку, хотя его выбор цел.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SettingsModelFieldTests
{
    private readonly WpfFixture _wpf;

    public SettingsModelFieldTests(WpfFixture wpf) => _wpf = wpf;

    private static ModelPickerField Field() =>
        (ModelPickerField)Application.Current.Windows
            .OfType<MainWindow>()
            .Single()
            .FindSetting("TitleModelPicker")!;

    /// <summary>
    /// Поле показывает то, что ему сказали в последний раз, и берёт имя без приставки
    /// провайдера — её человеку видеть незачем.
    /// </summary>
    [Fact]
    public void A_field_told_a_new_model_shows_that_model()
    {
        var (before, after, tip) = _wpf.Ui.Invoke(() =>
        {
            var field = Field();
            var saved = field.SelectedModelId;
            try
            {
                field.SetSelected("grok-4-6", null);
                var first = Label(field);

                field.SetSelected("openrouter:google/gemini-2.5-flash", null);
                return (first, Label(field), (string?)Tip(field));
            }
            finally
            {
                field.SetSelected(saved, null);
            }
        });

        Assert.Equal("Grok 4.6", before);
        Assert.Equal("Gemini 2.5 Flash", after);

        // Полный идентификатор — под курсором: имена моделей у провайдеров совпадают, и
        // отличить одну от другой иначе негде.
        Assert.Equal("openrouter:google/gemini-2.5-flash", tip);
    }

    /// <summary>
    /// Плашка выбора строится при первом открытии, а всё, что поле узнало раньше — ключи и
    /// выбор, — получает разом: подпись с именем ключа работает и без неё.
    /// </summary>
    [Fact]
    public void The_picker_panel_is_built_on_first_open_with_what_the_field_was_told()
    {
        var (empty, keyLabel, selected, keyOfPanel, placed) = _wpf.Ui.Invoke(() =>
        {
            var field = new ModelPickerField();
            field.SetKeys([new ApiKeyEntry("k2", "Work", "vk-second-key", ApiKeySource.Stored, false)]);
            field.SetSelected("grok-4-6", "k2");
            var popup = (Popup)field.FindName("PickerPopup")!;
            var before = popup.Child is null;
            var label = ((TextBlock)field.FindName("SelectedKeyLabel")!).Text;

            // Тем же путём, что нажатие: Checked кнопки, но без настоящего открытия окна попапа.
            ((ToggleButton)field.FindName("OpenButton")!).RaiseEvent(new RoutedEventArgs(ToggleButton.CheckedEvent));
            var panel = popup.Child as ModelPickerPanel;
            return (before, label, panel?.SelectedModelId, panel?.LabelOf("k2"), panel is not null && ReferenceEquals(panel, field.Panel));
        });

        Assert.True(empty, "плашка не должна строиться вместе с полем");
        Assert.Equal("Work", keyLabel);
        Assert.Equal("grok-4-6", selected);
        Assert.Equal("Work", keyOfPanel);
        Assert.True(placed);
    }

    private static string Label(ModelPickerField field) =>
        ((TextBlock)field.FindName("SelectedLabel")!).Text;

    private static string? Tip(ModelPickerField field) =>
        ((ToggleButton)field.FindName("OpenButton")!).ToolTip as string;
}

/// <summary>
/// Строки списка ключей, когда провайдеров два.
/// </summary>
/// <remarks>
/// Строки собираются кодом, а не шаблоном разметки, поэтому сломать их можно молча — ошибка
/// видна только глазами на открытой странице.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ProviderKeyRowTests
{
    private readonly WpfFixture _wpf;

    public ProviderKeyRowTests(WpfFixture wpf) => _wpf = wpf;

    private static SettingsKeyPage Page() =>
        (SettingsKeyPage)Application.Current.Windows
            .OfType<MainWindow>()
            .Single()
            .FindSetting("KeyPage")!;

    /// <summary>
    /// Провайдер виден у каждой строки. Плашка не только у своих ключей: две строки окружения
    /// различаются лишь названием переменной, и с одного взгляда их не разобрать.
    /// </summary>
    [Fact]
    public void Every_row_says_whose_key_it_is()
    {
        var labels = _wpf.Ui.Invoke(() =>
        {
            var page = Page();
            page.ShowForShot(Report(), Keys());
            return Rows(page)
                .Select(row => string.Join("|", Texts(row).Select(text => text.Text)))
                .ToList();
        });

        Assert.Equal(3, labels.Count);
        Assert.Contains(labels, row => row.Contains("Venice", StringComparison.Ordinal));
        Assert.Contains(labels, row => row.Contains("OpenRouter", StringComparison.Ordinal));
        Assert.All(labels, row => Assert.True(
            row.Contains("Venice", StringComparison.Ordinal) ||
            row.Contains("OpenRouter", StringComparison.Ordinal),
            "строка без провайдера: " + row));
    }

    /// <summary>Строка ключа OpenRouter собирается наравне с остальными и не роняет страницу.</summary>
    [Fact]
    public void A_mixed_list_still_builds()
    {
        var count = _wpf.Ui.Invoke(() =>
        {
            var page = Page();
            page.ShowForShot(Report(), Keys());
            return Rows(page).Count;
        });

        Assert.Equal(3, count);
    }

    private static List<FrameworkElement> Rows(SettingsKeyPage page) =>
        ((ItemsControl)page.FindName("KeyRows")!).ItemsSource?
            .OfType<FrameworkElement>()
            .ToList() ?? [];

    private static IEnumerable<TextBlock> Texts(DependencyObject root)
    {
        if (root is TextBlock text)
        {
            yield return text;
        }

        var children = root is Border border
            ? (border.Child is null ? [] : new[] { (DependencyObject)border.Child })
            : root is Panel panel
                ? panel.Children.OfType<DependencyObject>().ToArray()
                : [];

        foreach (var child in children)
        {
            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }

    private static SpendReport Report() => new() { Period = SpendPeriod.Week, Status = SpendStatus.Local };

    private static IReadOnlyList<ApiKeyEntry> Keys() =>
    [
        new("environment", "VENICE_API_KEY", "VENabcdefghijklmnopq",
            ApiKeySource.Environment, true),
        new("k2", "Рабочий", "vk-second-key-abcdefgh", ApiKeySource.Stored, false),
        new("k3", "Роутер", "sk-or-v1-abcdefghijkl", ApiKeySource.Stored, false,
            LlmProvider.OpenRouter)
    ];
}
