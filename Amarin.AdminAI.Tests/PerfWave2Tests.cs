using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class PerfWave2Tests
{
    [Fact]
    public void DeletionGuard_still_blocks_remove_item()
    {
        Assert.True(DeletionGuard.PowerShellAttemptsDeletion("Remove-Item C:\\temp\\a.txt"));
        Assert.True(DeletionGuard.PowerShellAttemptsDeletion("rmdir foo"));
        Assert.False(DeletionGuard.PowerShellAttemptsDeletion("Get-ChildItem C:\\temp"));
    }

    [Fact]
    public void DangerousActionGuard_still_flags_stop_service()
    {
        using var doc = JsonDocument.Parse("""{"command":"Stop-Service wuauserv"}""");
        Assert.True(DangerousActionGuard.RequiresConfirmation("run_powershell", doc.RootElement));
        Assert.False(DangerousActionGuard.RequiresConfirmation("run_powershell",
            JsonDocument.Parse("""{"command":"Get-Service"}""").RootElement));
    }

    [Fact]
    public void Parallel_safe_for_read_tools_only()
    {
        using var empty = JsonDocument.Parse("{}");
        Assert.True(Agent.IsParallelSafeToolCall("system_info", empty.RootElement, readOnlyMode: false));
        Assert.True(Agent.IsParallelSafeToolCall("performance",
            JsonDocument.Parse("""{"action":"summary"}""").RootElement, readOnlyMode: false));

        Assert.False(Agent.IsParallelSafeToolCall("ask_user", empty.RootElement, readOnlyMode: false));
        Assert.False(Agent.IsParallelSafeToolCall("download_file",
            JsonDocument.Parse("""{"url":"https://example.com/a.exe"}""").RootElement, readOnlyMode: false));
        Assert.False(Agent.IsParallelSafeToolCall("windows_service",
            JsonDocument.Parse("""{"action":"stop","service_name":"wuauserv"}""").RootElement, readOnlyMode: false));
    }

    [Fact]
    public void Native_net_table_returns_rows()
    {
        var tcp = NativeNetTable.GetTcpRows();
        var udp = NativeNetTable.GetUdpRows();
        Assert.True(tcp.Count + udp.Count > 0);
    }

    [Fact]
    public async Task Event_log_list_does_not_fail()
    {
        var tool = new EventLogTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"action":"list_logs"}""").RootElement);
        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Output));
    }

    [Fact]
    public async Task Performance_summary_does_not_fail()
    {
        var tool = new PerformanceTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"action":"summary"}""").RootElement);
        Assert.True(result.Success);
        Assert.Contains("CPU", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Network_connections_do_not_fail()
    {
        var tool = new NetworkTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"action":"connections"}""").RootElement);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Port_listener_list_does_not_fail()
    {
        var tool = new PortListenerTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"action":"list_listeners"}""").RootElement);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Disk_space_analyze_does_not_fail()
    {
        var tool = new DiskSpaceTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"action":"analyze"}""").RootElement);
        Assert.True(result.Success);
        Assert.Contains("Volumes", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wmi_os_scope_does_not_fail()
    {
        var tool = new WmiTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("""{"scope":"os"}""").RootElement);
        Assert.True(result.Success);
    }
}
