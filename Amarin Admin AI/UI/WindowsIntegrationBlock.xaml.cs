using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Подстраница «Интеграция с Windows» на странице Behavior (G1–G4).
/// </summary>
/// <remarks>
/// Правда — в <see cref="AppSettings.Windows"/>; после каждой правки окно приводит трей и записи
/// в реестре к настройке через <see cref="Applied"/>. Сочетания из любой программы живут рядом с
/// остальными горячими клавишами (<see cref="GlobalHotkeysBlock"/>), а не здесь: искать их
/// будут там.
/// </remarks>
public partial class WindowsIntegrationBlock : UserControl
{
    private AppServices? _services;
    private bool _loading;

    public WindowsIntegrationBlock() => InitializeComponent();

    /// <summary>Привести интеграцию к настройке — ставит окно.</summary>
    internal Action? Applied { get; set; }

    /// <summary>Что-то поменялось — строка-ссылка на странице перечитывает своё значение.</summary>
    internal event Action? Changed;

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var settings = services.Settings.Windows ??= new WindowsIntegrationSettings();
            TrayIconToggle.IsChecked = settings.ShowTrayIcon;
            CloseToTrayToggle.IsChecked = settings.CloseToTray;
            AutoStartToggle.IsChecked = settings.AutoStart;
            ExplorerToggle.IsChecked = settings.ExplorerMenu;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Значение строки-ссылки: что включено, коротко.</summary>
    internal static string Summary(WindowsIntegrationSettings? settings)
    {
        settings ??= new WindowsIntegrationSettings();
        var parts = new List<string>();
        if (settings.ShowTrayIcon)
        {
            parts.Add(Loc.Get("S.Windows.Short.Tray"));
        }

        if (settings.AutoStart)
        {
            parts.Add(Loc.Get("S.Windows.Short.AutoStart"));
        }

        if (settings.ExplorerMenu)
        {
            parts.Add(Loc.Get("S.Windows.Short.Explorer"));
        }

        return parts.Count == 0 ? Loc.Get("S.Common.Off") : string.Join(" · ", parts);
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _services is null)
        {
            return;
        }

        var settings = _services.Settings.Windows ??= new WindowsIntegrationSettings();
        settings.ShowTrayIcon = TrayIconToggle.IsChecked == true;
        settings.CloseToTray = CloseToTrayToggle.IsChecked == true;
        settings.AutoStart = AutoStartToggle.IsChecked == true;
        settings.ExplorerMenu = ExplorerToggle.IsChecked == true;
        _services.SettingsStore.Save(_services.Settings);
        Applied?.Invoke();
        Changed?.Invoke();
    }
}
