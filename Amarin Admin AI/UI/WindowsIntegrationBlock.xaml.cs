using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок «Windows» на странице Behavior (G1–G5).
/// </summary>
/// <remarks>
/// Правда — в <see cref="AppSettings.Windows"/>; после каждой правки окно приводит трей,
/// сочетания и записи в реестре к настройке через <see cref="Applied"/>. Сочетания — текстом
/// («Win+Shift+A»): поле записи сочетаний программы клавишу Win не принимает, а глобальным
/// сочетаниям она как раз и нужна.
/// </remarks>
public partial class WindowsIntegrationBlock : UserControl
{
    private AppServices? _services;
    private bool _loading;
    private readonly Dictionary<string, TextBlock> _status = new(StringComparer.Ordinal);

    public WindowsIntegrationBlock() => InitializeComponent();

    /// <summary>Привести интеграцию к настройке — ставит окно. Возвращает, что с сочетаниями.</summary>
    internal Func<IReadOnlyDictionary<string, string>>? Applied { get; set; }

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var settings = services.Settings.Windows ??= new WindowsIntegrationSettings();
            Toggles.Children.Clear();
            AddToggle("S.Windows.TrayIcon", "S.Windows.TrayIconDesc", settings.ShowTrayIcon, (s, on) => s.ShowTrayIcon = on);
            AddToggle("S.Windows.CloseToTray", null, settings.CloseToTray, (s, on) => s.CloseToTray = on);
            AddToggle("S.Windows.MinimizeToTray", null, settings.MinimizeToTray, (s, on) => s.MinimizeToTray = on);
            AddToggle("S.Windows.AutoStart", "S.Windows.AutoStartDesc", settings.AutoStart, (s, on) => s.AutoStart = on);
            AddToggle("S.Windows.Explorer", "S.Windows.ExplorerDesc", settings.ExplorerMenu, (s, on) => s.ExplorerMenu = on);
            (settings.Notifications == NotificationStyle.System ? NotifySystem : NotifyCard).IsChecked = true;

            HotkeyGrid.Children.Clear();
            HotkeyGrid.RowDefinitions.Clear();
            _status.Clear();
            foreach (var action in GlobalHotkeys.All)
            {
                AddHotkeyRow(action, GlobalHotkeys.Effective(settings, action));
            }
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Что Windows ответила на регистрацию сочетаний — под каждым полем.</summary>
    internal void ShowHotkeyProblems(IReadOnlyDictionary<string, string> problems)
    {
        foreach (var (action, label) in _status)
        {
            label.Text = problems.TryGetValue(action, out var problem) ? problem : "";
            label.Visibility = label.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void AddToggle(string titleKey, string? descKey, bool value, Action<WindowsIntegrationSettings, bool> save)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock { Text = Loc.Get(titleKey), Style = (Style)FindResource("SettingTitle") });
        if (descKey is not null)
        {
            text.Children.Add(new TextBlock { Text = Loc.Get(descKey), Style = (Style)FindResource("SettingDesc"), TextWrapping = TextWrapping.Wrap });
        }

        grid.Children.Add(text);
        var toggle = new CheckBox { Style = (Style)FindResource("SettingsToggle"), IsChecked = value, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(toggle, Loc.Get(titleKey));
        RoutedEventHandler changed = (_, _) => Save(settings => save(settings, toggle.IsChecked == true));
        toggle.Checked += changed;
        toggle.Unchecked += changed;
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        Toggles.Children.Add(grid);
    }

    private void AddHotkeyRow(string action, string? gesture)
    {
        var row = HotkeyGrid.RowDefinitions.Count;
        HotkeyGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
        caption.Children.Add(new TextBlock { Text = Loc.Get("S.Windows.Hotkey." + action), Style = (Style)FindResource("SettingTitle") });
        var status = new TextBlock { Style = (Style)FindResource("FieldHint"), Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
        status.SetResourceReference(TextBlock.ForegroundProperty, "Status.Warning");
        caption.Children.Add(status);
        _status[action] = status;
        Grid.SetRow(caption, row);
        HotkeyGrid.Children.Add(caption);

        var box = new TextBox { Text = gesture ?? "", Style = (Style)FindResource("FieldTextBox"), MaxLength = 40 };
        System.Windows.Automation.AutomationProperties.SetName(box, Loc.Get("S.Windows.Hotkey." + action));
        var placeholder = new TextBlock { Style = (Style)FindResource("Placeholder"), Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
        placeholder.SetResourceReference(TextBlock.TextProperty, "S.Windows.HotkeyNone");
        box.TextChanged += (_, _) => placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.LostFocus += (_, _) =>
        {
            var text = box.Text.Trim();
            if (text.Length > 0 && !GlobalHotkeys.TryParse(text, out _, out _))
            {
                status.Text = Loc.Get("S.Windows.HotkeyInvalid");
                status.Visibility = Visibility.Visible;
                return;
            }

            // Пустое поле — «без сочетания», и это явное назначение: заводское не вернётся само.
            Save(settings => settings.Hotkeys[action] = text);
        };

        var frame = new Border { Style = (Style)FindResource("FieldFrame"), Height = 28, Width = 150, Margin = new Thickness(0, 3, 0, 3) };
        var inner = new Grid();
        inner.Children.Add(box);
        inner.Children.Add(placeholder);
        frame.Child = inner;
        Grid.SetRow(frame, row);
        Grid.SetColumn(frame, 1);
        HotkeyGrid.Children.Add(frame);
    }

    private void Notify_Checked(object sender, RoutedEventArgs e) =>
        Save(settings => settings.Notifications = NotifySystem.IsChecked == true ? NotificationStyle.System : NotificationStyle.Card);

    private void Save(Action<WindowsIntegrationSettings> change)
    {
        if (_loading || _services is null)
        {
            return;
        }

        change(_services.Settings.Windows ??= new WindowsIntegrationSettings());
        _services.SettingsStore.Save(_services.Settings);
        if (Applied?.Invoke() is { } problems)
        {
            ShowHotkeyProblems(problems);
        }
    }
}
