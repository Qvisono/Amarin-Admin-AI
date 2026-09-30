using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Блок «Резервные копии» на странице данных (F1).
/// </summary>
/// <remarks>
/// Как у страниц настроек: правда в <see cref="AppSettings.Backup"/>, контролы только отражают её
/// и пишут изменения под <c>_loading</c>. Саму копию делает окно (<c>MainWindow.Backup</c>) —
/// блок лишь просит её через <see cref="RunNow"/>.
/// </remarks>
public partial class BackupBlock : UserControl
{
    private AppServices? _services;
    private bool _loading;

    public BackupBlock() => InitializeComponent();

    /// <summary>Сделать копию сейчас. Ставит окно.</summary>
    internal Func<Task>? RunNow { get; set; }

    internal void Load(AppServices services)
    {
        _services = services;
        _loading = true;
        try
        {
            var backup = services.Settings.Backup ?? new BackupSettings();
            EnabledToggle.IsChecked = backup.Enabled;
            (backup.Interval == BackupInterval.Weekly ? WeeklyChip : DailyChip).IsChecked = true;
            FolderBox.Text = Backups.FolderOf(backup);
            KeepBox.Text = backup.Keep.ToString(CultureInfo.InvariantCulture);
            Details.IsEnabled = backup.Enabled;
            PlainWarning.Visibility = services.Settings.EncryptChats ? Visibility.Visible : Visibility.Collapsed;
            ShowStatus(backup, running: false);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Строка под настройками: последняя копия, ошибка или «идёт».</summary>
    internal void ShowStatus(BackupSettings backup, bool running)
    {
        RunNowButton.IsEnabled = !running;
        if (running)
        {
            StatusText.Text = Loc.Get("S.Backup.Running");
            return;
        }

        if (backup.LastErrorAt is { } failed && (backup.LastAt is null || failed > backup.LastAt))
        {
            StatusText.Text = Loc.Format("S.Backup.Failed", failed.ToString("g", CultureInfo.CurrentCulture), backup.LastError ?? "");
            return;
        }

        StatusText.Text = backup.LastAt is { } last
            ? Loc.Format("S.Backup.Last", last.ToString("g", CultureInfo.CurrentCulture), AttachmentTypes.FormatSize(backup.LastBytes))
            : Loc.Get("S.Backup.Never");
    }

    private void Save(Action<BackupSettings> change)
    {
        if (_loading || _services is null)
        {
            return;
        }

        var backup = _services.Settings.Backup ??= new BackupSettings();
        change(backup);
        _services.SettingsStore.Save(_services.Settings);
    }

    private void EnabledToggle_Changed(object sender, RoutedEventArgs e)
    {
        Details.IsEnabled = EnabledToggle.IsChecked == true;
        Save(backup => backup.Enabled = EnabledToggle.IsChecked == true);
    }

    private void Interval_Checked(object sender, RoutedEventArgs e) =>
        Save(backup => backup.Interval = WeeklyChip.IsChecked == true ? BackupInterval.Weekly : BackupInterval.Daily);

    private void KeepBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var keep = int.TryParse(KeepBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 1 and <= 365
            ? value
            : _services?.Settings.Backup?.Keep ?? 7;
        KeepBox.Text = keep.ToString(CultureInfo.InvariantCulture);
        Save(backup => backup.Keep = keep);
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        // Папку данных программы не берём: копия должна пережить «Удалить все данные».
        var chosen = Path.GetFullPath(dialog.FolderName);
        if (chosen.StartsWith(Path.GetFullPath(AppPaths.Root), StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = Loc.Get("S.Backup.InsideData");
            return;
        }

        FolderBox.Text = chosen;
        Save(backup => backup.Folder = chosen);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(FolderBox.Text);
            Process.Start(new ProcessStartInfo { FileName = FolderBox.Text, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = Loc.Format("S.Backup.FolderFailed", ex.Message);
        }
    }

    private void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (RunNow is { } run)
        {
            Detached.Run(run(), "backup_now");
        }
    }
}
