using System.Runtime.Versioning;
using System.Text.Json;

namespace Amarin.Tools;

[SupportedOSPlatform("windows")]
public sealed class StartupProgramsTool : ITool
{
    public string Name => "startup_programs";
    public string Description =>
        "List Windows autostart programs: WMI startup commands, Run registry keys, Startup folders. Read-only.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list_all", "wmi", "registry", "folders"],
              "description": "Startup listing action"
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

        var action = actionProp.GetString()?.ToLowerInvariant();
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
            : Task.FromResult(PowerShellHelper.Run(script, 120));
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