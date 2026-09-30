using System.Diagnostics;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Стоп» хода обязан обрывать и то, что инструмент уже запустил. До 1.28.0 почти все
/// инструменты звали синхронный <c>PowerShellHelper.Run</c> без токена: остановленный ход
/// продолжал ждать скрипт до его таймаута (до десяти минут), а процесс жил дальше.
/// </summary>
public sealed class ToolCancellationTests
{
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task Cancelling_a_script_throws_and_kills_the_process()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"amarin-cancel-{Guid.NewGuid():N}.txt");
        try
        {
            using var cts = new CancellationTokenSource();
            var run = PowerShellHelper.RunAsync(
                $"Set-Content -LiteralPath '{PowerShellHelper.QuoteLiteral(pidFile)}' -Value $PID; Start-Sleep -Seconds 120",
                300,
                cts.Token);

            var pid = await WaitForPidAsync(pidFile);
            var watch = Stopwatch.StartNew();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(watch.Elapsed < Promptly, $"отмена заняла {watch.Elapsed}");
            Assert.True(HasExited(pid), "powershell.exe пережил отмену хода");
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task The_synchronous_wrapper_listens_to_the_token_too()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() =>
            PowerShellHelper.Run("Start-Sleep -Seconds 120", 300, cancellationToken: cts.Token)));
        Assert.True(watch.Elapsed < Promptly, $"отмена заняла {watch.Elapsed}");
    }

    [Fact]
    public async Task A_timeout_is_a_failure_and_not_a_cancellation()
    {
        // Таймаут — ответ инструмента, модель его читает; отменой он быть не должен, иначе
        // ход останавливался бы сам, хотя «Стоп» никто не нажимал.
        var result = await PowerShellHelper.RunAsync("Start-Sleep -Seconds 60", 5, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("timed out", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_a_native_program_throws_promptly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeProcess.RunAsync("ping.exe", ["-n", "60", "127.0.0.1"], 300, cts.Token));
        Assert.True(watch.Elapsed < Promptly, $"отмена заняла {watch.Elapsed}");
    }

    [Theory]
    [InlineData("windows_update", """{"action":"status"}""")]
    [InlineData("security_status", """{"action":"defender_status"}""")]
    [InlineData("startup_programs", """{"action":"list_all"}""")]
    [InlineData("devices", """{"action":"usb"}""")]
    [InlineData("reliability", """{"action":"recent_failures"}""")]
    [InlineData("firewall_rules", """{"action":"list"}""")]
    [InlineData("windows_features", """{"action":"list"}""")]
    [InlineData("dns_config", """{"action":"resolvers"}""")]
    [InlineData("local_users", """{"action":"list_users"}""")]
    [InlineData("disk_management", """{"action":"list_disks"}""")]
    [InlineData("network", """{"action":"firewall_rules"}""")]
    public async Task A_cancelled_turn_reaches_every_powershell_tool(string tool, string arguments)
    {
        // Отменённый токен до запуска: инструмент обязан отдать отмену, а не прочитать её
        // как ошибку и вернуть модели «инструмент упал».
        var registry = new ToolRegistry(
        [
            new WindowsUpdateTool(), new SecurityTool(), new StartupProgramsTool(), new DevicesTool(),
            new ReliabilityTool(), new FirewallRulesTool(), new WindowsFeaturesTool(), new DnsConfigTool(),
            new LocalUsersTool(), new DiskManagementTool(), new NetworkTool()
        ]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var doc = JsonDocument.Parse(arguments);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            registry.ExecuteAsync(tool, doc.RootElement.Clone(), cts.Token));
    }

    [Fact]
    public void A_firewall_profile_outside_the_list_never_reaches_netsh()
    {
        Assert.True(NetworkCommands.TryFirewallProfile("Private", out var profile));
        Assert.Equal("privateprofile", profile);
        Assert.True(NetworkCommands.TryFirewallProfile("all", out var all));
        Assert.Equal("allprofiles", all);
        Assert.Equal(["advfirewall", "set", "publicprofile", "state", "off"],
            NetworkCommands.FirewallState("publicprofile", enabled: false));

        Assert.False(NetworkCommands.TryFirewallProfile("private state off & calc", out _));
        Assert.False(NetworkCommands.TryFirewallProfile("", out _));
        Assert.False(NetworkCommands.TryFirewallProfile(null, out _));
    }

    [Theory]
    [InlineData("Web01", true)]
    [InlineData("Тестовая ВМ", true)]
    [InlineData("vm*", false)]
    [InlineData("x`y", false)]
    [InlineData("a[1]", false)]
    [InlineData("", false)]
    public void Only_a_plain_vm_name_is_accepted(string name, bool accepted) =>
        Assert.Equal(accepted, VirtualizationCommands.IsVmName(name));

    [Fact]
    public void A_quote_in_a_vm_name_stays_inside_the_string()
    {
        var script = VirtualizationCommands.StopVm("x’; Remove-Item C:\\ -Recurse; ’");

        Assert.Contains("'x’’; Remove-Item C:\\ -Recurse; ’’'", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("web_1.a-b", true)]
    [InlineData("3f2a9c", true)]
    [InlineData("-rm", false)]
    [InlineData("a b", false)]
    [InlineData("a;b", false)]
    public void Only_a_docker_style_container_name_is_accepted(string name, bool accepted) =>
        Assert.Equal(accepted, VirtualizationCommands.IsContainerName(name));

    private static async Task<int> WaitForPidAsync(string pidFile)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var text = File.Exists(pidFile) ? File.ReadAllText(pidFile).Trim() : "";
                if (int.TryParse(text, out var pid))
                {
                    return pid;
                }
            }
            catch (IOException)
            {
                // Файл ещё пишется.
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("powershell.exe так и не записал свой PID");
    }

    private static bool HasExited(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }
}
