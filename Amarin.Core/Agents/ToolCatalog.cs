namespace Amarin.Core;

/// <summary>
/// Какие инструменты есть у чата и у агента — для страницы «Безопасность», где их выключают.
/// </summary>
/// <remarks>
/// Список, а не обход живых реестров: инструменты собираются в <c>Program</c> и в
/// <c>AgentTools.Create</c> со своими клиентами и ключами, и строить их ради подписей на странице
/// незачем. Полноту сверяет тест с тем, что <c>AgentTools.Create</c> действительно выдаёт.
/// Выключают по имени, поэтому инструмент, который есть у обоих (поиск, загрузка картинок),
/// показан один раз — в группе чата, с пометкой.
/// </remarks>
internal static class ToolCatalog
{
    public static IReadOnlyList<string> Chat { get; } =
    [
        "read_file", "write_file", "search_web", "generate_image", "fetch_image", "youtube_transcript",
        "init_agent", "read_instruction"
    ];

    /// <summary>Только агентские — без тех, что уже есть у чата.</summary>
    public static IReadOnlyList<string> Agent { get; } =
    [
        "run_powershell", "registry", "windows_service", "filesystem", "system_info", "download_file",
        "capture_screenshot", "read_clipboard", "write_clipboard", "analyze_folder", "scrape_url", "event_log", "network",
        "scheduled_task", "wmi_query", "windows_process", "virtualization", "reliability", "windows_update",
        "security_status", "devices", "dns_config", "port_listener", "remote_access", "change_rollback",
        "performance", "startup_programs", "credentials", "system_repair", "restore_point", "disk_management",
        "disk_space", "software_inventory", "firewall_rules", "windows_features", "local_users"
    ];

    /// <summary>Инструменты, которые есть и у чата, и у агента.</summary>
    public static IReadOnlySet<string> Shared { get; } =
        new HashSet<string>(["search_web", "fetch_image"], StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> All => Chat.Concat(Agent);

    /// <summary>Ключ человеческой подписи инструмента в словаре.</summary>
    public static string LabelKey(string tool) => "S.Tool." + tool;
}
