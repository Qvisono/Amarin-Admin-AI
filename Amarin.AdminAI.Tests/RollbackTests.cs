using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using Microsoft.Win32;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Откат снимка возвращает только отличия, в обе стороны: изменённое — назад, добавленное после
/// снимка — прочь. Прежде реестр только импортировался из .reg, и добавленное переживало откат.
/// </summary>
public sealed class RollbackTests
{
    private const string Run = @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    private static RegistryValueState Text(string name, string text) =>
        new() { Name = name, Kind = "String", Text = text };

    private static RegistryKeyState Key(string path, params RegistryValueState[] values) =>
        new() { Path = path, Exists = true, Values = values.ToList() };

    [Fact]
    public void A_value_added_after_the_snapshot_is_removed()
    {
        // Ради этого откат и переписан: импорт .reg умеет только дописывать, и новая запись
        // в Run — ровно то, что стоило убрать, — оставалась на месте.
        var before = Key(Run, Text("OneDrive", "onedrive.exe"));
        var now = Key(Run, Text("OneDrive", "onedrive.exe"), Text("Updater", @"C:\evil.exe"));

        var change = Assert.Single(RegistryDiff.Plan(before, now));

        Assert.Equal(RegistryChangeKind.DeleteValue, change.Kind);
        Assert.Equal("Updater", change.ValueName);
    }

