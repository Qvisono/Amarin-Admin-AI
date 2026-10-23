using System.Runtime.Versioning;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class StartupProgramsTool : ITool
{
    private const int AuditTimeoutSeconds = 180;

    public string Name => "startup_programs";
    public string Description =>
        "Windows autostart programs: list them (status shows each item's location, name and whether it is " +
        "enabled) and disable or enable an item the way Task Manager does - the entry itself is kept. " +
        "audit checks everything that starts automatically (services, scheduled tasks, Run keys, startup " +
        "folders, WMI subscriptions, browser extensions) and lists only the risks: files without a valid " +
        "signature or in places an ordinary user can write to, extensions not from the store. " +
        "disable/enable require user confirmation.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_all", "wmi", "registry", "folders", "status", "audit", "disable", "enable"],
              "description": "Startup action"
            },
            "name": {
              "type": "string",
              "description": "For disable/enable: the item name exactly as the status action shows it"
            },
            "location": {
              "type": "string",
              "enum": ["hkcu_run", "hklm_run", "hklm_run32", "user_folder", "common_folder"],
              "description": "For disable/enable: the item location as the status action shows it"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("action", out var actionProp))
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
        }

        var action = actionProp.GetString()?.Trim().ToLowerInvariant();
        switch (action)
        {
            case "status":
                return Task.FromResult(ToolResult.Ok(StartupApproval.Format(StartupApproval.List())));
            case "audit":
                return AuditAsync(
                    () => ToolResult.Ok(AutostartAudit.ForThisMachine().Run(cancellationToken)),
                    TimeSpan.FromSeconds(AuditTimeoutSeconds),
                    cancellationToken);
            case "disable" or "enable":
                return Task.FromResult(SetEnabled(arguments, enable: action == "enable"));
        }

        var script = action switch
        {
            "list_all" => ListAllScript(),
            "wmi" => WmiScript(),
            "registry" => RegistryScript(),
            "folders" => FoldersScript(),
            _ => null
        };

        return script is null
            ? Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            : PowerShellHelper.RunAsync(script, 120, cancellationToken);
    }

    /// <summary>
    /// Аудит — на рабочем потоке (сотни подписей и списков доступа), со сроком и с ответом на
    /// любую ошибку.
    /// </summary>
    /// <remarks>
    /// Запросы к WMI и Планировщику ждут свою службу сколько угодно: без срока зависшая служба
    /// держала бы ход агента до перезапуска программы. А исключение из рабочего потока, не
    /// пойманное по источникам, уронило бы весь шаг агента вместо ответа инструмента. Срок больше,
    /// чем у прочих действий (120 с): аудит проверяет ещё и каждый файл.
    /// </remarks>
    internal static async Task<ToolResult> AuditAsync(
        Func<ToolResult> audit, TimeSpan limit, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(audit, cancellationToken).WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Startup.AuditTimeout", (int)limit.TotalSeconds));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ToolResult.Fail($"Autostart audit error: {ex.Message}");
        }
    }

    private static ToolResult SetEnabled(JsonElement arguments, bool enable)
    {
        var name = arguments.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString()?.Trim()
            : null;
        var locationText = arguments.TryGetProperty("location", out var locationProp) &&
                           locationProp.ValueKind == JsonValueKind.String
            ? locationProp.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(name) || !StartupApproval.TryLocation(locationText, out var location))
        {
            return ToolResult.Fail(
                "disable/enable need name and location exactly as the status action shows them. Call status first.");
        }

        return StartupApproval.SetEnabled(location, name, enable, DateTime.UtcNow);
    }

    private static string ListAllScript() => """
        '=== WMI Startup Commands ==='
        Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue |
          Select-Object Name, Command, Location, User | Format-Table -Wrap -AutoSize
        ''
        '=== Registry Run Keys ==='
        $paths = @(
          'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
          'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
          'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
        )
        foreach ($p in $paths) {
          if (Test-Path $p) {
            "--- $p ---"
            Get-ItemProperty $p -ErrorAction SilentlyContinue |
              Get-Member -MemberType NoteProperty |
              Where-Object { $_.Name -notmatch '^PS' } |
              ForEach-Object {
                $n = $_.Name; $v = (Get-ItemProperty $p).$n
                [PSCustomObject]@{ Name=$n; Command=[string]$v }
              } | Format-Table -Wrap -AutoSize
          }
        }
        ''
        '=== Startup Folders ==='
        $folders = @(
          [Environment]::GetFolderPath('CommonStartup'),
          [Environment]::GetFolderPath('Startup')
        )
        foreach ($f in $folders) {
          "--- $f ---"
          if (Test-Path $f) { Get-ChildItem $f -ErrorAction SilentlyContinue | Select-Object Name, FullName }
        }
        """;

    private static string WmiScript() => """
        Get-CimInstance Win32_StartupCommand -ErrorAction SilentlyContinue |
          Select-Object Name, Command, Location, User | Format-Table -Wrap -AutoSize
        """;

    private static string RegistryScript() => """
        $paths = @(
          'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
          'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run',
          'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
        )
        foreach ($p in $paths) {
          if (Test-Path $p) {
            "--- $p ---"
            Get-ItemProperty $p -ErrorAction SilentlyContinue |
              Get-Member -MemberType NoteProperty |
              Where-Object { $_.Name -notmatch '^PS' } |
              ForEach-Object {
                $n = $_.Name; $v = (Get-ItemProperty $p).$n
                [PSCustomObject]@{ Name=$n; Command=[string]$v }
              } | Format-Table -Wrap -AutoSize
          }
        }
        """;

    private static string FoldersScript() => """
        $folders = @(
          [Environment]::GetFolderPath('CommonStartup'),
          [Environment]::GetFolderPath('Startup')
        )
        foreach ($f in $folders) {
          "--- $f ---"
          if (Test-Path $f) {
            Get-ChildItem $f -ErrorAction SilentlyContinue | Select-Object Name, FullName, LastWriteTime
          } else { '(not found)' }
        }
        """;
}