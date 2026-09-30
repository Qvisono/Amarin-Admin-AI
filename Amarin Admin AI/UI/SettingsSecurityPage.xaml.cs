using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Безопасность»: режим доступа и выключатели инструментов.
/// </summary>
/// <remarks>
/// Отдельным <see cref="UserControl"/>, как Key, Info и Instructions: разметка главного окна и так
/// на три тысячи строк. Режим доступа переехал сюда из «Поведения» — четыре режима с разной
/// ценой ошибки читаются карточками с объяснением, а не строкой выпадающего списка.
/// <para>
/// Контролы нигде не читаются как состояние: каждый живёт в своём обработчике под
/// <see cref="_loading"/>, правда лежит в <see cref="AppSettings"/>. Строки инструментов
/// строятся при первом наполнении, а не в конструкторе: страница создаётся вместе с окном, до
/// первого кадра, а сорок строк ради страницы, которую могут и не открыть, там не нужны.
/// </para>
/// </remarks>
public partial class SettingsSecurityPage : UserControl
{
    private readonly Dictionary<string, CheckBox> _toolToggles = new(StringComparer.OrdinalIgnoreCase);
    private AppServices? _services;
    private bool _loading;

    public SettingsSecurityPage()
    {
        InitializeComponent();

        SmoothScroll.SetIsEnabled(PageScroll, true);
        SmoothScroll.SetDragScroll(PageScroll, true);
    }

    /// <summary>Ставится главным окном, когда службы уже собраны.</summary>
    internal void Attach(AppServices services) => _services = services;

    /// <summary>«Заблокировать сейчас»: закрывает окно сам хозяин — экран блокировки его.</summary>
    internal event EventHandler? LockRequested;

    /// <summary>Наполняет страницу из настроек. Зовётся из <c>LoadSettingsUi</c>.</summary>
    internal void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureToolRows();

        _loading = true;
        try
        {
            foreach (var card in ModeCards.Children.OfType<RadioButton>())
            {
                card.IsChecked = card.Tag is string tag &&
                                 string.Equals(tag, settings.ApprovalMode.ToString(), StringComparison.Ordinal);
            }

            foreach (var (tool, toggle) in _toolToggles)
            {
                toggle.IsChecked = !ToolGate.IsDisabled(settings, tool);
            }

            EnableAllToolsButton.IsEnabled = settings.DisabledTools is { Count: > 0 };

            EncryptChatsToggle.IsChecked = settings.EncryptChats;
            LoadAutoLock(settings);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Автоблокировка: выбор минут и кнопка «Заблокировать сейчас» — только у профиля с паролем.
    /// </summary>
    /// <remarks>
    /// Без пароля снимать блокировку нечем. Сохранённый выбор при этом не стирается: заведёт
    /// человек пароль — блокировка заработает так, как он её уже настроил.
    /// </remarks>
    private void LoadAutoLock(AppSettings settings)
    {
        var hasPassword = HasPassword;
        var minutes = AutoLock.Normalize(settings.AutoLockMinutes);
        foreach (var chip in AutoLockChoices.Children.OfType<RadioButton>())
        {
            var value = int.Parse((string)chip.Tag, System.Globalization.CultureInfo.InvariantCulture);
            if (value > 0)
            {
                chip.Content = Loc.Format("S.Security.Minutes", value);
            }

            chip.IsChecked = value == minutes;
            chip.IsEnabled = hasPassword;
        }

        AutoLockNeedsPassword.Visibility = hasPassword ? Visibility.Collapsed : Visibility.Visible;
        LockNowButton.IsEnabled = hasPassword;
    }

    private bool HasPassword =>
        _services is not null && _services.Profiles.Active(_services.ProfileRegistry).HasPassword;

    private void EncryptChatsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        _services.Settings.EncryptChats = EncryptChatsToggle.IsChecked == true;
        _services.SettingsStore.Save(_services.Settings);

        // Уже лежащие файлы переписываются в фоне; новые записи пойдут в новом формате сразу.
        _ = _services.ChatStore.EnsureFormat();
    }

    private void AutoLockChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        _services.Settings.AutoLockMinutes = int.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
        _services.SettingsStore.Save(_services.Settings);
    }

    private void LockNowButton_Click(object sender, RoutedEventArgs e) => LockRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Карточки режимов — для тестов: сколько их и какие режимы они ставят.</summary>
    internal IEnumerable<string> ModeTags => ModeCards.Children.OfType<RadioButton>().Select(card => (string)card.Tag);

    private void ModeCard_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null ||
            sender is not RadioButton { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: false, out ApprovalMode mode))
        {
            return;
        }

        _services.Settings.ApprovalMode = mode;
        _services.SettingsStore.Save(_services.Settings);
    }

    private void ToolToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null || sender is not CheckBox { Tag: string tool } toggle)
        {
            return;
        }

        var settings = _services.Settings;
        var disabled = settings.DisabledTools ?? [];
        disabled.RemoveAll(name => string.Equals(name, tool, StringComparison.OrdinalIgnoreCase));
        if (toggle.IsChecked != true)
        {
            disabled.Add(tool);
        }

        // Пустой список — null: старый settings.json и новый с выключенным ничем пишутся одинаково.
        settings.DisabledTools = disabled.Count == 0 ? null : disabled;
        _services.SettingsStore.Save(settings);
        EnableAllToolsButton.IsEnabled = settings.DisabledTools is not null;
    }

    private void EnableAllToolsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        _services.Settings.DisabledTools = null;
        _services.SettingsStore.Save(_services.Settings);
        Load(_services.Settings);
    }

    private void EnsureToolRows()
    {
        if (_toolToggles.Count > 0)
        {
            return;
        }

        AddRows(ChatToolRows, ToolCatalog.Chat);
        AddRows(AgentToolRows, ToolCatalog.Agent);
    }

    private void AddRows(Panel host, IReadOnlyList<string> tools)
    {
        for (var i = 0; i < tools.Count; i++)
        {
            if (i > 0)
            {
                var divider = new Border { Height = 1 };
                divider.SetResourceReference(Border.BackgroundProperty, "Bg.Hover");
                host.Children.Add(divider);
            }

            host.Children.Add(BuildRow(tools[i]));
        }
    }

    /// <summary>Строка инструмента: подпись человеческая, под ней — имя, которым его зовёт модель.</summary>
    private Grid BuildRow(string tool)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.TextProperty, ToolCatalog.LabelKey(tool));
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");

        var name = new TextBlock
        {
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
            Margin = new Thickness(0, 2, 0, 0),
            Text = tool
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");

        var captions = new StackPanel { Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        captions.Children.Add(label);
        if (ToolCatalog.Shared.Contains(tool))
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(name);
            var shared = new TextBlock { FontSize = 10.5, Margin = new Thickness(8, 2, 0, 0) };
            shared.SetResourceReference(TextBlock.TextProperty, "S.Security.Shared");
            shared.SetResourceReference(TextBlock.ForegroundProperty, "Text.Dim");
            line.Children.Add(shared);
            captions.Children.Add(line);
        }
        else
        {
            captions.Children.Add(name);
        }

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("SettingsToggle"),
            Tag = tool,
            IsChecked = true
        };
        toggle.Checked += ToolToggle_Changed;
        toggle.Unchecked += ToolToggle_Changed;
        Grid.SetColumn(toggle, 1);
        _toolToggles[tool] = toggle;

        row.Children.Add(captions);
        row.Children.Add(toggle);
        return row;
    }
}
