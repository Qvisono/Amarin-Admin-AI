using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Страница «Безопасность»: режим доступа, SynGuard, план агента, шифрование, выключатели
/// инструментов и разрешённые источники загрузки.
/// </summary>
/// <remarks>
/// Отдельным <see cref="UserControl"/>, как Key, Info и Instructions: разметка главного окна и так
/// на три тысячи строк. Режим доступа — выпадающим списком, как было в «Поведении» 1.27.1, а цена
/// ошибки выбранного режима — строкой под ним: четыре карточки с абзацем каждая занимали весь
/// первый экран страницы.
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

        MainWindow.EnablePageScroll(PageScroll);
    }

    /// <summary>Ставится главным окном, когда службы уже собраны.</summary>
    internal void Attach(AppServices services) => _services = services;

    /// <summary>«+» у разрешённых источников: диалог домена — у главного окна.</summary>
    internal event EventHandler? AddDomainRequested;

    /// <summary>«–» у строки источника — домен этой строки.</summary>
    internal event EventHandler<string>? RemoveDomainRequested;

    /// <summary>
    /// Показывает разрешённые источники и их число справа у строки «Источники загрузки ›».
    /// Список ведёт главное окно: его же дополняет вопрос «разрешить домен?» из чата.
    /// </summary>
    internal void ShowDomains(IReadOnlyList<string> domains)
    {
        AllowedDomainsList.ItemsSource = null;
        AllowedDomainsList.ItemsSource = domains.ToList();
        AllowedDomainsEmpty.Visibility = domains.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SourcesLinkRow.Tag = Loc.Format("S.Security.SourcesCount", domains.Count);
    }

    private void AddDomainButton_Click(object sender, RoutedEventArgs e) => AddDomainRequested?.Invoke(this, EventArgs.Empty);

    private void RemoveDomainButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string domain })
        {
            RemoveDomainRequested?.Invoke(this, domain);
        }
    }

    /// <summary>Наполняет страницу из настроек. Зовётся из <c>LoadSettingsUi</c>.</summary>
    internal void Load(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        EnsureToolRows();

        _loading = true;
        try
        {
            ModeCombo.SelectedItem = ModeItems.FirstOrDefault(item =>
                string.Equals((string)item.Tag, settings.ApprovalMode.ToString(), StringComparison.Ordinal));
            ShowModeDescription(settings.ApprovalMode);

            foreach (var (tool, toggle) in _toolToggles)
            {
                toggle.IsChecked = !ToolGate.IsDisabled(settings, tool);
            }

            ShowToolsSummary(settings);

            foreach (var toggle in PlanToggles)
            {
                toggle.IsChecked = AgentPlanSettings.IsOn(settings, (string)toggle.Tag);
            }

            EncryptChatsToggle.IsChecked = settings.EncryptChats;
        }
        finally
        {
            _loading = false;
        }
    }

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
        // Индекс поиска — тоже текст переписок: переписывается в новом формате сразу, а не при
        // следующей правке, иначе открытая копия осталась бы лежать рядом с зашифрованными.
        _services.TextIndex.SaveNow();
    }

    private IEnumerable<CheckBox> PlanToggles => [PlanLiteToggle, PlanFastToggle, PlanHeavyToggle];

    private void PlanTierToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null || sender is not CheckBox { Tag: string tier } toggle)
        {
            return;
        }

        AgentPlanSettings.Set(_services.Settings, tier, toggle.IsChecked == true);
        _services.SettingsStore.Save(_services.Settings);
    }

    private IEnumerable<ComboBoxItem> ModeItems => ModeCombo.Items.OfType<ComboBoxItem>();

    /// <summary>Режимы списка — для тестов: сколько их и какие режимы они ставят.</summary>
    internal IEnumerable<string> ModeTags => ModeItems.Select(item => (string)item.Tag);

    /// <summary>Выбирает режим так же, как человек в списке, — для тестов.</summary>
    internal void SelectMode(ApprovalMode mode) =>
        ModeCombo.SelectedItem = ModeItems.First(item => (string)item.Tag == mode.ToString());

    /// <summary>Режим, выбранный в списке сейчас.</summary>
    internal ApprovalMode? ShownMode =>
        ModeCombo.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse(tag, out ApprovalMode mode) ? mode : null;

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeDesc is null || ShownMode is not { } mode)
        {
            // SelectionChanged приходит из InitializeComponent, до полей.
            return;
        }

        ShowModeDescription(mode);
        if (_loading || _services is null)
        {
            return;
        }

        _services.Settings.ApprovalMode = mode;
        _services.SettingsStore.Save(_services.Settings);
    }

    /// <summary>Одна строка о выбранном режиме; у «Подтверждать всё» — цветом предупреждения.</summary>
    private void ShowModeDescription(ApprovalMode mode)
    {
        ModeDesc.SetResourceReference(TextBlock.TextProperty, "S.Access." + mode + "Desc");
        ModeDesc.SetResourceReference(TextBlock.ForegroundProperty, mode == ApprovalMode.AlwaysApprove ? "Status.Warning" : "Text.Dim");
    }

    /// <summary>Значение строки «Инструменты ›»: все ли включены, а если нет — сколько выключено.</summary>
    private void ShowToolsSummary(AppSettings settings)
    {
        var off = _toolToggles.Keys.Count(tool => ToolGate.IsDisabled(settings, tool));
        ToolsLinkRow.Tag = off == 0 ? Loc.Get("S.Security.ToolsAllOn") : Loc.Format("S.Security.ToolsOff", off);
        EnableAllToolsButton.IsEnabled = settings.DisabledTools is { Count: > 0 };
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
        ShowToolsSummary(settings);
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
            // Строки лежат в группе-карточке: черта — её, без полей, а воздух — у самих строк,
            // иначе первая и последняя строка прижимались бы к краю карточки.
            if (i > 0)
            {
                host.Children.Add(new Border { Style = (Style)FindResource("GroupDivider") });
            }

            var row = BuildRow(tools[i]);
            row.Margin = new Thickness(0, 9, 0, 9);
            host.Children.Add(row);
        }
    }

    /// <summary>Строка инструмента: подпись человеческая, под ней — имя, которым его зовёт модель.</summary>
    private Grid BuildRow(string tool)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Style = (Style)FindResource("SettingTitle"), TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.TextProperty, ToolCatalog.LabelKey(tool));

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
        toggle.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, ToolCatalog.LabelKey(tool));
        toggle.Checked += ToolToggle_Changed;
        toggle.Unchecked += ToolToggle_Changed;
        Grid.SetColumn(toggle, 1);
        _toolToggles[tool] = toggle;

        row.Children.Add(captions);
        row.Children.Add(toggle);
        return row;
    }
}
