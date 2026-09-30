using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Microsoft.Win32;

namespace Amarin.UI;

/// <summary>
/// Страница «Подключения»: удалённые машины (C10). Список, редактор и «Проверить связь».
/// </summary>
/// <remarks>
/// Пароль в редактор не возвращается никогда: поле пустое, а пустое при сохранении значит
/// «оставить прежний». Так расшифрованный пароль не лежит в контроле дольше, чем его набирают.
/// </remarks>
public partial class SettingsConnectionsPage : UserControl
{
    private AppServices? _services;
    private RemoteMachine? _editing;
    private readonly Dictionary<string, string> _probes = [];

    public SettingsConnectionsPage()
    {
        InitializeComponent();
    }

    /// <summary>Список машин изменился — выбор цели в чате перечитывает его.</summary>
    internal event Action? MachinesChanged;

    internal void Attach(AppServices services) => _services = services;

    internal void Load()
    {
        EditorPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Refresh()
    {
        if (_services is null)
        {
            return;
        }

        var machines = _services.Machines.Load();
        MachineItems.ItemsSource = machines.Select(machine => new MachineRow(machine, _probes.GetValueOrDefault(machine.Id))).ToList();
        EmptyState.Visibility = machines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Вкладка одна; вторая (MCP) заводится своим пунктом.
    }

    private RemoteMachine? MachineOf(object sender) =>
        sender is FrameworkElement { Tag: string id } ? _services?.Machines.Find(id) : null;

    private void Card_Click(object sender, RoutedEventArgs e)
    {
        if (MachineOf(sender) is { } machine)
        {
            OpenEditor(machine);
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e) => OpenEditor(new RemoteMachine());

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (MachineOf(sender) is not { } machine || sender is not Button button)
        {
            return;
        }

        button.IsEnabled = false;
        _probes[machine.Id] = Loc.Get("S.Machines.Testing");
        Refresh();
        _probes[machine.Id] = await TestAsync(machine, CancellationToken.None);
        Refresh();
    }

    /// <summary>
    /// Проверка связи: на той стороне исполняется команда и отвечает своим именем. Ответ —
    /// строкой для карточки: «связь есть: SRV-01» или первая строка ошибки.
    /// </summary>
    internal static async Task<string> TestAsync(RemoteMachine machine, CancellationToken cancellationToken)
    {
        ToolResult result;
        using (ExecutionTarget.Push(machine))
        {
            result = await PowerShellHelper.RunAsync(RemoteScript.ProbeScript, 45, cancellationToken).ConfigureAwait(true);
        }

        var stdout = PowerShellHelper.ExtractStdout(result.Output).Trim();
        return result.Success && stdout.Length > 0
            ? Loc.Format("S.Machines.TestOk", FirstLine(stdout))
            : Loc.Format("S.Machines.TestFailed", FirstLine(result.Output));
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(part => !part.StartsWith("Exit code", StringComparison.Ordinal) && !part.StartsWith("---", StringComparison.Ordinal))
            ?? text.Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    // ───────────────────────── редактор ─────────────────────────

    private void OpenEditor(RemoteMachine machine)
    {
        _editing = machine;
        EditorTitle.SetResourceReference(TextBlock.TextProperty,
            string.IsNullOrEmpty(machine.Id) ? "S.Machines.NewTitle" : "S.Machines.EditTitle");
        NameBox.Text = machine.Name;
        AddressBox.Text = machine.Address;
        UserBox.Text = machine.User;
        PasswordBox.Password = "";
        PasswordHint.SetResourceReference(TextBlock.TextProperty,
            machine.ProtectedPassword is null ? "S.Machines.PasswordHint" : "S.Machines.PasswordKept");
        KeyBox.Text = machine.KeyFile ?? "";
        MethodWinRm.IsChecked = machine.Method == RemoteMethod.WinRm;
        MethodSsh.IsChecked = machine.Method == RemoteMethod.Ssh;
        ApplyMethod();
        DeleteButton.Visibility = string.IsNullOrEmpty(machine.Id) ? Visibility.Collapsed : Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Collapsed;
        EditorPane.Visibility = Visibility.Visible;
        NameBox.Focus();
    }

    private RemoteMethod SelectedMethod => MethodSsh.IsChecked == true ? RemoteMethod.Ssh : RemoteMethod.WinRm;

    private void Method_Checked(object sender, RoutedEventArgs e) => ApplyMethod();

    private void ApplyMethod()
    {
        if (PasswordRow is null)
        {
            return;
        }

        var ssh = SelectedMethod == RemoteMethod.Ssh;
        PasswordRow.Visibility = ssh ? Visibility.Collapsed : Visibility.Visible;
        KeyRow.Visibility = ssh ? Visibility.Visible : Visibility.Collapsed;
        MethodHint.SetResourceReference(TextBlock.TextProperty,
            ssh
                ? RemoteScript.FindPwsh() is null ? "S.Machines.SshNoPwsh" : "S.Machines.SshHint"
                : "S.Machines.WinRmHint");
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            KeyBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _editing is not { } machine)
        {
            return;
        }

        var draft = new RemoteMachine
        {
            Id = machine.Id,
            Name = NameBox.Text.Trim(),
            Address = AddressBox.Text.Trim(),
            User = UserBox.Text.Trim(),
            Method = SelectedMethod,
            KeyFile = string.IsNullOrWhiteSpace(KeyBox.Text) ? null : KeyBox.Text.Trim(),
            ProtectedPassword = machine.ProtectedPassword
        };

        if (MachineBook.Validate(draft) is { } problem)
        {
            ShowError(problem);
            return;
        }

        if (draft.Method == RemoteMethod.WinRm && PasswordBox.Password.Length > 0)
        {
            draft.ProtectedPassword = DataProtector.Protect(PasswordBox.Password);
            if (draft.ProtectedPassword is null)
            {
                ShowError(Loc.Get("S.Machines.ProtectFailed"));
                return;
            }
        }

        if (draft.Method == RemoteMethod.WinRm && draft.ProtectedPassword is null)
        {
            ShowError(Loc.Get("S.Machines.NeedPassword"));
            return;
        }

        PasswordBox.Password = "";
        var machines = _services.Machines.Load();
        if (string.IsNullOrEmpty(draft.Id))
        {
            draft.Id = Guid.NewGuid().ToString("N")[..12];
            machines.Add(draft);
        }
        else
        {
            var index = machines.FindIndex(item => item.Id == draft.Id);
            if (index >= 0)
            {
                machines[index] = draft;
            }
            else
            {
                machines.Add(draft);
            }
        }

        if (!_services.Machines.Save(machines))
        {
            ShowError(Loc.Get("S.Machines.SaveFailed"));
            return;
        }

        _probes.Remove(draft.Id);
        MachinesChanged?.Invoke();
        Load();
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_services is null || _editing is not { Id.Length: > 0 } machine || Window.GetWindow(this) is not MainWindow window)
        {
            return;
        }

        var confirmed = await window.ShowNoticeAsync(
            Loc.Get("S.Machines.DeleteTitle"),
            Loc.Format("S.Machines.DeleteText", machine.Name),
            Loc.Get("S.Common.Delete"),
            Loc.Get("S.Common.Cancel"),
            NoticeTone.Danger);
        if (!confirmed)
        {
            return;
        }

        var machines = _services.Machines.Load();
        machines.RemoveAll(item => item.Id == machine.Id);
        _services.Machines.Save(machines);
        MachinesChanged?.Invoke();
        Load();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        PasswordBox.Password = "";
        Load();
    }
}

/// <summary>Карточка машины.</summary>
internal sealed class MachineRow(RemoteMachine machine, string? probe)
{
    public string Id { get; } = machine.Id;

    public string Name { get; } = machine.Name;

    public string Detail { get; } = $"{machine.User}@{machine.Address} · {(machine.Method == RemoteMethod.Ssh ? "SSH" : "WinRM")}";

    public string Probe { get; } = probe ?? "";

    public Visibility ProbeVisibility => string.IsNullOrEmpty(Probe) ? Visibility.Collapsed : Visibility.Visible;
}
