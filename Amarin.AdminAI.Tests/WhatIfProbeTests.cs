using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Пробный прогон с -WhatIf перед подтверждением (C2). Главное обещание — он ничего не меняет:
/// скрипт, где запись делает не командлет, пробовать нельзя вовсе.
/// </summary>
public sealed class WhatIfProbeTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Theory]
    [InlineData("Set-Content -Path C:\\temp\\a.txt -Value x")]
    [InlineData("Get-ChildItem C:\\temp -Filter *.log | Remove-Item")]
    [InlineData("Stop-Service -Name Spooler")]
    public void A_script_whose_writes_are_all_cmdlets_can_be_tried(string script)
    {
        var probe = WhatIfProbe.ForScript(script);

        Assert.NotNull(probe);
        Assert.Contains("$WhatIfPreference = $true", probe, StringComparison.Ordinal);
        Assert.EndsWith(script, probe, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Set-Content C:\\temp\\a.txt x; ipconfig /release")]
    [InlineData("[IO.File]::WriteAllText('C:\\temp\\a.txt', 'x')")]
    [InlineData("'x' > C:\\temp\\a.txt")]
    [InlineData("& $cmd")]
    [InlineData("reg add HKCU\\Software\\X /v Y /d 1 /f")]
    public void A_write_that_is_not_a_cmdlet_is_never_tried(string script) =>
        Assert.Null(WhatIfProbe.ForScript(script));

    [Fact]
    public void A_reading_script_has_nothing_to_try() =>
        Assert.Null(WhatIfProbe.ForScript("Get-Service | Where-Object Status -eq 'Running'"));

    [Fact]
    public void Each_writing_cmdlet_is_checked_for_whatif_before_the_script_runs()
    {
        var probe = WhatIfProbe.ForScript("New-Item -ItemType File -Path C:\\temp\\x.txt; Set-Content C:\\temp\\x.txt 1")!;

        Assert.Contains("'new-item','set-content'", probe, StringComparison.Ordinal);
        Assert.Contains("ContainsKey('WhatIf')", probe, StringComparison.Ordinal);
        Assert.True(probe.IndexOf(WhatIfProbe.UnsupportedMarker, StringComparison.Ordinal) <
                    probe.IndexOf("$WhatIfPreference", StringComparison.Ordinal));
    }

    [Fact]
    public void Tool_actions_get_their_own_whatif_command()
    {
        var service = WhatIfProbe.ScriptFor("windows_service", Args(new { action = "stop", service_name = "Spooler" }));
        var adapter = WhatIfProbe.ScriptFor("network", Args(new { action = "adapter_disable", adapter = "Wi-Fi" }));
        var device = WhatIfProbe.ScriptFor("devices", Args(new { action = "disable", instance_id = "USB\\VID_1&PID_2\\3" }));

        Assert.EndsWith("Stop-Service -Name 'Spooler' -Force -WhatIf", service, StringComparison.Ordinal);
        Assert.EndsWith("Disable-NetAdapter -Name 'Wi-Fi' -WhatIf", adapter, StringComparison.Ordinal);
        Assert.EndsWith("Disable-PnpDevice -InstanceId 'USB\\VID_1&PID_2\\3' -WhatIf", device, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("download_file")]
    [InlineData("write_clipboard")]
    [InlineData("registry")]
    public void Tools_without_whatif_get_no_probe(string tool) =>
        Assert.Null(WhatIfProbe.ScriptFor(tool, Args(new { action = "write", url = "https://x", text = "y" })));

    [Fact]
    public void Output_is_read_for_the_person()
    {
        Assert.Equal(new WhatIfOutcome(false, ""),
            WhatIfProbe.Interpret(ToolResult.Fail("Exit code: 3\n--- stdout ---\nWHATIF_UNSUPPORTED: foo")));
        Assert.Equal("What if: Removing file",
            WhatIfProbe.Interpret(ToolResult.Ok("Exit code: 0\n--- stdout ---\nWhat if: Removing file"))!.Text);
        Assert.True(WhatIfProbe.Interpret(ToolResult.Ok("Exit code: 0"))!.Supported);
        Assert.Null(WhatIfProbe.Interpret(ToolResult.Fail("Exit code: 1")));
    }

    [Fact]
    public async Task A_real_dry_run_leaves_the_file_system_untouched()
    {
        var folder = Path.Combine(Path.GetTempPath(), "amarin-whatif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var existing = Path.Combine(folder, "keep.txt");
        File.WriteAllText(existing, "original");
        var created = Path.Combine(folder, "new.txt");
        try
        {
            var script = $"New-Item -ItemType File -Path '{created}' | Out-Null; Set-Content -Path '{existing}' -Value changed";

            var outcome = await WhatIfProbe.RunAsync("run_powershell", Args(new { command = script }), CancellationToken.None);

            Assert.NotNull(outcome);
            Assert.True(outcome.Supported);
            Assert.False(File.Exists(created));
            Assert.Equal("original", File.ReadAllText(existing));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}

/// <summary>Блок «Что изменится» в окне подтверждения.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class WhatIfConfirmationUiTests
{
    private readonly WpfFixture _wpf;

    public WhatIfConfirmationUiTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public async Task No_block_appears_where_a_dry_run_is_impossible()
    {
        var visibility = await _wpf.Ui.Invoke(async () =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var request = new ConfirmationRequest
            {
                AgentLabel = "Чат",
                Info = DangerousActionGuard.DescribeDetailed("write_file",
                    JsonSerializer.SerializeToElement(new { path = "C:\\temp\\a.txt", content = "x" })),
                Completion = new TaskCompletionSource<ConfirmationAnswer>()
            };

            var probe = (Task)typeof(MainWindow)
                .GetMethod("ProbeConfirmationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [request])!;
            await probe;
            return ((ContentControl)window.FindName("ConfirmationWhatIfHost")).Visibility;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }
}
