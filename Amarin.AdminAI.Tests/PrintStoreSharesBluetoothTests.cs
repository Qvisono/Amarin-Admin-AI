using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Очередь печати, приложения Store, общие папки и Bluetooth: проверки того, что приходит от
/// модели, и сборка скриптов. Принтеры, приложения, сеть и радио тест не трогает.
/// </summary>
public sealed class PrintStoreSharesBluetoothTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "amarin-spool-" + Guid.NewGuid().ToString("N"));

    public PrintStoreSharesBluetoothTests() => Directory.CreateDirectory(Path.Combine(_temp, "Windows", "System32", "spool", "PRINTERS", "keep"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ───────────────────────── печать ─────────────────────────

    [Fact]
    public void The_spool_folder_is_cleaned_only_inside_the_windows_folder()
    {
        var windows = Path.Combine(_temp, "Windows");

        Assert.Equal(PermissionTargets.Canonical(Path.Combine(windows, "System32", "spool", "PRINTERS")),
            PrintQueueCommands.SpoolDirectory(null, windows));
        Assert.Null(PrintQueueCommands.SpoolDirectory(Path.Combine(_temp, "Elsewhere"), windows));
        Assert.Null(PrintQueueCommands.SpoolDirectory(@"relative\spool", windows));
        Assert.Null(PrintQueueCommands.SpoolDirectory(Path.Combine(windows, "..", "Users"), windows));
    }

    [Fact]
    public void Purge_deletes_only_job_files()
    {
        var spool = Path.Combine(_temp, "Windows", "System32", "spool", "PRINTERS");
        foreach (var name in new[] { "00001.SPL", "00001.SHD", "notes.txt" })
        {
            File.WriteAllText(Path.Combine(spool, name), "x");
        }

        File.WriteAllText(Path.Combine(spool, "keep", "00002.SPL"), "x");

        Assert.Equal(2, PrintQueueCommands.Purge(spool));
        Assert.True(File.Exists(Path.Combine(spool, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(spool, "keep", "00002.SPL")));
    }

    [Fact]
    public void Printer_names_are_quoted_in_the_scripts()
    {
        Assert.True(PrintQueueCommands.IsPrinterName(@"\\server\Office printer"));
        Assert.False(PrintQueueCommands.IsPrinterName("bad\nname"));
        Assert.Contains("'Office''s'", PrintQueueCommands.CancelScript("Office's", 3), StringComparison.Ordinal);
        Assert.Contains("-ID 3", PrintQueueCommands.CancelScript("Office", 3), StringComparison.Ordinal);
        Assert.Contains("LegacyDefaultPrinterMode -Value 1", PrintQueueCommands.DefaultScript("Office"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_is_cancelled_only_by_its_id()
    {
        var result = await new PrintQueueTool().ExecuteAsync(Args(new { action = "cancel_job", printer = "Office" }));

        Assert.False(result.Success);
        Assert.Contains("job_id", result.Output, StringComparison.Ordinal);
    }

    // ───────────────────────── Store ─────────────────────────

    [Theory]
    [InlineData("Vendor.App", true)]
    [InlineData("Vendor.App_1.2.3.0_x64__8wekyb3d8bbwe", true)]
    [InlineData("Vendor App", false)]
    [InlineData("Vendor.*", false)]
    [InlineData("Vendor.App'; Remove-Item", false)]
    public void Package_names_are_plain(string name, bool valid) =>
        Assert.Equal(valid, StoreAppsCommands.IsPackageName(name));

    [Fact]
    public void Removal_checks_the_package_inside_the_script()
    {
        var script = StoreAppsCommands.RemoveScript("Vendor.App", forNewUsers: true);

        Assert.Contains("$p.IsFramework -or $p.NonRemovable", script, StringComparison.Ordinal);
        Assert.Contains("SignatureKind -eq 'System'", script, StringComparison.Ordinal);
        Assert.Contains("if ($true)", script, StringComparison.Ordinal);
        Assert.Contains("Remove-AppxProvisionedPackage", script, StringComparison.Ordinal);

        // Без for_new_users предустановленная копия не трогается: её ветка выключена.
        Assert.Contains("if ($false)", StoreAppsCommands.RemoveScript("Vendor.App", forNewUsers: false), StringComparison.Ordinal);
        Assert.True(StoreAppsCommands.IsProtected("Microsoft.WindowsStore_22.0.0.0_x64__8wekyb3d8bbwe"));
        Assert.False(StoreAppsCommands.IsProtected("Vendor.App"));
    }

    // ───────────────────────── общие папки ─────────────────────────

    [Theory]
    [InlineData("Z", true, 'Z')]
    [InlineData("z:", true, 'Z')]
    [InlineData("ZZ", false, '\0')]
    [InlineData("1", false, '\0')]
    public void Drive_letters_are_one_latin_letter(string value, bool valid, char letter)
    {
        Assert.Equal(valid, SharesCommands.TryLetter(value, out var parsed));
        Assert.Equal(letter, parsed);
    }

    [Theory]
    [InlineData(@"\\server\share", true)]
    [InlineData(@"\\server\share\sub", true)]
    [InlineData(@"\\server", false)]
    [InlineData(@"C:\share", false)]
    [InlineData(@"\\server\sh*re", false)]
    [InlineData("\\\\server\\sh\"are", false)]
    public void Network_paths_are_server_and_share_without_wildcards(string value, bool valid) =>
        Assert.Equal(valid, SharesCommands.IsUncPath(value));

    [Fact]
    public void Discovery_is_turned_on_by_the_language_neutral_group_for_private_profiles_only()
    {
        var discovery = SharesCommands.EnableScript(fileSharing: false);
        Assert.Contains(SharesCommands.DiscoveryGroup, discovery, StringComparison.Ordinal);
        Assert.Contains("FDResPub", discovery, StringComparison.Ordinal);
        Assert.Contains("PUBLIC", discovery, StringComparison.Ordinal);

        var sharing = SharesCommands.EnableScript(fileSharing: true);
        Assert.Contains(SharesCommands.FileSharingGroup, sharing, StringComparison.Ordinal);
        Assert.Contains("LanmanServer", sharing, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_rule_shared_with_public_networks_is_not_left_on(bool fileSharing)
    {
        // Встроенные правила бывают на «Private, Public» сразу: прежний скрипт включал их целиком,
        // и «только частные сети» открывали компьютер и в общедоступной.
        var script = SharesCommands.EnableScript(fileSharing);

        Assert.Contains("EnableRuleGroup(3, $group, $true)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Enable-NetFirewallRule", script, StringComparison.Ordinal);
        Assert.Contains("'Public|Any'", script, StringComparison.Ordinal);
        Assert.Contains("Disable-NetFirewallRule", script, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_RULES", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drive_is_mapped_without_any_credentials()
    {
        var script = SharesCommands.MapScript('Z', @"\\server\share", persistent: true);

        Assert.Contains("-Persistent $true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Password", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-UserName", script, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("check_host", "host", "bad host;name")]
    [InlineData("set_private", "adapter", "Wi*Fi")]
    [InlineData("map_drive", "letter", "ZZ")]
    public async Task Bad_arguments_are_refused_before_any_script(string action, string field, string value)
    {
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["action"] = action, [field] = value });

        Assert.False((await new SharesTool().ExecuteAsync(arguments)).Success);
    }

    // ───────────────────────── Bluetooth ─────────────────────────

    [Theory]
    [InlineData("A0:B1:C2:D3:E4:F5", true)]
    [InlineData("a0b1c2d3e4f5", true)]
    [InlineData("A0-B1-C2-D3-E4-F5", true)]
    [InlineData("Bluetooth#Bluetooth00:11:22:33:44:55-66:77:88:99:aa:bb", true)]
    [InlineData("keyboard", false)]
    [InlineData("", false)]
    public void Devices_are_addresses_or_ids(string value, bool valid) =>
        Assert.Equal(valid, BluetoothCommands.IsDevice(value));

    [Fact]
    public void Bluetooth_scripts_wait_for_winrt_and_quote_the_device()
    {
        Assert.Contains("RadioState]::Off", BluetoothCommands.RadioScript(on: false), StringComparison.Ordinal);
        Assert.Contains("RadioState]::On", BluetoothCommands.RadioScript(on: true), StringComparison.Ordinal);
        Assert.Contains("'x''y'", BluetoothCommands.ForgetScript("x'y"), StringComparison.Ordinal);
        Assert.Contains("function Await", BluetoothCommands.DevicesScript, StringComparison.Ordinal);
        Assert.Contains(BluetoothCommands.BatteryProperty, BluetoothCommands.DevicesScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_script_of_the_new_tools_parses_as_powershell()
    {
        // Скрипт с ошибкой разбора выполнился бы только у человека, и отказ пришёл бы текстом
        // PowerShell; здесь его ловит тот же разборщик, что у PowerShellAnalysis.
        string[] scripts =
        [
            PrintQueueCommands.ListScript, PrintQueueCommands.JobsScript(null), PrintQueueCommands.JobsScript("Office"),
            PrintQueueCommands.CancelScript("Office", 1), PrintQueueCommands.ClearScript("Office"), PrintQueueCommands.DefaultScript("Office"),
            StoreAppsCommands.ListScript("Vendor", frameworks: true, 20), StoreAppsCommands.RemoveScript("Vendor.App", forNewUsers: true),
            StoreAppsCommands.ResetScript("Vendor.App"),
            SharesCommands.StatusScript, SharesCommands.SharesScript, SharesCommands.DrivesScript, SharesCommands.SetPrivateScript("Wi-Fi"),
            SharesCommands.EnableScript(fileSharing: false), SharesCommands.EnableScript(fileSharing: true),
            SharesCommands.MapScript('Z', @"\\server\share", persistent: false), SharesCommands.UnmapScript('Z'),
            BluetoothCommands.StatusScript, BluetoothCommands.DevicesScript, BluetoothCommands.RadioScript(on: true),
            BluetoothCommands.ForgetScript("A0:B1:C2:D3:E4:F5")
        ];

        foreach (var script in scripts)
        {
            _ = System.Management.Automation.Language.Parser.ParseInput(script, out _, out var errors);
            Assert.True(errors.Length == 0, string.Join("; ", errors.Select(error => error.Message)) + "\n" + script);
        }
    }

    [Fact]
    public async Task Forget_needs_a_device_from_the_list()
    {
        var result = await new BluetoothTool().ExecuteAsync(Args(new { action = "forget", device = "keyboard" }));

        Assert.False(result.Success);
    }
}
