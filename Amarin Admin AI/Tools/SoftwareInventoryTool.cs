using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>
/// Installed software inventory (registry Uninstall keys) and winget search/install/upgrade/uninstall.
/// Does not use Win32_Product.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SoftwareInventoryTool : ITool
{
    private static readonly Regex PackageIdPattern = new(
        @"^[A-Za-z0-9 ._+-]+$",
        RegexOptions.Compiled);

    // Free-text query for winget search only — no shell metacharacters.
    private static readonly Regex QuerySafePattern = new(
        @"^[\w .+\-@#()/\\,:&']{1,120}$",
        RegexOptions.Compiled);

    public string Name => "software_inventory";

    public string Description =>
        "List installed programs (registry Uninstall keys — not Win32_Product), search/list upgrades via winget, " +
        "and install/upgrade/uninstall packages. Prefer this over winget through run_powershell.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": [
                "list_installed", "search", "list_upgrades",
                "install", "upgrade", "upgrade_all", "uninstall"
              ],
              "description": "Software inventory action"
            },
            "query": {
              "type": "string",
              "description": "Search text for search action; optional filter for list_installed"
            },
            "package_id": {
              "type": "string",
              "description": "winget package id for install/upgrade/uninstall (e.g. Vendor.App)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!arguments.TryGetProperty("action", out var actionProp))
            {
                return Task.FromResult(ToolResult.Fail("Missing required parameter: action"));
            }

            var action = actionProp.GetString()?.ToLowerInvariant();
            return action switch
            {
                "list_installed" => Task.FromResult(ListInstalled(arguments)),
                "search" => Task.FromResult(WingetSearch(arguments)),
                "list_upgrades" => Task.FromResult(WingetListUpgrades()),
                "install" => Task.FromResult(WingetMutate("install", arguments)),
                "upgrade" => Task.FromResult(WingetMutate("upgrade", arguments)),
                "upgrade_all" => Task.FromResult(WingetUpgradeAll()),
                "uninstall" => Task.FromResult(WingetMutate("uninstall", arguments)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Software inventory error: {ex.Message}"));
        }
    }

    private static ToolResult ListInstalled(JsonElement arguments)
    {
        string? filter = null;
        if (arguments.TryGetProperty("query", out var q) &&
            q.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(q.GetString()))
        {
            filter = q.GetString()!.Trim();
        }

        var rows = new List<(string Name, string Version, string Publisher, string Date, string Hive)>();

        CollectUninstall(Registry.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM", rows);
        CollectUninstall(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM-WOW64", rows);
        CollectUninstall(Registry.CurrentUser,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "HKCU", rows);
        CollectUninstall(Registry.CurrentUser,
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "HKCU-WOW64", rows);

        IEnumerable<(string Name, string Version, string Publisher, string Date, string Hive)> query = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .GroupBy(r => r.Name + "|" + r.Version, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(r =>
                r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Publisher.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                r.Version.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        var list = query.Take(400).ToList();
        if (list.Count == 0)
        {
            return ToolResult.Ok(filter is null
                ? "Установленных программ не найдено в ключах Uninstall."
                : $"Нет совпадений по filter «{filter}».");
        }

        var sb = new StringBuilder();
        sb.AppendLine("Name | Version | Publisher | InstallDate | Hive");
        sb.AppendLine(new string('-', 80));
        foreach (var r in list)
        {
            sb.AppendLine($"{r.Name} | {r.Version} | {r.Publisher} | {r.Date} | {r.Hive}");
        }

        sb.AppendLine();
        sb.AppendLine($"Всего (показано до 400): {list.Count}. Источник: реестр Uninstall (не Win32_Product).");
        return ToolResult.Ok(Truncate(sb.ToString().TrimEnd(), 4000));
    }

    private static void CollectUninstall(
        RegistryKey hive,
        string subKey,
        string hiveLabel,
        List<(string Name, string Version, string Publisher, string Date, string Hive)> rows)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey, writable: false);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                try
                {
                    using var item = key.OpenSubKey(name, writable: false);
                    if (item is null)
                    {
                        continue;
                    }

                    // Skip system components without display name
                    var displayName = item.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var systemComponent = item.GetValue("SystemComponent");
                    if (systemComponent is int sc && sc == 1)
                    {
                        continue;
                    }

                    var version = item.GetValue("DisplayVersion") as string ?? "";
                    var publisher = item.GetValue("Publisher") as string ?? "";
                    var date = item.GetValue("InstallDate") as string ?? "";
                    rows.Add((displayName.Trim(), version.Trim(), publisher.Trim(), date.Trim(), hiveLabel));
                }
                catch
                {
                    // ignore individual key errors
                }
            }
        }
        catch
        {
            // hive may be inaccessible
        }
    }

    private static ToolResult WingetSearch(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("query", out var q) ||
            q.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(q.GetString()))
        {
            return ToolResult.Fail("search requires query");
        }

        var query = q.GetString()!.Trim();
        if (!QuerySafePattern.IsMatch(query))
        {
            return ToolResult.Fail(
                "query содержит недопустимые символы. Разрешены буквы/цифры и ._+-@#()/\\,:&' (до 120 символов).");
        }

        if (!TryFindWinget(out var wingetPath, out var hint))
        {
            return ToolResult.Fail(hint);
        }

        // query is validated; pass as single argument — no shell string concat of free text into powershell.
        return RunWinget(wingetPath, ["search", query, "--disable-interactivity"], 120);
    }

    private static ToolResult WingetListUpgrades()
    {
        if (!TryFindWinget(out var wingetPath, out var hint))
        {
            return ToolResult.Fail(hint);
        }

        return RunWinget(wingetPath, ["upgrade", "--disable-interactivity"], 180);
    }

    private static ToolResult WingetUpgradeAll()
    {
        if (!TryFindWinget(out var wingetPath, out var hint))
        {
            return ToolResult.Fail(hint);
        }

        return RunWinget(
            wingetPath,
            [
                "upgrade", "--all",
                "--silent",
                "--accept-package-agreements",
                "--accept-source-agreements",
                "--disable-interactivity"
            ],
            600);
    }

    private static ToolResult WingetMutate(string verb, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("package_id", out var idProp) ||
            idProp.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idProp.GetString()))
        {
            return ToolResult.Fail($"package_id is required for {verb}");
        }

        var packageId = idProp.GetString()!.Trim();
        if (!PackageIdPattern.IsMatch(packageId) || packageId.Length > 128)
        {
            return ToolResult.Fail(
                "package_id invalid. Allowed: [A-Za-z0-9 ._+-], max 128 chars (e.g. Vendor.App).");
        }

        if (!TryFindWinget(out var wingetPath, out var hint))
        {
            return ToolResult.Fail(hint);
        }

        // Only validated package_id enters argv — never free model command text.
        var args = new List<string>
        {
            verb,
            "--id", packageId,
            "--exact",
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity"
        };

        return RunWinget(wingetPath, args, 600);
    }

    private static bool TryFindWinget(out string path, out string failMessage)
    {
        path = string.Empty;
        failMessage = string.Empty;

        // Prefer PATH resolution
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "winget",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10_000);
                var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(l => l.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(first) && File.Exists(first.Trim()))
                {
                    path = first.Trim();
                    return true;
                }
            }
        }
        catch
        {
            // continue fallbacks
        }

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
            Path.Combine(localApp, @"Microsoft\WindowsApps\winget.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe")
        };

        foreach (var c in candidates)
        {
            if (c.Contains('*', StringComparison.Ordinal))
            {
                try
                {
                    var dir = Path.GetDirectoryName(c);
                    var parent = Path.GetDirectoryName(dir);
                    var pattern = Path.GetFileName(dir);
                    if (parent is not null && Directory.Exists(parent) && pattern is not null)
                    {
                        foreach (var d in Directory.GetDirectories(parent, pattern))
                        {
                            var w = Path.Combine(d, "winget.exe");
                            if (File.Exists(w))
                            {
                                path = w;
                                return true;
                            }
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }
            else if (File.Exists(c))
            {
                path = c;
                return true;
            }
        }

        failMessage =
            "winget недоступен. Установите или обновите «App Installer» из Microsoft Store " +
            "(Microsoft.DesktopAppInstaller), затем повторите. " +
            "Либо: winget из https://aka.ms/getwinget";
        return false;
    }

    private static ToolResult RunWinget(string wingetPath, IReadOnlyList<string> args, int timeoutSeconds)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 600);

        var psi = new ProcessStartInfo
        {
            FileName = wingetPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        // Prefer non-interactive progress for clean stdout
        psi.Environment["WINGET_DISABLE_INTERACTIVITY"] = "1";

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return ToolResult.Fail("Не удалось запустить winget.");
            }

            var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

            if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return ToolResult.Fail($"winget timed out after {timeoutSeconds} seconds.");
            }

            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(10));
            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            // Strip ANSI / progress noise
            stdout = StripAnsi(stdout);
            stderr = StripAnsi(stderr);

            var sb = new StringBuilder();
            sb.AppendLine($"Exit code: {process.ExitCode}");
            sb.AppendLine($"Command: winget {string.Join(' ', args)}");
            if (stdout.Length > 0)
            {
                sb.AppendLine("--- stdout ---");
                sb.Append(Truncate(stdout, 4000));
            }

            if (stderr.Length > 0)
            {
                sb.AppendLine("--- stderr ---");
                sb.Append(Truncate(stderr, 1500));
            }

            var text = sb.ToString().TrimEnd();
            // winget often returns non-zero for "no upgrades" etc. — still surface output.
            // Treat 0 as Ok; known soft codes with useful stdout as Ok if stdout present.
            if (process.ExitCode == 0)
            {
                return ToolResult.Ok(text);
            }

            if (!string.IsNullOrWhiteSpace(stdout) &&
                (stdout.Contains("No installed package", StringComparison.OrdinalIgnoreCase) ||
                 stdout.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase) ||
                 stdout.Contains("No package found", StringComparison.OrdinalIgnoreCase) ||
                 stdout.Contains("No installed package found", StringComparison.OrdinalIgnoreCase)))
            {
                return ToolResult.Ok(text);
            }

            return ToolResult.Fail(text);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"winget error: {ex.Message}");
        }
    }

    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return Regex.Replace(text, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n... [показаны первые " + max + " символов]";
}
