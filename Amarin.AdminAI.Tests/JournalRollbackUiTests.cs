using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Откат из журнала: у снимка — «Откатить…» со списком того, что вернётся, у точки
/// восстановления Windows и у вызова, её создавшего, — мастер «Восстановление системы».
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class JournalRollbackUiTests
{
    private readonly WpfFixture _wpf;

    public JournalRollbackUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static T Named<T>(MainWindow window, string name) where T : class =>
        (T)window.FindName(name)!;

    private static void Call(MainWindow window, string method, params object[] args) =>
        typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, args);

    private static (Visibility Rollback, Visibility RestoreSystem) Buttons(MainWindow window) =>
        (Named<Button>(window, "JournalRollbackButton").Visibility,
         Named<Button>(window, "JournalRestoreSystemButton").Visibility);

    [Fact]
    public void A_snapshot_offers_a_rollback_and_a_restore_point_offers_the_wizard()
    {
        var (snapshot, point, created, plain) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Call(window, "OpenJournal");
            try
            {
                Call(window, "ShowJournalDetails", JournalView.ToRow(
                    new SnapshotEntry("20260101_120000", new DateTime(2026, 1, 1, 12, 0, 0), "перед правкой", "PC", @"C:\s")));
                var snapshotButtons = Buttons(window);

                Call(window, "ShowJournalDetails", JournalView.ToRow(
                    new WindowsRestorePoint(12, new DateTime(2026, 1, 1), "Установка драйвера", 12)));
                var pointButtons = Buttons(window);

                Call(window, "ShowJournalDetails", JournalView.ToRow(Entry("restore_point", """{"action":"create"}"""), true));
                var createdButtons = Buttons(window);

                Call(window, "ShowJournalDetails", JournalView.ToRow(Entry("restore_point", """{"action":"list"}"""), true));
                return (snapshotButtons, pointButtons, createdButtons, Buttons(window));
            }
            finally
            {
                Call(window, "CloseJournal");
            }
        });

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Visibility.Visible, snapshot.Rollback);
        }

        Assert.Equal(Visibility.Collapsed, snapshot.RestoreSystem);
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), point);
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), created);
        Assert.Equal((Visibility.Collapsed, Visibility.Collapsed), plain);
    }

    [Fact]
    public void The_plan_lists_what_comes_back_and_what_is_left_alone()
    {
        var (visible, lines, applyText, notesTitle, applyVisible) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Call(window, "OpenJournal");
            try
            {
                Call(window, "ShowJournalDetails", JournalView.ToRow(
                    new SnapshotEntry("20260101_120000", new DateTime(2026, 1, 1, 12, 0, 0), "перед правкой", "PC", @"C:\s")));

                var plan = new RollbackPlan { SnapshotId = "20260101_120000" };
                plan.Registry.Add(new RegistryChange(
                    RegistryChangeKind.DeleteValue, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Updater"));
                plan.Tasks.Add(new TaskChange(TaskChangeKind.Enable, @"\Backup"));
                plan.Notes.Add("служба X — часть Windows");
                Call(window, "ShowRollbackPlan", plan);

                var box = Named<Border>(window, "JournalRollbackBox");
                var items = Named<ItemsControl>(window, "JournalRollbackLines");
                return (
                    box.Visibility,
                    items.Items.Count,
                    Named<Button>(window, "JournalRollbackApplyButton").Content as string,
                    Named<TextBlock>(window, "JournalRollbackNotesTitle").Visibility,
                    Named<Button>(window, "JournalRollbackApplyButton").Visibility);
            }
            finally
            {
                Call(window, "CloseJournal");
            }
        });

        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal(2, lines);
        Assert.Equal(Loc.Format("S.Journal.Rollback.Apply", 2), applyText);
        Assert.Equal(Visibility.Visible, notesTitle);
        Assert.Equal(Visibility.Visible, applyVisible);
    }

    [Fact]
    public void An_empty_plan_offers_nothing_to_apply()
    {
        var (title, applyVisible) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            Call(window, "OpenJournal");
            try
            {
                Call(window, "ShowJournalDetails", JournalView.ToRow(
                    new SnapshotEntry("20260101_120000", new DateTime(2026, 1, 1, 12, 0, 0), "", "PC", @"C:\s")));
                Call(window, "ShowRollbackPlan", new RollbackPlan { SnapshotId = "20260101_120000" });
                return (
                    Named<TextBlock>(window, "JournalRollbackTitle").Text,
                    Named<Button>(window, "JournalRollbackApplyButton").Visibility);
            }
            finally
            {
                Call(window, "CloseJournal");
            }
        });

        Assert.Equal(Loc.Get("S.Rollback.Nothing"), title);
        Assert.Equal(Visibility.Collapsed, applyVisible);
    }

    private static JournalEntry Entry(string tool, string args) =>
        new("chat", "Чат", new DateTime(2026, 1, 1, 12, 0, 0), TimeSpan.FromSeconds(1), tool, args, "ok", "ok",
            true, ToolCallStatus.Done, null);
}
