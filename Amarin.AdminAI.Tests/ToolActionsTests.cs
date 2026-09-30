using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Новые действия инструментов (1.28.0, C5): как собирается команда, что спрашивается у человека,
/// что попадает в снимок и чем откат возвращает то, что сравнением не вернуть. Ничего здесь не
/// меняет систему — проверяются сборщики команд и правила.
/// </summary>
public sealed class ToolActionsTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    // ───────────────────────── классификация ─────────────────────────

    [Theory]
    [InlineData("windows_service", "set_start_type")]
    [InlineData("startup_programs", "disable")]
    [InlineData("startup_programs", "enable")]
    [InlineData("windows_update", "install")]
    [InlineData("windows_update", "hide")]
    [InlineData("windows_update", "unhide")]
    [InlineData("windows_update", "pause")]
    [InlineData("windows_update", "resume")]
    [InlineData("network", "adapter_enable")]
    [InlineData("network", "adapter_disable")]
    [InlineData("network", "wifi_forget")]
    [InlineData("network", "reset_winsock")]
    [InlineData("network", "reset_ip")]
    [InlineData("dns_config", "set_dns")]
    [InlineData("dns_config", "reset_dns")]
    [InlineData("dns_config", "hosts_add")]
    [InlineData("dns_config", "hosts_remove")]
    [InlineData("devices", "enable")]
    [InlineData("devices", "disable")]
    [InlineData("devices", "rollback_driver")]
    [InlineData("security_status", "quick_scan")]
    [InlineData("security_status", "update_signatures")]
    [InlineData("remote_access", "rdp_enable")]
    [InlineData("remote_access", "rdp_disable")]
    public void Every_new_writing_action_is_a_write_and_is_asked_about(string tool, string action)
    {
        // Запись, которую забыли внести в вопросы, в обычном режиме шла бы молча: шлюз
        // спрашивает только то, что назвал DangerousActionGuard.
        var arguments = Args(new { action });

        Assert.Equal(ToolEffect.Write, ToolEffects.Classify(tool, arguments));
        Assert.True(DangerousActionGuard.RequiresConfirmation(tool, arguments), $"{tool}/{action} идёт без вопроса");
    }

    [Fact]
    public void Writing_to_the_clipboard_is_always_asked_about()
    {
        var arguments = Args(new { text = "hello" });

        Assert.Equal(ToolEffect.Write, ToolEffects.Classify("write_clipboard", arguments));
        Assert.True(DangerousActionGuard.RequiresConfirmation("write_clipboard", arguments));
    }

    [Theory]
    [InlineData("network", "traceroute")]
    [InlineData("network", "wifi_profiles")]
    [InlineData("filesystem", "search")]
    [InlineData("startup_programs", "status")]
    public void The_new_reading_actions_stay_reads(string tool, string action)
    {
        // Иначе в режиме «только чтение» поиск файлов и трассировка перестали бы работать.
        Assert.Equal(ToolEffect.Read, ToolEffects.Classify(tool, Args(new { action })));
    }

    [Theory]
    [InlineData("windows_service", "set_start_type", true)]
    [InlineData("startup_programs", "disable", true)]
    [InlineData("windows_update", "pause", true)]
    [InlineData("dns_config", "set_dns", true)]
    [InlineData("dns_config", "hosts_add", true)]
    [InlineData("network", "adapter_disable", true)]
    [InlineData("network", "wifi_forget", true)]
    [InlineData("devices", "disable", true)]
    [InlineData("remote_access", "rdp_enable", true)]
    [InlineData("windows_update", "install", false)]
    [InlineData("devices", "rollback_driver", false)]
    [InlineData("security_status", "quick_scan", false)]
    public void A_snapshot_is_taken_where_rollback_can_return_something(string tool, string action, bool snapshot) =>
        Assert.Equal(snapshot, DangerousActionGuard.RequiresUndoSnapshot(tool, Args(new { action })));

    [Theory]
    [InlineData("disabled")]
    [InlineData("manual")]
    [InlineData("automatic_delayed")]
    public void A_protected_service_cannot_be_demoted_by_start_type(string startType)
    {
        var arguments = Args(new { action = "set_start_type", service_name = "WinDefend", start_type = startType });

        Assert.True(ProtectedSystemTargets.TryGetServiceBlock(arguments, out _, _ => "WinDefend"));
    }

    [Fact]
    public void Setting_a_protected_service_to_automatic_is_allowed()
    {
        var arguments = Args(new { action = "set_start_type", service_name = "WinDefend", start_type = "automatic" });

        Assert.False(ProtectedSystemTargets.TryGetServiceBlock(arguments, out _, _ => "WinDefend"));
    }

    // ───────────────────────── службы ─────────────────────────

    [Theory]
    [InlineData("automatic", "auto")]
    [InlineData("automatic_delayed", "delayed-auto")]
    [InlineData("Manual", "demand")]
    [InlineData("disabled", "disabled")]
    public void Start_types_map_to_sc_words(string given, string expected)
    {
        Assert.True(ServiceCommands.TryStartType(given, out var value));
        Assert.Equal(expected, value);
        Assert.Equal(["config", "Spooler", "start=", expected], ServiceCommands.ConfigArguments("Spooler", value));
    }

    [Theory]
    [InlineData("boot")]
    [InlineData("auto start= disabled")]
    [InlineData("")]
    public void Unknown_start_types_are_refused(string given) =>
        Assert.False(ServiceCommands.TryStartType(given, out _));

    // ───────────────────────── автозагрузка ─────────────────────────

    [Fact]
    public void A_disabled_mark_is_odd_and_carries_the_time()
    {
        var now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);

        var off = StartupApproval.Mark(enabled: false, now);
        var on = StartupApproval.Mark(enabled: true, now);

        Assert.Equal(12, off.Length);
        Assert.Equal(0x03, off[0]);
        Assert.Equal(now.ToFileTimeUtc(), BitConverter.ToInt64(off, 4));
        Assert.Equal(0x02, on[0]);
        Assert.All(on.Skip(1), b => Assert.Equal(0, b));
        Assert.False(StartupApproval.IsEnabled(off));
        Assert.True(StartupApproval.IsEnabled(on));
        Assert.True(StartupApproval.IsEnabled([0x06, 0, 0, 0]));
        Assert.True(StartupApproval.IsEnabled(null));
    }

    [Theory]
    [InlineData("hkcu_run", true)]
    [InlineData("HKLM_RUN32", true)]
    [InlineData("common_folder", true)]
    [InlineData("runonce", false)]
    [InlineData("", false)]
    public void Only_known_startup_locations_are_accepted(string location, bool known) =>
        Assert.Equal(known, StartupApproval.TryLocation(location, out _));

    // ───────────────────────── Центр обновления ─────────────────────────

    [Fact]
    public void Kb_numbers_are_normalised_and_checked()
    {
        Assert.True(UpdateCommands.TryKbList(["KB5034441", "5034441", "kb5035845"], out var kbs, out _));
        Assert.Equal(["5034441", "5035845"], kbs);

        Assert.False(UpdateCommands.TryKbList(["KB5034441'; Restart-Computer; '"], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Installing_updates_never_reboots()
    {
        var script = UpdateCommands.InstallScript(["5034441"]);

        Assert.Contains("'5034441'", script, StringComparison.Ordinal);
        Assert.Contains("IsHidden=0", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart-Computer", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shutdown", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RebootRequired", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pause_is_clamped_and_written_in_utc()
    {
        var now = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc);

        var script = UpdateCommands.PauseScript(90, now);

        Assert.Contains("2026-11-04T08:00:00Z", script, StringComparison.Ordinal);
        Assert.Contains("PauseUpdatesExpiryTime", script, StringComparison.Ordinal);
    }

    // ───────────────────────── сеть и DNS ─────────────────────────

    [Fact]
    public void Dns_servers_must_be_addresses_and_are_quoted()
    {
        Assert.True(NetworkCommands.TryDnsServers(["1.1.1.1", " 8.8.8.8 ", "1.1.1.1", "2606:4700:4700::1111"], out var list, out _));
        Assert.Equal(["1.1.1.1", "8.8.8.8", "2606:4700:4700::1111"], list);
        Assert.False(NetworkCommands.TryDnsServers(["1.1.1.1; Remove-Item C:\\"], out _, out _));
        Assert.False(NetworkCommands.TryDnsServers([], out _, out _));

        var script = NetworkCommands.SetDnsScript("Ethernet ’x", list);
        Assert.Contains("-InterfaceAlias 'Ethernet ’’x'", script, StringComparison.Ordinal);
        Assert.Contains("@('1.1.1.1','8.8.8.8','2606:4700:4700::1111')", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_to_hosts_replaces_an_older_address_and_keeps_comments()
    {
        string[] lines = ["# comment", "127.0.0.1 localhost", "10.0.0.5 intranet other # keep", ""];

        var (updated, changed) = NetworkCommands.HostsAdd(lines, "10.0.0.9", "intranet");

        Assert.True(changed);
        Assert.Equal("# comment", updated[0]);
        Assert.Equal("127.0.0.1 localhost", updated[1]);
        Assert.Equal("10.0.0.5\tother # keep", updated[2]);
        Assert.Equal("10.0.0.9\tintranet", updated[^1]);
    }

    [Fact]
    public void Adding_the_same_entry_twice_changes_nothing()
    {
        string[] lines = ["10.0.0.9\tintranet"];

        var (_, changed) = NetworkCommands.HostsAdd(lines, "10.0.0.9", "INTRANET");

        Assert.False(changed);
    }

    [Fact]
    public void Removing_from_hosts_leaves_other_names_and_commented_lines()
    {
        string[] lines = ["# 10.0.0.5 intranet", "10.0.0.5 intranet other", "10.0.0.6 intranet"];

        var (updated, removed) = NetworkCommands.HostsRemove(lines, "intranet");

        Assert.Equal(2, removed);
        Assert.Equal(["# 10.0.0.5 intranet", "10.0.0.5\tother"], updated);
    }

    [Theory]
    [InlineData("Home WiFi", true)]
    [InlineData("Кафе_5G", true)]
    [InlineData("x\" name=all", false)]
    [InlineData("", false)]
    public void Wifi_profile_names_cannot_break_out_of_netsh_quotes(string name, bool accepted)
    {
        Assert.Equal(accepted, NetworkCommands.IsWifiProfileName(name));
        if (accepted)
        {
            Assert.Equal($"wlan delete profile name=\"{name}\"", NetworkCommands.WifiDeleteArguments(name));
        }
    }

    [Fact]
    public void Traceroute_hops_are_clamped_and_the_host_is_the_last_argument()
    {
        var arguments = NetworkCommands.TracertArguments("example.com", 99);

        Assert.Equal(["-d", "-h", "30", "-w", "1000", "example.com"], arguments);
        Assert.True(NetworkCommands.TracertTimeoutSeconds(30) >= 30 * 3);
    }

    // ───────────────────────── устройства, RDP, Защитник ─────────────────────────

    [Theory]
    [InlineData(@"USB\VID_046D&PID_C52B\5&1A2B3C4D&0&2", true)]
    [InlineData(@"PCI\VEN_8086*", false)]
    [InlineData("x`$(calc)", false)]
    [InlineData("a\"b", false)]
    public void Device_ids_without_wildcards_only(string id, bool accepted) =>
        Assert.Equal(accepted, DeviceCommands.IsInstanceId(id));

    [Fact]
    public void Disabling_checks_the_protected_classes_inside_the_script()
    {
        var script = DeviceCommands.SetEnabledScript(@"HID\VID_1&PID_2\3", enable: false);

        Assert.Contains("PROTECTED_DEVICE_CLASS", script, StringComparison.Ordinal);
        Assert.Contains("'Keyboard'", script, StringComparison.Ordinal);
        Assert.Contains("'DiskDrive'", script, StringComparison.Ordinal);
        Assert.Contains("-Confirm:$false", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Wmi_filter_doubles_backslashes()
    {
        Assert.Equal(@"USB\\VID_1\\2", DeviceCommands.WmiLiteral(@"USB\VID_1\2"));
    }

    [Fact]
    public void Rdp_uses_the_language_neutral_firewall_group()
    {
        var on = SecurityCommands.RdpScript(enable: true);
        var off = SecurityCommands.RdpScript(enable: false);

        Assert.Contains("fDenyTSConnections -Value 0", on, StringComparison.Ordinal);
        Assert.Contains("Enable-NetFirewallRule -Group '@FirewallAPI.dll,-28752'", on, StringComparison.Ordinal);
        Assert.Contains("fDenyTSConnections -Value 1", off, StringComparison.Ordinal);
        Assert.Contains("Disable-NetFirewallRule", off, StringComparison.Ordinal);
    }

    // ───────────────────────── обратные шаги ─────────────────────────

    [Fact]
    public void Undo_restores_static_dns_or_dhcp()
    {
        var staticStep = new UndoStep { Kind = UndoKind.Dns, Target = "Ethernet", Values = ["1.1.1.1"] };
        var dhcpStep = new UndoStep { Kind = UndoKind.Dns, Target = "Ethernet", Enabled = true };

        Assert.Contains("-ServerAddresses @('1.1.1.1')", UndoCommands.Script(staticStep, Path.GetTempPath())!,
            StringComparison.Ordinal);
        Assert.Contains("-ResetServerAddresses", UndoCommands.Script(dhcpStep, Path.GetTempPath())!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_tampered_undo_step_builds_no_command()
    {
        // Снимок лежит в папке, куда пишет пользователь; подложенное значение не должно стать кодом.
        var dns = new UndoStep { Kind = UndoKind.Dns, Target = "Ethernet", Values = ["1.1.1.1'); calc; ('"] };
        var adapter = new UndoStep { Kind = UndoKind.Adapter, Target = "*" };
        var rules = new UndoStep { Kind = UndoKind.FirewallRules, Values = ["a", "b"], States = [true] };
        var wifi = new UndoStep { Kind = UndoKind.WifiProfile, Target = "Home", File = @"..\..\evil.xml" };

        Assert.Null(UndoCommands.Script(dns, Path.GetTempPath()));
        Assert.Null(UndoCommands.Script(adapter, Path.GetTempPath()));
        Assert.Null(UndoCommands.Script(rules, Path.GetTempPath()));
        Assert.Null(UndoCommands.WifiRestoreArguments(wifi, Path.GetTempPath()));
        Assert.Null(UndoCommands.SnapshotFile(wifi, Path.GetTempPath()));
    }

    [Fact]
    public void Firewall_rules_come_back_to_their_own_state()
    {
        var step = new UndoStep
        {
            Kind = UndoKind.FirewallRules,
            Values = ["RemoteDesktop-UserMode-In-TCP", "RemoteDesktop-UserMode-In-UDP"],
            States = [true, false]
        };

        var script = UndoCommands.Script(step, Path.GetTempPath())!;

        Assert.Contains("-Name 'RemoteDesktop-UserMode-In-TCP' -Enabled True", script, StringComparison.Ordinal);
        Assert.Contains("-Name 'RemoteDesktop-UserMode-In-UDP' -Enabled False", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Undo_steps_appear_in_the_rollback_plan()
    {
        var plan = new RollbackPlan { SnapshotId = "x" };
        plan.Undo.Add(new UndoStep { Kind = UndoKind.Adapter, Target = "Wi-Fi", Enabled = true });

        Assert.Equal(1, plan.Count);
        Assert.Contains("Wi-Fi", Assert.Single(plan.Lines()), StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_change_to_the_same_target_keeps_the_first_state()
    {
        var first = new UndoStep { Kind = UndoKind.Adapter, Target = "Ethernet", Enabled = true };
        var second = new UndoStep { Kind = UndoKind.Adapter, Target = "ETHERNET", Enabled = false };

        Assert.True(first.SameTargetAs(second));
    }

    // ───────────────────────── буфер обмена ─────────────────────────

    [Theory]
    [InlineData("123456-123456-123456-123456-123456-123456-123456-123456", "BitLocker")]
    [InlineData("Password: hunter2", "password")]
    [InlineData("пароль = qwerty", "password")]
    [InlineData("    Key Content            : MySecretWifi", "Wi-Fi")]
    [InlineData("export KEY=sk-or-v1-0123456789abcdef0123456789abcdef", "API")]
    public void Secrets_never_reach_the_clipboard(string text, string kind)
    {
        var reason = ClipboardWriteTool.SecretReason(text, []);

        Assert.NotNull(reason);
        Assert.Contains(kind, reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_programs_own_keys_are_refused_whatever_their_shape()
    {
        Assert.NotNull(ClipboardWriteTool.SecretReason("key is venice-abc12345xyz", ["venice-abc12345xyz"]));
    }

    [Theory]
    [InlineData("Get-Service | Where-Object Status -eq 'Running'")]
    [InlineData("Сброс пароля делается в «Параметрах»")]
    public void Ordinary_text_is_allowed(string text) =>
        Assert.Null(ClipboardWriteTool.SecretReason(text, []));

    // ───────────────────────── поиск файлов ─────────────────────────

    [Fact]
    public void Search_filters_by_size_and_date_and_stops_at_the_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "deep", "deeper"));
        try
        {
            File.WriteAllText(Path.Combine(root, "a.log"), new string('x', 10));
            File.WriteAllText(Path.Combine(root, "deep", "b.log"), new string('x', 5000));
            File.WriteAllText(Path.Combine(root, "deep", "deeper", "c.log"), new string('x', 5000));
            File.WriteAllText(Path.Combine(root, "d.txt"), new string('x', 5000));

            var (big, _) = FileSearch.Run(new FileSearchQuery(root, "*.log", 1000, null, null, null, 100, 8), CancellationToken.None);
            var (shallow, _) = FileSearch.Run(new FileSearchQuery(root, "*.log", null, null, null, null, 100, 1), CancellationToken.None);
            var (limited, truncated) = FileSearch.Run(new FileSearchQuery(root, "*", null, null, null, null, 2, 8), CancellationToken.None);
            var (future, _) = FileSearch.Run(
                new FileSearchQuery(root, "*", null, null, DateTime.Now.AddDays(1), null, 100, 8), CancellationToken.None);

            Assert.Equal(["b.log", "c.log"], big.Select(file => file.Name).Order());
            Assert.Equal(["a.log", "b.log"], shallow.Select(file => file.Name).Order());
            Assert.Equal(2, limited.Count);
            Assert.True(truncated);
            Assert.Empty(future);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Search_hears_the_stop_button()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => FileSearch.Run(
            new FileSearchQuery(Path.GetTempPath(), "*", null, null, null, null, 10, 0), cts.Token));
    }
}