    [Fact]
    public void A_changed_or_removed_value_comes_back_as_it_was()
    {
        var before = Key(@"HKCU\SOFTWARE\Vendor", Text("Mode", "safe"), Text("Path", @"C:\app"));
        var now = Key(@"HKCU\SOFTWARE\Vendor", Text("mode", "unsafe"));

        var changes = RegistryDiff.Plan(before, now);

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change => Assert.Equal(RegistryChangeKind.SetValue, change.Kind));
        Assert.Equal("safe", changes.Single(change => change.ValueName == "Mode").Value!.Text);
        Assert.Equal(@"C:\app", changes.Single(change => change.ValueName == "Path").Value!.Text);
    }

    [Fact]
    public void Nothing_changed_means_nothing_to_do()
    {
        var before = Key(Run, Text("A", "a.exe"), new RegistryValueState { Name = "N", Kind = "DWord", Number = 1 });
        var now = Key(Run, Text("a", "a.exe"), new RegistryValueState { Name = "n", Kind = "DWord", Number = 1 });

        Assert.Empty(RegistryDiff.Plan(before, now));
    }

    [Fact]
    public void A_key_that_did_not_exist_is_removed_whole()
    {
        var before = RegistryKeyState.Missing(@"HKCU\SOFTWARE\New");
        var now = Key(@"HKCU\SOFTWARE\New", Text("x", "1"));

        var change = Assert.Single(RegistryDiff.Plan(before, now));

        Assert.Equal(RegistryChangeKind.DeleteKey, change.Kind);
        Assert.Equal(@"HKCU\SOFTWARE\New", change.KeyPath);
    }

    [Fact]
    public void A_deleted_key_comes_back_with_its_values_and_subkeys()
    {
        var before = Key(@"HKCU\SOFTWARE\Gone", Text("a", "1"));
        before.SubKeys.Add(Key(@"HKCU\SOFTWARE\Gone\Child", Text("b", "2")));

        var changes = RegistryDiff.Plan(before, RegistryKeyState.Missing(before.Path));

        Assert.Equal(
            [
                (RegistryChangeKind.CreateKey, @"HKCU\SOFTWARE\Gone", (string?)null),
                (RegistryChangeKind.SetValue, @"HKCU\SOFTWARE\Gone", "a"),
                (RegistryChangeKind.CreateKey, @"HKCU\SOFTWARE\Gone\Child", null),
                (RegistryChangeKind.SetValue, @"HKCU\SOFTWARE\Gone\Child", "b")
            ],
            changes.Select(change => (change.Kind, change.KeyPath, change.ValueName)).ToList());
    }

    [Fact]
    public void An_incomplete_snapshot_does_not_delete_what_it_did_not_see()
    {
        // Раздел упёрся в предел или часть не открылась: «нет в снимке» тогда не значит
        // «добавлено после», и удалять такое нельзя.
        var before = Key(@"HKLM\SOFTWARE\Big", Text("a", "1"));
        before.Truncated = true;
        var now = Key(@"HKLM\SOFTWARE\Big", Text("a", "1"), Text("unseen", "2"));
        now.SubKeys.Add(Key(@"HKLM\SOFTWARE\Big\Unseen"));

        Assert.Empty(RegistryDiff.Plan(before, now));
    }

    [Fact]
    public void A_values_only_snapshot_leaves_subkeys_alone()
    {
        var before = Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion", Text("ProgramFilesDir", @"C:\Program Files"));
        before.Shallow = true;
        var now = Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion", Text("ProgramFilesDir", @"C:\Program Files"));
        now.SubKeys.Add(Key(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));

        Assert.Empty(RegistryDiff.Plan(before, now));
    }

    [Fact]
    public void A_value_of_a_type_net_cannot_write_back_is_left_alone()
    {
        var before = Key(@"HKLM\HARDWARE\X", new RegistryValueState { Name = "r", Kind = "Unknown", Bytes = "AAE=" });
        var now = Key(@"HKLM\HARDWARE\X", new RegistryValueState { Name = "r", Kind = "Unknown", Bytes = "AAI=" });

        Assert.Empty(RegistryDiff.Plan(before, now));
    }

    [Fact]
    public void The_first_capture_of_a_key_wins()
    {
        // Второй снимок того же раздела сделан уже после правки: заменить им первый значило бы
        // откатывать к изменённому.
        var set = new RegistrySnapshotSet();
        var first = Key(@"HKCU\SOFTWARE\Vendor", Text("Mode", "safe"));
        first.Shallow = true;
        Assert.True(set.Add(first));

        var second = Key(@"HKCU\SOFTWARE\Vendor", Text("Mode", "changed"));
        second.Shallow = true;
        Assert.False(set.Add(second));

        Assert.Equal("safe", Assert.Single(set.Roots).Values.Single().Text);
    }

    [Fact]
    public void Capturing_a_whole_key_keeps_the_values_captured_before_it_changed()
    {
        // Сначала запись значения (снимаются значения), потом удаление раздела (снимается
        // поддерево — уже после записи). Значения обязаны остаться прежними.
        var set = new RegistrySnapshotSet();
        var values = Key(@"HKCU\SOFTWARE\Vendor", Text("Mode", "safe"));
        values.Shallow = true;
        set.Add(values);

        var tree = Key(@"HKCU\SOFTWARE\Vendor", Text("Mode", "written"));
        tree.SubKeys.Add(Key(@"HKCU\SOFTWARE\Vendor\Child", Text("c", "1")));
        Assert.True(set.Add(tree));

        var root = Assert.Single(set.Roots);
        Assert.False(root.Shallow);
        Assert.Equal("safe", root.Values.Single().Text);
        Assert.Single(root.SubKeys);
    }

    [Fact]
    public void A_parent_captured_later_takes_in_the_child_captured_earlier()
    {
        var set = new RegistrySnapshotSet();
        var child = Key(@"HKCU\SOFTWARE\Vendor\App", Text("v", "original"));
        child.Shallow = true;
        set.Add(child);

        var parent = Key(@"HKCU\SOFTWARE\Vendor");
        parent.SubKeys.Add(Key(@"HKCU\SOFTWARE\Vendor\App", Text("v", "modified")));
        set.Add(parent);

        var root = Assert.Single(set.Roots);
        Assert.Equal(@"HKCU\SOFTWARE\Vendor", root.Path);
        Assert.Equal("original", root.Find(@"HKCU\SOFTWARE\Vendor\App")!.Values.Single().Text);
    }

    [Fact]
    public void A_key_that_did_not_exist_covers_everything_under_it()
    {
        var set = new RegistrySnapshotSet();
        set.Add(RegistryKeyState.Missing(@"HKCU\SOFTWARE\New"));

        Assert.True(set.Covers(@"HKCU\SOFTWARE\New\Deep\Deeper", deep: true));
        Assert.False(set.Add(Key(@"HKCU\SOFTWARE\New\Deep", Text("x", "1"))));
    }

    [Fact]
    public void A_deep_capture_covers_its_descendants()
    {
        var set = new RegistrySnapshotSet();
        var tree = Key(@"HKCU\SOFTWARE\Vendor");
        tree.SubKeys.Add(Key(@"HKCU\SOFTWARE\Vendor\App"));
        set.Add(tree);

        Assert.True(set.Covers(@"HKCU\SOFTWARE\Vendor\App", deep: true));
        Assert.True(set.Covers(@"HKCU\SOFTWARE\Vendor\NotThereThen", deep: true));
        Assert.False(set.Covers(@"HKCU\SOFTWARE\Other", deep: false));
    }

    [Fact]
    public void Service_and_driver_keys_that_appeared_later_are_not_deleted()
    {
        // Раздел службы, появившийся после снимка, — чаще драйвер от Windows Update, чем след
        // запроса, а удалить его значит не загрузиться.
        var notes = new List<string>();
        var kept = RollbackRules.FilterRegistry(
            [
                new RegistryChange(RegistryChangeKind.DeleteKey, @"HKLM\SYSTEM\CurrentControlSet\Services\NewDriver"),
                new RegistryChange(RegistryChangeKind.DeleteKey, @"HKLM\SOFTWARE\Vendor\Added")
            ],
            notes);

        Assert.Equal(@"HKLM\SOFTWARE\Vendor\Added", Assert.Single(kept).KeyPath);
        Assert.Contains("NewDriver", Assert.Single(notes), StringComparison.Ordinal);
    }

    private static ServiceSnapshotEntry Service(string name, string status, string startType) =>
        new() { Name = name, Status = status, StartType = startType };

    [Fact]
    public void A_service_that_stopped_on_its_own_is_left_alone()
    {
        // Службы «по требованию» запускаются и гаснут сами. Прежний откат возвращал состояние
        // всем трёмстам и гасил то, что Windows только что запустила по делу.
        var notes = new List<string>();
        var changes = RollbackRules.PlanServices(
            [Service("BITS", "Running", "Manual"), Service("TrustedInstaller", "Stopped", "Manual")],
            [Service("BITS", "Stopped", "Manual"), Service("TrustedInstaller", "Running", "Manual")],
            new HashSet<string>(),
            notes);

        Assert.Empty(changes);
        Assert.Contains("2", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_changed_startup_type_is_restored()
    {
        var changes = RollbackRules.PlanServices(
            [Service("wuauserv", "Stopped", "Manual")],
            [Service("wuauserv", "Stopped", "Disabled")],
            new HashSet<string>(),
            []);

        var change = Assert.Single(changes);
        Assert.Equal("Disabled", change.StartTypeFrom);
        Assert.Equal("Manual", change.StartTypeTo);
        Assert.Equal(ServiceRunAction.None, change.Run);
    }

    [Fact]
    public void A_service_this_request_stopped_is_started_again()
    {
        var changes = RollbackRules.PlanServices(
            [Service("Spooler", "Running", "Manual")],
            [Service("Spooler", "Stopped", "Manual")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spooler" },
            []);

        Assert.Equal(ServiceRunAction.Start, Assert.Single(changes).Run);
    }

    [Fact]
    public void An_automatic_service_found_stopped_is_started()
    {
        var changes = RollbackRules.PlanServices(
            [Service("Audiosrv", "Running", "Automatic")],
            [Service("Audiosrv", "Stopped", "Automatic")],
            new HashSet<string>(),
            []);

        Assert.Equal(ServiceRunAction.Start, Assert.Single(changes).Run);
    }

    [Fact]
    public void Rollback_never_stops_a_protected_service()
    {
        var notes = new List<string>();
        var changes = RollbackRules.PlanServices(
            [Service("WinDefend", "Stopped", "Manual")],
            [Service("WinDefend", "Running", "Manual")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WinDefend" },
            notes);

        Assert.Empty(changes);
        Assert.Contains("WinDefend", Assert.Single(notes), StringComparison.Ordinal);
    }

    private static ScheduledTaskSnapshotEntry Task(string path, string name, string state) =>
        new() { TaskPath = path, TaskName = name, State = state };

    [Fact]
    public void A_task_that_is_merely_running_is_not_a_change()
    {
        // Ready и Running — одна включённая задача в разные минуты. Прежний откат считал это
        // изменением и «включал» её заново.
        var changes = RollbackRules.PlanTasks(
            [Task(@"\Microsoft\Windows\Defrag\", "ScheduledDefrag", "Ready")],
            [Task(@"\Microsoft\Windows\Defrag\", "ScheduledDefrag", "Running")],
            new HashSet<string>(),
            new Dictionary<string, string>(),
            []);

        Assert.Empty(changes);
    }

    [Fact]
    public void A_disabled_task_is_enabled_again()
    {
        var change = Assert.Single(RollbackRules.PlanTasks(
            [Task(@"\", "Backup", "Ready")],
            [Task(@"\", "Backup", "Disabled")],
            new HashSet<string>(),
            new Dictionary<string, string>(),
            []));

        Assert.Equal(TaskChangeKind.Enable, change.Kind);
        Assert.Equal(@"\Backup", change.FullName);
    }

    [Fact]
    public void Only_tasks_this_request_created_are_deleted()
    {
        var notes = new List<string>();
        var changes = RollbackRules.PlanTasks(
            [],
            [Task(@"\", "Ours", "Ready"), Task(@"\Vendor\", "Updater", "Ready")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\ours" },
            new Dictionary<string, string>(),
            notes);

        var change = Assert.Single(changes);
        Assert.Equal(TaskChangeKind.Delete, change.Kind);
        Assert.Equal(@"\Ours", change.FullName);
        Assert.Single(notes);
    }

    [Fact]
    public void A_deleted_task_comes_back_only_from_a_saved_copy()
    {
        var notes = new List<string>();
        var changes = RollbackRules.PlanTasks(
            [Task(@"\", "WithCopy", "Ready"), Task(@"\", "NoCopy", "Ready")],
            [],
            new HashSet<string>(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"\WithCopy"] = "a.xml" },
            notes);

        var change = Assert.Single(changes);
        Assert.Equal(TaskChangeKind.Recreate, change.Kind);
        Assert.Equal("a.xml", change.XmlFile);
        Assert.Contains("NoCopy", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_overwritten_by_create_is_restored_from_its_copy()
    {
        var change = Assert.Single(RollbackRules.PlanTasks(
            [Task(@"\", "Report", "Ready")],
            [Task(@"\", "Report", "Ready")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\Report" },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [@"\Report"] = "r.xml" },
            []));

        Assert.Equal(TaskChangeKind.Recreate, change.Kind);
    }

    [Theory]
    [InlineData("MyTask", @"\MyTask")]
    [InlineData(@"\MyTask", @"\MyTask")]
    [InlineData(@"\Folder\MyTask", @"\Folder\MyTask")]
    [InlineData(@"Folder\MyTask", @"\Folder\MyTask")]
    public void Task_names_are_spelled_one_way(string given, string expected)
    {
        Assert.Equal(expected, RollbackRules.TaskFullName(given));
    }

    [Fact]
    public void A_task_state_sent_as_a_number_is_read()
    {
        // ConvertTo-Json в Windows PowerShell 5.1 пишет перечисления числами; строковое поле на
        // числе роняло разбор, и снимок задач выходил пустым.
        var tasks = JsonSerializer.Deserialize<List<ScheduledTaskSnapshotEntry>>(
            """[{"TaskName":"A","TaskPath":"\\","State":1},{"TaskName":"B","TaskPath":"\\","State":"Ready"}]""")!;

        Assert.Equal(["Disabled", "Ready"], tasks.Select(task => task.State).ToList());
        Assert.False(RollbackRules.IsEnabled(tasks[0].State));
    }

    [Fact]
    public void A_run_key_change_reads_as_startup()
    {
        var line = RollbackText.Describe(
            new RegistryChange(RegistryChangeKind.DeleteValue, Run, "Updater", Current: Text("Updater", "x.exe")));

        Assert.StartsWith(StringsRu.Values["S.Rollback.Startup.Remove"].Split('«')[0], line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE\Vendor", "HKLM", @"SOFTWARE\Vendor")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor\", "HKLM", @"SOFTWARE\Vendor")]
    [InlineData(@"HKCU:\Software\Vendor", "HKCU", @"Software\Vendor")]
    [InlineData(@"Registry::HKEY_CURRENT_USER\Software\Vendor", "HKCU", @"Software\Vendor")]
    [InlineData(@"HKCR\*\shell", "HKCR", @"*\shell")]
    [InlineData(@"HKCR\MIME\Database\Content Type\text/html", "HKCR", @"MIME\Database\Content Type\text/html")]
    public void Registry_paths_are_read_in_every_usual_spelling(string input, string root, string subKey)
    {
        Assert.True(RegistryPath.TryParse(input, out var path));
        Assert.Equal(root, path.Root);
        Assert.Equal(subKey, path.SubKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HKLM")]
    [InlineData(@"HKXX\SOFTWARE")]
    [InlineData(@"HKLM\SOFTWARE\..\SAM")]
    [InlineData("HKLM\\SOFTWARE\\a\nb")]
    [InlineData(@"C:\Windows")]
    public void Garbage_is_not_a_registry_path(string input)
    {
        Assert.False(RegistryPath.TryParse(input, out _));
    }

    [Fact]
    public void Every_change_in_a_request_extends_the_one_snapshot()
    {
        // Снимок один на запрос, и раньше вторая правка — в другой раздел — в него не попадала.
        var taken = 0;
        var extended = new List<(string Id, string Tool, string Path)>();
        var tracker = new SessionUndoTracker
        {
            TakeSnapshot = _ =>
            {
                taken++;
                return new SnapshotResult(true, "snap-1", "ok");
            },
            ExtendSnapshot = (id, tool, args) => extended.Add((id, tool, args.GetProperty("path").GetString()!))
        };
        tracker.BeginRequest("правка");

        tracker.EnsureSnapshotBeforeMutation("registry", Args(@"HKCU\Software\A"));
        tracker.EnsureSnapshotBeforeMutation("registry", Args(@"HKCU\Software\B"));

        Assert.Equal(1, taken);
        Assert.Equal(
            [("snap-1", "registry", @"HKCU\Software\A"), ("snap-1", "registry", @"HKCU\Software\B")],
            extended);

        static JsonElement Args(string path) =>
            JsonSerializer.SerializeToElement(new { action = "write", path, value_name = "v" });
    }

    [Fact]
    public void A_created_task_is_recorded_in_the_snapshot()
    {
        var recorded = new List<string>();
        var tracker = new SessionUndoTracker
        {
            TakeSnapshot = _ => new SnapshotResult(true, "snap-1", "ok"),
            ExtendSnapshot = (_, _, _) => { },
            RecordInSnapshot = (id, tool, _) => recorded.Add(id + ":" + tool)
        };
        tracker.BeginRequest("задача");
        var args = JsonSerializer.SerializeToElement(new { action = "create", task_name = "Report" });

        tracker.EnsureSnapshotBeforeMutation("scheduled_task", args);
        tracker.RecordMutation("scheduled_task", args);

        Assert.Equal(["snap-1:scheduled_task"], recorded);
    }

    [Fact]
    public void A_live_registry_key_rolls_back_both_ways()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Настоящий реестр, но свой временный раздел в HKCU — и убирается в finally.
        var sub = @"Software\AmarinAdminAI.Tests\" + Guid.NewGuid().ToString("N");
        var canonical = @"HKCU\" + sub;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(sub))
            {
                key.SetValue("Keep", "original");
                key.SetValue("Number", 7, RegistryValueKind.DWord);
                key.SetValue("Path", "%SystemRoot%\\x", RegistryValueKind.ExpandString);
                key.SetValue("Lines", new[] { "a", "b" }, RegistryValueKind.MultiString);
                key.SetValue("Blob", new byte[] { 1, 2, 3 }, RegistryValueKind.Binary);
                key.CreateSubKey("Child")!.SetValue("c", "1");
            }

            Assert.True(RegistryPath.TryParse(canonical, out var path));
            var before = RegistryStateIo.Capture(path, deep: true);

            using (var key = Registry.CurrentUser.OpenSubKey(sub, writable: true)!)
            {
                key.SetValue("Keep", "changed");
                key.DeleteValue("Number");
                key.SetValue("Added", "evil.exe");
                key.DeleteSubKeyTree("Child");
                key.CreateSubKey("AddedChild")!.Dispose();
            }

            var outcomes = RegistryStateIo.Apply(RegistryDiff.Plan(before, RegistryStateIo.CaptureLike(before)));

            Assert.All(outcomes, outcome => Assert.True(outcome.Success, outcome.Line + " " + outcome.Error));
            Assert.Empty(RegistryDiff.Plan(before, RegistryStateIo.CaptureLike(before)));
            using var restored = Registry.CurrentUser.OpenSubKey(sub)!;
            Assert.Equal("original", restored.GetValue("Keep"));
            Assert.Equal(7, restored.GetValue("Number"));
            Assert.Null(restored.GetValue("Added"));
            Assert.Equal(
                "%SystemRoot%\\x",
                restored.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
            Assert.Equal(["Child"], restored.GetSubKeyNames());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(sub, throwOnMissingSubKey: false);
        }
    }
}
