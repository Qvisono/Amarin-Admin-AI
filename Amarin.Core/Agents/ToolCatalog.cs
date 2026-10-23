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
        "read_file", "write_file", "edit_file", "create_folder", "create_document", "edit_document", "save_image",
        "analyze_table", "recognize_text", "search_web", "generate_image", "fetch_image", "youtube_transcript", "init_agent", "read_instruction", "deferred_task"
    ];

    /// <summary>Только агентские — без тех, что уже есть у чата.</summary>
    public static IReadOnlyList<string> Agent { get; } =
    [
        "run_powershell", "registry", "windows_service", "filesystem", "system_info", "download_file",
        "capture_screenshot", "read_clipboard", "write_clipboard", "analyze_folder", "scrape_url", "event_log", "network",
        "scheduled_task", "wmi_query", "windows_process", "virtualization", "reliability", "windows_update",
        "security_status", "devices", "dns_config", "port_listener", "remote_access", "change_rollback",
        "performance", "startup_programs", "credentials", "system_repair", "restore_point", "disk_management",
        "disk_space", "software_inventory", "firewall_rules", "windows_features", "local_users",
        "audio", "power", "file_locks", "permissions", "environment", "store_apps", "print_queue", "shares",
        "bluetooth", "perf_record", "network_quality"
    ];

    /// <summary>Инструменты, которые есть и у чата, и у агента.</summary>
    public static IReadOnlySet<string> Shared { get; } =
        new HashSet<string>(["search_web", "fetch_image"], StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<string> All => Chat.Concat(Agent);

    /// <summary>
    /// Инструменты без своего переключателя: включаются и выключаются вместе с названным (1.36.0).
    /// </summary>
    /// <remarks>
    /// Разговор с агентом и его подъём после отказа — продолжение <c>init_agent</c>: человек,
    /// выключивший агента, не ждёт, что чат продолжит гонять его другим инструментом. Отдельная
    /// строка на странице «Безопасность» значила бы, что агента можно выключить наполовину.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> SwitchOf { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["message_agent"] = "init_agent",
            ["agent_refused"] = "init_agent"
        };

    /// <summary>Имя, под которым инструмент выключают: своё или того, с кем он ходит.</summary>
    public static string SwitchName(string tool) => SwitchOf.TryGetValue(tool.Trim(), out var owner) ? owner : tool.Trim();

    /// <summary>
    /// Вызов, в котором работает агент: запуск, сообщение ему и подъём после отказа. Строка в ленте
    /// у них одна — со значком модели агента.
    /// </summary>
    public static bool IsAgentCall(string tool) =>
        SwitchName(tool).Equals(Tools.InitAgentTool.ToolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ключ человеческой подписи инструмента в словаре.</summary>
    public static string LabelKey(string tool) => "S.Tool." + tool;
}
