using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amarin.Tools;

/// <param name="Skipped">
/// Инструмент отказал, потому что на этой машине нет его компонента (Hyper-V, Wi-Fi, модуль
/// PowerShell). Это не поломка программы — на раннере CI таких компонентов нет вовсе.
/// </param>
public sealed record ToolSmokeResult(
    string ToolName,
    bool Success,
    long ElapsedMs,
    string Summary,
    bool Skipped = false)
{
    public string Status => Success ? "OK" : Skipped ? "SKIP" : "FAIL";
}

public static class ToolSmokeRunner
{
    private static readonly (string Name, string Arguments)[] SmokeCases =
    [
        ("run_powershell", """{"command":"Get-Date -Format o"}"""),
        ("registry", """{"action":"read","path":"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion"}"""),
        ("windows_service", """{"action":"list"}"""),
        ("filesystem", """{"action":"exists","path":"C:\\__amarin_smoke_missing__.txt"}"""),
        ("system_info", "{}"),
        ("capture_screenshot", "{}"),
        ("read_clipboard", "{}"),
        ("analyze_folder", """{"path":"C:\\Windows"}"""),
        ("event_log", """{"action":"read","preset":"critical_recent","max_events":5}"""),
        ("network", """{"action":"adapters"}"""),
        ("scheduled_task", """{"action":"list"}"""),
        ("wmi_query", """{"scope":"os"}"""),
        ("windows_process", """{"action":"list"}"""),
        ("virtualization", """{"action":"list_docker_containers"}"""),
        ("reliability", """{"action":"stability_records"}"""),
        ("windows_update", """{"action":"reboot_required"}"""),
        ("security_status", """{"action":"firewall_status"}"""),
        ("devices", """{"action":"pnp_devices"}"""),
        ("dns_config", """{"action":"resolvers"}"""),
        ("port_listener", """{"action":"list_listeners"}"""),
        ("remote_access", """{"action":"rdp_status"}"""),
        ("change_rollback", """{"action":"list_snapshots"}"""),
        ("performance", """{"action":"summary"}"""),
        ("startup_programs", """{"action":"list_all"}"""),
        ("credentials", """{"action":"list_cmdkey"}"""),
        ("system_repair", """{"action":"status_sfc"}"""),
        ("restore_point", """{"action":"status"}"""),
        ("disk_management", """{"action":"list_volumes"}"""),
        ("disk_space", """{"action":"analyze"}"""),
        ("software_inventory", """{"action":"list_installed"}"""),
        ("firewall_rules", """{"action":"list","filter":"enabled"}"""),
        ("windows_features", """{"action":"list","filter":"enabled"}"""),
        ("local_users", """{"action":"list_users"}""")
    ];

    public static async Task<IReadOnlyList<ToolSmokeResult>> RunAsync(
        ToolRegistry registry,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ToolSmokeResult>(SmokeCases.Length);

        foreach (var (name, argumentsJson) in SmokeCases)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!registry.All.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(new ToolSmokeResult(name, false, 0, "Tool not registered"));
                continue;
            }

            if (name == "read_clipboard")
            {
                SeedEmptyClipboard();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            var sw = Stopwatch.StartNew();
            try
            {
                var args = JsonDocument.Parse(argumentsJson).RootElement.Clone();
                var result = await registry.ExecuteAsync(name, args, timeoutCts.Token);
                sw.Stop();
                results.Add(new ToolSmokeResult(
                    name,
                    result.Success,
                    sw.ElapsedMilliseconds,
                    Summarize(result.Output),
                    !result.Success && IsUnavailable(result.Output)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                sw.Stop();
                results.Add(new ToolSmokeResult(name, false, sw.ElapsedMilliseconds, "Timeout (3 min)"));
            }
            catch (Exception ex)
            {
                sw.Stop();
                results.Add(new ToolSmokeResult(name, false, sw.ElapsedMilliseconds, ex.Message));
            }
        }

        return results;
    }

    /// <summary>Метка, которую прогон кладёт в пустой буфер обмена.</summary>
    internal const string ClipboardSeed = "Amarin smoke test";

    /// <summary>
    /// Пустой буфер обмена — не поломка, а состояние машины: на свежем раннере CI в нём нет
    /// ничего, и чтение отказывало бы всегда. Поэтому в совсем пустой буфер (ни текста, ни
    /// файлов, ни картинки) кладётся метка — тогда чтение проверяется по-настоящему. Непустой
    /// не трогается: прогон запускают и люди на своих машинах, и чужое содержимое буфера
    /// затирать нельзя.
    /// </summary>
    private static void SeedEmptyClipboard()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            StaThread.Run(() =>
                !ClipboardNative.HasText() && !ClipboardNative.HasFiles() && !ClipboardNative.HasImage() &&
                ClipboardNative.TrySetText(ClipboardSeed));
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Runtime.InteropServices.ExternalException)
        {
            // Не вышло — чтение просто покажет, что буфер пуст.
        }
    }

    /// <summary>
    /// Признаки «компонента на этой машине нет». Смотрятся только у отказавших вызовов: в удачном
    /// выводе те же слова (скажем, «Hyper-V» в списке компонентов) ничего не значат.
    /// </summary>
    /// <remarks>
    /// Список — по тому, что отвечают Windows и PowerShell на английской и русской системе; он
    /// заведомо неполон, и неузнанный отказ остаётся FAIL — лучше лишний красный, чем
    /// спрятанная поломка.
    /// </remarks>
    private static readonly string[] UnavailableMarkers =
    [
        "not installed",
        "не установлен",
        "is not recognized as",
        "не распознано как",
        "wlansvc",
        "wireless autoconfig",
        "hyper-v",
        "feature is not available",
        "not supported on this",
        "не поддерживается",

        // Docker установлен, но его движок не запущен: на раннерах CI так бывает через раз.
        "docker_engine",
        "is the docker daemon running"
    ];

    internal static bool IsUnavailable(string? output) =>
        !string.IsNullOrWhiteSpace(output) &&
        UnavailableMarkers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>Итог прогона Markdown-таблицей — для файла <c>--smoke-report</c> и сводки шага CI.</summary>
    public static string FormatReport(IReadOnlyList<ToolSmokeResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var sb = new StringBuilder();
        sb.AppendLine("### Tool smoke test").AppendLine();
        sb.AppendLine("| Status | Tool | ms | Output |");
        sb.AppendLine("|---|---|---:|---|");
        foreach (var result in results)
        {
            sb.Append("| ").Append(result.Status)
              .Append(" | `").Append(result.ToolName).Append('`')
              .Append(" | ").Append(result.ElapsedMs.ToString(CultureInfo.InvariantCulture))
              .Append(" | ").Append(Cell(result.Summary))
              .AppendLine(" |");
        }

        sb.AppendLine().AppendLine(Totals(results));
        return sb.ToString();
    }

    public static string Totals(IReadOnlyList<ToolSmokeResult> results) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"Total: {results.Count(r => r.Success)} OK, {results.Count(r => r.Skipped)} SKIP, {results.Count(r => !r.Success && !r.Skipped)} FAIL of {results.Count}.");

    /// <summary>Итог процесса: пропуски — не поломка, отказ — да.</summary>
    public static int ExitCode(IReadOnlyList<ToolSmokeResult> results) =>
        results.Any(r => !r.Success && !r.Skipped) ? 1 : 0;

    // Вертикальная черта разорвала бы строку таблицы, перевод строки — таблицу целиком.
    private static string Cell(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');

    private static string Summarize(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "empty output";
        }

        var line = output.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return line.Length <= 100 ? line : line[..100] + "…";
    }
}