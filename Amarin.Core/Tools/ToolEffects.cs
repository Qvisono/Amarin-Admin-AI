using System.Collections.Frozen;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>Меняет ли вызов что-нибудь на машине.</summary>
public enum ToolEffect
{
    Read,
    Write
}

/// <summary>
/// Чтение или запись — по инструменту и его <c>action</c>. Неизвестное — запись.
/// </summary>
/// <remarks>
/// <para>
/// Список разрешённого, а не запрещённого: новый инструмент или новое действие, про которые
/// здесь забыли, считаются записью — и в режиме «только чтение» не исполнятся, а в «спрашивать
/// всё» спросятся. Обратная ошибка (запись, записанная в чтение) молча открывала бы систему.
/// </para>
/// <para>
/// <c>run_powershell</c> решается разбором самого скрипта (<see cref="PowerShellAnalysis"/>).
/// <c>change_rollback snapshot</c> — чтение: он пишет только во внутреннюю папку снимков, и
/// снимок перед правкой нужен как раз тогда, когда писать нельзя. <c>generate_image</c> тоже
/// чтение: он стоит денег, но систему не меняет. <c>init_agent</c> — чтение: вызовы самого агента
/// проходят шлюз сами.
/// </para>
/// </remarks>
internal static class ToolEffects
{
    private static readonly FrozenSet<string> ActionlessReads = new[]
    {
        "read_clipboard", "fetch_image", "analyze_folder", "generate_image", "init_agent", "read_file",
        "read_instruction", "scrape_url", "capture_screenshot", "system_info", "search_web", "wmi_query",
        "youtube_transcript"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Инструменты без действий, которые всегда пишут, — чтобы таблица знала их по имени.</summary>
    private static readonly FrozenSet<string> ActionlessWrites = new[]
    {
        "write_clipboard", "edit_file", "create_folder", "create_document", "edit_document", "save_image"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, FrozenSet<string>> ReadActions =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["change_rollback"] = ["snapshot", "list_snapshots", "snapshot_info", "compare"],
            ["credentials"] = ["list_cmdkey", "list_certs", "expiring_certs"],
            ["devices"] = ["printers", "usb", "drivers", "driver_problems", "pnp_devices"],
            ["disk_management"] = ["list_disks", "list_volumes", "smart_status", "chkdsk_scan", "bitlocker_status"],
            ["disk_space"] = ["analyze", "largest_items"],
            ["dns_config"] = ["resolvers", "hosts_file", "proxy", "test_resolve", "suffix_list"],
            ["event_log"] = ["list_logs", "read"],
            ["filesystem"] = ["read", "list", "exists", "search"],
            ["firewall_rules"] = ["list", "get"],
            ["local_users"] = ["list_users", "list_groups", "group_members", "user_details"],
            ["network"] = ["adapters", "dns", "ping", "traceroute", "connections", "firewall_rules", "wifi_profiles"],
            ["performance"] = ["summary", "cpu", "memory", "disk", "top_processes"],
            ["port_listener"] = ["list_listeners", "find_port", "find_process"],
            ["windows_process"] = ["list", "top"],
            ["registry"] = ["read", "list_subkeys"],
            ["reliability"] = ["stability_records", "crash_dumps", "bsod_info", "recent_failures"],
            ["remote_access"] =
                ["rdp_status", "rdp_sessions", "vpn_connections", "hyperv_switches", "hyperv_nics", "listening_rdp"],
            ["restore_point"] = ["list", "status"],
            ["scheduled_task"] = ["list", "query"],
            ["security_status"] =
                ["firewall_status", "firewall_rules", "defender_status", "defender_threats", "defender_preferences"],
            ["windows_service"] = ["list", "status", "dependencies"],
            ["software_inventory"] = ["list_installed", "search", "list_upgrades"],
            ["startup_programs"] = ["list_all", "wmi", "registry", "folders", "status"],
            ["system_repair"] = ["status_sfc", "status_dism"],
            ["virtualization"] = ["list_hyperv_vms", "hyperv_vm_status", "list_docker_containers", "docker_status"],
            ["windows_features"] = ["list", "get"],
            ["windows_update"] = ["status", "history", "pending", "reboot_required"]
        }.ToFrozenDictionary(
            pair => pair.Key,
            pair => pair.Value.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    public static ToolEffect Classify(string? toolName, JsonElement arguments)
    {
        var tool = (toolName ?? "").Trim();
        if (ActionlessReads.Contains(tool))
        {
            return ToolEffect.Read;
        }

        // PowerShell — по дереву скрипта: только чтение проходит как чтение, остальное (и всё,
        // что разобрать не вышло) — запись. См. PowerShellAnalysis.
        if (tool.Equals("run_powershell", StringComparison.OrdinalIgnoreCase))
        {
            return PowerShellAnalysis.Analyze(arguments).IsWrite ? ToolEffect.Write : ToolEffect.Read;
        }

        return ReadActions.TryGetValue(tool, out var reads) && reads.Contains(DangerousActionGuard.ActionOf(arguments))
            ? ToolEffect.Read
            : ToolEffect.Write;
    }

    /// <summary>Все инструменты, о которых таблица знает, — для проверки её полноты тестом.</summary>
    internal static IEnumerable<string> KnownTools => ActionlessReads.Concat(ActionlessWrites).Concat(ReadActions.Keys);
}
