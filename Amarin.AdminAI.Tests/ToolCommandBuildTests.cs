using System.Text.Json;
using Amarin.Tools;
using Microsoft.Win32;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сборка команд у прежних инструментов (H2): значение от модели не выходит из своей строки,
/// перечни проверяются, разбор чужого вывода не падает. Ничего не запускается.
/// </summary>
/// <remarks>
/// Скрипты собираются строкой, и единственная защита в них — удвоенная кавычка и проверка
/// формы. Ошибка там не видна глазами: скрипт выполняется, только делает ещё и чужое. Поэтому
/// каждый сборщик получает здесь враждебное значение.
/// </remarks>
public sealed class ToolCommandBuildTests
{
    private const string Hostile = "x’; Remove-Item C:\\ -Recurse; ’";

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ───────────────────────── firewall_rules ─────────────────────────

    [Theory]
    [InlineData("Allow RDP (office)", true)]
    [InlineData("rule'; calc; '", false)]
    [InlineData("rule$(calc)", false)]
    [InlineData("", false)]
    public void Firewall_rule_names_are_checked_before_they_reach_a_script(string name, bool accepted) =>
        Assert.Equal(accepted, FirewallRulesTool.TryGetSafeName(Args(new { name }), out _, out _));

    [Fact]
    public void Firewall_create_quotes_port_and_program()
    {
        var script = FirewallRulesTool.CreateScript("Web", "inbound", "allow", "tcp", "8080", @"C:\App\a’b.exe");

        Assert.Contains("Direction   = 'Inbound'", script, StringComparison.Ordinal);
        Assert.Contains("Protocol    = 'TCP'", script, StringComparison.Ordinal);
        Assert.Contains(@"$prog = 'C:\App\a’’b.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Firewall_create_turns_anything_unknown_into_a_known_word()
    {
        var script = FirewallRulesTool.CreateScript("Web", "sideways", "maybe", "icmp; calc", null, null);

        Assert.Contains("Direction   = 'Outbound'", script, StringComparison.Ordinal);
        Assert.Contains("Action      = 'Allow'", script, StringComparison.Ordinal);
        Assert.Contains("Protocol    = 'Any'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("calc", script, StringComparison.Ordinal);
    }

    // ───────────────────────── scheduled_task ─────────────────────────

    [Theory]
    [InlineData("Backup nightly", true)]
    [InlineData("Backup\"; calc", false)]
    [InlineData("", false)]
    public void Task_names_with_quotes_are_refused(string name, bool accepted) =>
        Assert.Equal(accepted, ScheduledTaskTool.TryValidateTaskName(name, out _));

    [Fact]
    public void Task_arguments_split_path_and_name()
    {
        Assert.Equal(("\\Folder\\", "Name"), ChangeRollbackStore.SplitTaskName("\\Folder\\Name"));
        Assert.Equal(["/change", "/tn", "\\Folder\\Name", "/disable"], ChangeRollbackStore.TaskToggleArguments("\\Folder\\Name", disable: true));
        Assert.Equal(["/delete", "/tn", "\\X", "/f"], ChangeRollbackStore.TaskDeleteArguments("\\X"));
    }

    // ───────────────────────── local_users ─────────────────────────

    [Theory]
    [InlineData("Alice", true)]
    [InlineData("Администраторы", true)]
    [InlineData("DOMAIN\\bob", true)]
    [InlineData("bob\"; net user x /add", false)]
    [InlineData("a;b", false)]
    [InlineData("$(calc)", false)]
    [InlineData("", false)]
    public void Local_account_names_are_plain(string name, bool accepted) =>
        Assert.Equal(accepted, LocalUsersSafety.TryValidateAccountName(name, out _, out _));

    [Fact]
    public void Local_user_scripts_keep_the_name_inside_quotes()
    {
        var escaped = LocalUsersSafety.EscapeForPowerShell(Hostile);

        Assert.Contains($"'{escaped}'", LocalUsersTool.SetUserEnabledScript(escaped, enabled: false), StringComparison.Ordinal);
        Assert.Contains($"'{escaped}'", LocalUsersTool.ChangeGroupScript(escaped, "Users", add: true), StringComparison.Ordinal);
        Assert.Equal("x’’; Remove-Item C:\\ -Recurse; ’’", escaped);
    }

    [Fact]
    public void Net_localgroup_output_is_read_without_its_header_and_footer()
    {
        var output = """
            Alias name     Administrators
            Comment        Administrators have complete and unrestricted access

            Members

            -------------------------------------------------------------------------------
            Administrator
            Alice
            The command completed successfully.

            """;

        Assert.Equal(["Administrator", "Alice"], LocalUsersSafety.ParseNetLocalGroupMembers(output));
    }

    // ───────────────────────── registry ─────────────────────────

    [Theory]
    [InlineData("0x10", 16)]
    [InlineData("42", 42)]
    [InlineData("0xFFFFFFFF", -1)]
    [InlineData("", 0)]
    public void Registry_integers_read_decimal_and_hex(string text, int expected) =>
        Assert.Equal(expected, RegistryTool.ParseInteger(text));

    [Fact]
    public void Registry_values_are_shown_readably()
    {
        Assert.Equal("01-02-FF", RegistryTool.FormatValue(new byte[] { 1, 2, 255 }, RegistryValueKind.Binary));
        Assert.Equal("a | b", RegistryTool.FormatValue(new[] { "a", "b" }, RegistryValueKind.MultiString));
        Assert.Equal("(null)", RegistryTool.FormatValue(null, RegistryValueKind.String));
        Assert.Equal(1L << 40, RegistryTool.ParseLong("0x10000000000"));
    }

    // ───────────────────────── restore_point, system_repair ─────────────────────────

    [Fact]
    public void Restore_point_description_stays_a_string()
    {
        var script = RestorePointTool.CreateScript(PowerShellHelper.QuoteLiteral(Hostile));

        Assert.Contains("'x’’; Remove-Item C:\\ -Recurse; ’’'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sfc_status_only_reads()
    {
        Assert.DoesNotContain("/scannow", SystemRepairTool.StatusSfcScript, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ToolEffect.Read, ToolEffects.Classify("system_repair", Args(new { action = "status_sfc" })));
        Assert.Equal(ToolEffect.Write, ToolEffects.Classify("system_repair", Args(new { action = "run_sfc" })));
    }

    // ───────────────────────── credentials ─────────────────────────

    [Theory]
    [InlineData("my", "My")]
    [InlineData("AuthRoot", "AuthRoot")]
    [InlineData("All", "All")]
    public void Certificate_stores_come_from_the_list(string given, string expected)
    {
        Assert.True(CredentialsTool.TryStore(given, out var store));
        Assert.Equal(expected, store);
    }

    [Theory]
    [InlineData("My$(calc)")]
    [InlineData("My\"; calc; \"")]
    [InlineData("..\\..\\")]
    public void A_crafted_store_never_reaches_the_script(string given) =>
        Assert.False(CredentialsTool.TryStore(given, out _));

    [Fact]
    public async Task A_crafted_store_is_refused_before_anything_runs()
    {
        var result = await new CredentialsTool().ExecuteAsync(Args(new { action = "list_certs", store = "My$(calc)" }));

        Assert.False(result.Success);
        Assert.Contains("store must be one of", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Expiring_certificates_use_only_the_number_of_days()
    {
        Assert.Contains("AddDays(30)", CredentialsTool.ExpiringCertsScript(30), StringComparison.Ordinal);
    }

    // ───────────────────────── windows_update, dns_config ─────────────────────────

    [Fact]
    public void Update_history_count_is_a_number_in_the_script()
    {
        Assert.Contains("$count - 25", WindowsUpdateTool.HistoryScript(25), StringComparison.Ordinal);
        Assert.Equal(ToolEffect.Read, ToolEffects.Classify("windows_update", Args(new { action = "history" })));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("пример.рф", true)]
    [InlineData("example.com; Remove-Item C:\\", false)]
    [InlineData("a'b", false)]
    public void Host_names_are_only_names(string value, bool accepted) =>
        Assert.Equal(accepted, DnsConfigTool.IsHostName(value));

    // ───────────────────────── snapshots, scrape_url ─────────────────────────

    [Fact]
    public void Snapshot_ids_are_read_as_times()
    {
        Assert.Equal(new DateTime(2026, 9, 30, 10, 15, 0), RollbackSnapshots.ParseId("20260930_101500"));
        Assert.Equal(DateTime.MinValue, RollbackSnapshots.ParseId("not-a-snapshot"));
    }

    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("javascript:alert(1)")]
    [InlineData("example.com")]
    public async Task Scrape_only_fetches_web_addresses(string url)
    {
        var called = false;
        var tool = new ScrapeUrlTool((_, _) =>
        {
            called = true;
            return Task.FromResult("page");
        });

        var result = await tool.ExecuteAsync(Args(new { url }));

        Assert.False(result.Success);
        Assert.False(called);
    }

    [Fact]
    public async Task A_stopped_scrape_is_a_cancellation_not_an_error()
    {
        using var cts = new CancellationTokenSource();
        var tool = new ScrapeUrlTool((_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult("never");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tool.ExecuteAsync(Args(new { url = "https://example.com" }), cts.Token));
    }

    [Fact]
    public async Task A_scrape_timeout_stays_an_answer_for_the_model()
    {
        // Таймаут HttpClient — тоже OperationCanceledException, но «Стоп» никто не нажимал: ход
        // не должен останавливаться, модель должна прочитать отказ.
        var tool = new ScrapeUrlTool((_, _) => throw new TaskCanceledException("timeout"));

        var result = await tool.ExecuteAsync(Args(new { url = "https://example.com" }), CancellationToken.None);

        Assert.False(result.Success);
    }

    // ───────────────────────── windows_service ─────────────────────────

    [Fact]
    public void Service_snapshot_key_is_the_services_own()
    {
        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Services\Spooler", ServiceCommands.RegistryKey("Spooler"));
        Assert.Equal(ToolEffect.Read, ToolEffects.Classify("windows_service", Args(new { action = "status" })));
    }
}
