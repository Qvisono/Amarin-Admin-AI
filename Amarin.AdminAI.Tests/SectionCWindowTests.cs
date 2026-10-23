using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Что видно в окне от раздела C 1.37.0: блокировки политики организации на странице
/// «Безопасность», копия в журнал Windows и строка о цепочке журнала аудита.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SectionCWindowTests
{
    private readonly WpfFixture _wpf;

    public SectionCWindowTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static CheckBox ToolToggle(SettingsSecurityPage page, string tool) =>
        Descendants(page).OfType<CheckBox>().Single(box => box.Tag as string == tool);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child))
            {
                yield return deeper;
            }
        }
    }

    [Fact]
    public void The_organization_policy_locks_what_it_sets_and_says_so_once()
    {
        var state = _wpf.Ui.Invoke(() =>
        {
            var page = new SettingsSecurityPage();
            var settings = new AppSettings { ComputerControl = true, AuditToWindowsLog = false };
            page.Load(settings);
            page.ShowPolicy(settings, new OrganizationPolicy
            {
                ReadOnly = true,
                DisabledTools = ["registry"],
                DisableComputerControl = true,
                WindowsEventLog = true
            });

            var locked = (
                Note: ((TextBlock)page.FindName("PolicyNote")).Visibility,
                Mode: (string?)((page.FindName("ModeCombo") as ComboBox)?.SelectedItem as ComboBoxItem)?.Tag,
                ModeEnabled: ((ComboBox)page.FindName("ModeCombo")).IsEnabled,
                Mouse: (((CheckBox)page.FindName("ComputerControlToggle")).IsChecked, ((CheckBox)page.FindName("ComputerControlToggle")).IsEnabled),
                Log: (((CheckBox)page.FindName("WindowsLogToggle")).IsChecked, ((CheckBox)page.FindName("WindowsLogToggle")).IsEnabled),
                Registry: (ToolToggle(page, "registry").IsChecked, ToolToggle(page, "registry").IsEnabled),
                Other: ToolToggle(page, "read_file").IsEnabled);

            page.ShowPolicy(settings, OrganizationPolicy.None);
            var free = (
                Note: ((TextBlock)page.FindName("PolicyNote")).Visibility,
                ModeEnabled: ((ComboBox)page.FindName("ModeCombo")).IsEnabled,
                Mouse: ((CheckBox)page.FindName("ComputerControlToggle")).IsChecked,
                Log: ((CheckBox)page.FindName("WindowsLogToggle")).IsChecked);
            return (locked, free, settings);
        });

        Assert.Equal(Visibility.Visible, state.locked.Note);
        Assert.Equal(nameof(ApprovalMode.ReadOnly), state.locked.Mode);
        Assert.False(state.locked.ModeEnabled);
        Assert.Equal((false, false), state.locked.Mouse);
        Assert.Equal((true, false), state.locked.Log);
        Assert.Equal((false, false), state.locked.Registry);
        Assert.True(state.locked.Other);

        // Политика в настройки не пишется: снятая, она возвращает выбор человека.
        Assert.Equal(Visibility.Collapsed, state.free.Note);
        Assert.True(state.free.ModeEnabled);
        Assert.True(state.free.Mouse);
        Assert.False(state.free.Log);
        Assert.Equal(ApprovalMode.Normal, state.settings.ApprovalMode);
        Assert.True(state.settings.ComputerControl);
        Assert.False(state.settings.AuditToWindowsLog);
    }

    [Fact]
    public void The_audit_tab_says_whether_the_chain_is_intact_or_where_it_breaks()
    {
        var (intact, broken, none) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var show = typeof(MainWindow).GetMethod("ShowAuditChain", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var text = (TextBlock)window.FindName("JournalAuditChainText");

            show.Invoke(window, [new AuditChainReport(true, null, 0)]);
            var ok = (text.Text, text.Visibility);
            show.Invoke(window, [new AuditChainReport(true, "2026-10", 15)]);
            var bad = (text.Text, text.Visibility);
            show.Invoke(window, [new AuditChainReport(false, null, 0)]);
            return (ok, bad, text.Visibility);
        });

        Assert.Equal((Loc.Get("S.Journal.Audit.ChainOk"), Visibility.Visible), intact);
        Assert.Equal((Loc.Format("S.Journal.Audit.ChainBroken", "2026-10", 15), Visibility.Visible), broken);
        Assert.Equal(Visibility.Collapsed, none);
    }
}
