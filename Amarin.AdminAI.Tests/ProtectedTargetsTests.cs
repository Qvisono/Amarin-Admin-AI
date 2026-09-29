using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Критичные процессы и службы Windows останавливать нельзя даже с подтверждением: остановленный
/// lsass роняет систему, остановленный WinDefend выключает защиту.
/// </summary>
public sealed class ProtectedTargetsTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("""{"action":"kill","process_name":"lsass"}""")]
    [InlineData("""{"action":"stop","process_name":"csrss.exe"}""")]
    [InlineData("""{"action":"KILL","process_name":"  Winlogon "}""")]
    [InlineData("""{"action":"kill","process_name":"svchost"}""")]
    [InlineData("""{"action":"stop","process_name":"C:\\Windows\\System32\\smss.exe"}""")]
    [InlineData("""{"action":"kill","process_name":"MsMpEng"}""")]
    [InlineData("""{"action":"kill","pid":4}""")]
    public void A_critical_process_is_refused_before_any_question(string json)
    {
        Assert.True(ProtectedSystemTargets.TryGetProcessBlock(Args(json), out var reason, _ => null));
        Assert.StartsWith("ЗАПРЕЩЕНО", reason, StringComparison.Ordinal);
        Assert.Contains("Не повторяй попытку", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_critical_process_named_by_pid_is_refused_too()
    {
        Assert.True(ProtectedSystemTargets.TryGetProcessBlock(
            Args("""{"action":"kill","pid":812}"""), out _, pid => pid == 812 ? "wininit" : null));
    }

    [Fact]
    public void The_program_does_not_stop_itself()
    {
        var json = $$"""{"action":"kill","pid":{{Environment.ProcessId}}}""";
        Assert.True(ProtectedSystemTargets.TryGetProcessBlock(Args(json), out var reason, _ => null));
        Assert.Contains("Amarin", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"action":"kill","process_name":"notepad"}""")]
    [InlineData("""{"action":"list","process_name":"lsass"}""")]
    [InlineData("""{"action":"stop","pid":999999}""")]
    public void Ordinary_work_is_not_refused(string json)
    {
        Assert.False(ProtectedSystemTargets.TryGetProcessBlock(Args(json), out _, _ => "notepad"));
    }

    [Theory]
    [InlineData("""{"action":"stop","service_name":"WinDefend"}""")]
    [InlineData("""{"action":"restart","service_name":"eventlog"}""")]
    [InlineData("""{"action":"Stop","service_name":"RpcSs"}""")]
    [InlineData("""{"action":"stop","service_name":"Dnscache"}""")]
    public void A_critical_service_is_refused(string json)
    {
        Assert.True(ProtectedSystemTargets.TryGetServiceBlock(Args(json), out var reason, _ => null));
        Assert.StartsWith("ЗАПРЕЩЕНО", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_critical_service_named_by_its_display_name_is_refused()
    {
        Assert.True(ProtectedSystemTargets.TryGetServiceBlock(
            Args("""{"action":"stop","service_name":"Windows Event Log"}"""),
            out _,
            given => given == "Windows Event Log" ? "EventLog" : null));
    }

    [Theory]
    [InlineData("""{"action":"restart","service_name":"wuauserv"}""")]
    [InlineData("""{"action":"stop","service_name":"Spooler"}""")]
    [InlineData("""{"action":"start","service_name":"WinDefend"}""")]
    public void Ordinary_service_work_is_not_refused(string json)
    {
        // Перезапуск Центра обновления — обычный способ починить зависшее обновление, а запуск
        // защиты только к лучшему.
        Assert.False(ProtectedSystemTargets.TryGetServiceBlock(Args(json), out _, _ => null));
    }
}
