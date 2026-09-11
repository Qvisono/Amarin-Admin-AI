using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Installed software inventory (registry Uninstall keys) and winget search/install/upgrade/uninstall.
/// Does not use Win32_Product.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class SoftwareInventoryTool : ITool
{
    private static string? _wingetPath;
    private static string? _wingetFailMessage;
    private static int _wingetState;

    [GeneratedRegex(@"^[A-Za-z0-9 ._+-]+$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex(@"^[\w .+\-@#()/\\,:&']{1,120}$")]
    private static partial Regex QuerySafePattern();

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscape();

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
                "install" => Task.FromResult(MutateAndInvalidate(() => WingetMutate("install", arguments))),
                "upgrade" => Task.FromResult(MutateAndInvalidate(() => WingetMutate("upgrade", arguments))),
                "upgrade_all" => Task.FromResult(MutateAndInvalidate(WingetUpgradeAll)),
                "uninstall" => Task.FromResult(MutateAndInvalidate(() => WingetMutate("uninstall", arguments))),
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

        var list = InstalledProgramsCatalog.Query(filter, max: 400);
        if (list.Count == 0)
        {
            return ToolResult.Ok(filter is null
                ? "Установленных программ не найдено в ключах Uninstall."
                : $"Нет совпадений по filter «{filter}».");
        }

        return ToolResult.Ok(Truncate(InstalledProgramsCatalog.FormatTable(list, 400), 4000));
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
        if (!QuerySafePattern().IsMatch(query))
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
        if (!PackageIdPattern().IsMatch(packageId) || packageId.Length > 128)
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

    private static ToolResult MutateAndInvalidate(Func<ToolResult> action)
    {
        var result = action();
        if (result.Success)
        {
            InstalledProgramsCatalog.Invalidate();
        }

        return result;
    }

    private static bool TryFindWinget(out string path, out string failMessage)
    {
        if (_wingetState == 1 && _wingetPath is not null)
        {
            path = _wingetPath;
            failMessage = string.Empty;
            return true;
        }

        if (_wingetState == 2)
        {
            path = string.Empty;
            failMessage = _wingetFailMessage ?? "winget недоступен.";
            return false;
        }

        if (TryResolveWinget(out path, out failMessage))
        {
            _wingetPath = path;
            _wingetState = 1;
            return true;
        }

        _wingetFailMessage = failMessage;
        _wingetState = 2;
        return false;
    }

    private static bool TryResolveWinget(out string path, out string failMessage)
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
                    // Каталог из PATH может быть недоступен — ищем дальше в следующем.
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

        return AnsiEscape().Replace(text, string.Empty);
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n... [показаны первые " + max + " символов]";
}
