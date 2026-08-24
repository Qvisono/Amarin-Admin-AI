using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Disk space analysis and safe cleanup of fixed categories only (never arbitrary paths on cleanup).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DiskSpaceTool : ITool
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "recycle_bin",
        "temp_files",
        "windows_update_cache",
        "memory_dumps",
        "thumbnails"
    };

    [GeneratedRegex(@"^[A-Za-z]:\\(?:[^<>:""|?*\x00-\x1F]+\\)*[^<>:""|?*\x00-\x1F]*$")]
    private static partial Regex SafePathChars();

    public string Name => "disk_space";

    public string Description =>
        "Analyze free/used disk space, find largest items under a path, and clean fixed safe categories " +
        "(recycle bin, temp, WU cache, dumps, thumbnails). cleanup ignores path — only hardcoded locations.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["analyze", "largest_items", "cleanup"],
              "description": "Disk space action"
            },
            "path": {
              "type": "string",
              "description": "Root path for largest_items only (ignored by cleanup)"
            },
            "top": {
              "type": "integer",
              "description": "Top N items for largest_items (default 20, max 50)"
            },
            "categories": {
              "type": "array",
              "items": {
                "type": "string",
                "enum": [
                  "recycle_bin", "temp_files", "windows_update_cache",
                  "memory_dumps", "thumbnails"
                ]
              },
              "description": "Cleanup categories (cleanup only)"
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
                "analyze" => Task.FromResult(Analyze()),
                "largest_items" => Task.FromResult(LargestItems(arguments)),
                "cleanup" => Task.FromResult(Cleanup(arguments)),
                _ => Task.FromResult(ToolResult.Fail($"Unknown action: {action}"))
            };
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"Disk space error: {ex.Message}"));
        }
    }

    private static ToolResult LargestItems(JsonElement arguments)
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (arguments.TryGetProperty("path", out var pathProp) &&
            pathProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(pathProp.GetString()))
        {
            path = pathProp.GetString()!.Trim();
        }

        if (path.Length >= 2 && path[1] == ':' && path.Length == 2)
        {
            path += "\\";
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return ToolResult.Fail($"Path not found: {path}");
        }

        // Reject obvious injection / alternate data streams abuse in PS string.
        if (path.Contains('\'', StringComparison.Ordinal) ||
            path.Contains('`', StringComparison.Ordinal) ||
            path.Contains('\n') ||
            path.Contains('\r') ||
            !SafePathChars().IsMatch(path.TrimEnd('\\')))
        {
            // Allow root like C:\
            var isRoot = path.Length is 3 && path[1] == ':' && path[2] == '\\';
            if (!isRoot)
            {
                return ToolResult.Fail("path contains unsupported characters");
            }
        }

        var top = GetInt(arguments, "top", 20, 1, 50);
        return LargestItemsScan(path, top);
    }

    private static ToolResult Analyze()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Volumes (free / used) ===");
        sb.AppendLine("Drive Letter Label Format SizeGB FreeGB UsedGB UsedPct");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            try
            {
                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = total - free;
                var pct = total > 0 ? 100.0 * used / total : 0;
                sb.AppendLine(
                    $"{drive.Name.TrimEnd('\\')} {drive.DriveType} {drive.VolumeLabel} {drive.DriveFormat} " +
                    $"{Gb(total):0.##} {Gb(free):0.##} {Gb(used):0.##} {pct:0.#}");
            }
            catch
            {
                // skip
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== Common reclaimable locations (GB) ===");
        sb.AppendLine("Location Path SizeGB");

        var tempUser = Environment.GetEnvironmentVariable("TEMP")
                       ?? Environment.GetEnvironmentVariable("TMP")
                       ?? "";
        var winTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var wuCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download");
        var minidump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
        var memdump = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
        var thumb = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Microsoft\Windows\Explorer");

        AppendLocation(sb, "RecycleBin", @"shell:RecycleBinFolder", RecycleBinBytes());
        AppendLocation(sb, "UserTemp", tempUser, DirSizeBytes(tempUser, 5_000));
        AppendLocation(sb, "WindowsTemp", winTemp, DirSizeBytes(winTemp, 5_000));
        AppendLocation(sb, "WindowsUpdateCache", wuCache, DirSizeBytes(wuCache, 8_000));
        AppendLocation(sb, "Minidump", minidump, DirSizeBytes(minidump, 2_000));
        AppendLocation(sb, "MemoryDmp", memdump, FileSizeBytes(memdump));
        AppendLocation(sb, "Thumbnails", thumb, ThumbCacheBytes(thumb));

        sb.AppendLine();
        sb.AppendLine("cleanup categories: recycle_bin, temp_files, windows_update_cache, memory_dumps, thumbnails");
        return ToolResult.Ok(Truncate(sb.ToString().TrimEnd(), 4000));
    }

    private static ToolResult LargestItemsScan(string path, int top)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== largest_items under: {path} (top {top}, depth<=4, timeout ~90s) ===");

        var deadline = DateTime.UtcNow.AddSeconds(90);
        var items = new List<(string Kind, string Path, long Size, string Note)>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((path, 0));

        if (File.Exists(path) && !Directory.Exists(path))
        {
            try
            {
                items.Add(("file", path, new FileInfo(path).Length, ""));
            }
            catch
            {
                // skip
            }
        }

        while (queue.Count > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                sb.AppendLine("Scan time limit reached; results may be partial.");
                break;
            }

            var (current, depth) = queue.Dequeue();
            DirectoryInfo dirInfo;
            try
            {
                dirInfo = new DirectoryInfo(current);
                if (!dirInfo.Exists)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var entry in dirInfo.EnumerateFileSystemInfos())
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        break;
                    }

                    try
                    {
                        if (entry is DirectoryInfo sub)
                        {
                            long size = 0;
                            try
                            {
                                size = sub.EnumerateFiles().Sum(f =>
                                {
                                    try { return f.Length; }
                                    catch { return 0L; }
                                });
                            }
                            catch
                            {
                                size = 0;
                            }

                            items.Add(("dir", sub.FullName, size, "immediate-files-only"));
                            if (depth + 1 <= 4)
                            {
                                queue.Enqueue((sub.FullName, depth + 1));
                            }
                        }
                        else if (entry is FileInfo file)
                        {
                            items.Add(("file", file.FullName, file.Length, ""));
                        }
                    }
                    catch
                    {
                        // skip entry
                    }
                }
            }
            catch
            {
                // skip directory
            }
        }

        foreach (var item in items.OrderByDescending(i => i.Size).Take(top))
        {
            sb.AppendLine(
                $"{item.Kind} {item.Size / (1024.0 * 1024.0):0.#} MB {Gb(item.Size):0.###} GB {item.Path} {item.Note}");
        }

        sb.AppendLine($"Items scanned (listed): {items.Count}");
        return ToolResult.Ok(Truncate(sb.ToString().TrimEnd(), 4000));
    }

    private static void AppendLocation(StringBuilder sb, string name, string path, long? bytes)
    {
        var size = bytes is null ? "n/a" : Gb(bytes.Value).ToString("0.###");
        sb.AppendLine($"{name} {path} {size}");
    }

    private static long? RecycleBinBytes()
    {
        long total = 0;
        var any = false;
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
        {
            var rb = Path.Combine(drive.Name, "$Recycle.Bin");
            var size = DirSizeBytes(rb, 3_000);
            if (size is null)
            {
                continue;
            }

            any = true;
            total += size.Value;
        }

        return any ? total : null;
    }

    private static long? ThumbCacheBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("thumbcache_*.db")
                .Sum(f => f.Length);
        }
        catch
        {
            return null;
        }
    }

    private static long? FileSizeBytes(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private static long? DirSizeBytes(string path, int timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return null;
        }

        var sw = Stopwatch.StartNew();
        long sum = 0;
        try
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    return sum;
                }

                try
                {
                    sum += file.Length;
                }
                catch
                {
                    // skip locked
                }
            }
        }
        catch
        {
            return sum;
        }

        return sum;
    }

    private static double Gb(long bytes) => bytes / 1073741824.0;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n... [truncated]";

    private static ToolResult Cleanup(JsonElement arguments)
    {
        // path must not affect cleanup — refuse if model tries free-form path cleanup.
        if (arguments.TryGetProperty("path", out var pathProp) &&
            pathProp.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(pathProp.GetString()))
        {
            // Soft note only: path is ignored, not a hard fail — still refuse arbitrary deletion.
        }

        var categories = ParseCategories(arguments);
        if (categories.Count == 0)
        {
            return ToolResult.Fail(
                "cleanup requires categories: recycle_bin, temp_files, windows_update_cache, memory_dumps, thumbnails. " +
                "path is ignored — only fixed safe locations are cleaned.");
        }

        // Only whitelist enum values reach the script.
        var catsLiteral = string.Join(",", categories.Select(c => "'" + c + "'"));
        return PowerShellHelper.Run(CleanupScript(catsLiteral), 300, maxOutput: 4000);
    }

    private static List<string> ParseCategories(JsonElement arguments)
    {
        var list = new List<string>();
        if (!arguments.TryGetProperty("categories", out var cats) ||
            cats.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in cats.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = item.GetString()?.Trim().ToLowerInvariant();
            if (name is not null && AllowedCategories.Contains(name) && !list.Contains(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    private static string CleanupScript(string categoriesCsv) => $$"""
        # categories is a fixed whitelist from C# — never free-form paths from the model.
        $categories = @({{categoriesCsv}})
        Write-Output '=== disk_space cleanup (fixed paths only; path parameter ignored) ==='
        Write-Output ("Categories: " + ($categories -join ', '))

        function Get-DirSizeBytes([string]$p) {
          if (-not (Test-Path -LiteralPath $p)) { return 0 }
          try {
            $sum = (Get-ChildItem -LiteralPath $p -Force -Recurse -ErrorAction SilentlyContinue |
              Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum
            if ($null -eq $sum) { return 0 }
            return [int64]$sum
          } catch { return 0 }
        }

        function Remove-OldFiles([string]$dir, [int]$olderHours) {
          if (-not (Test-Path -LiteralPath $dir)) {
            Write-Output "  skip (missing): $dir"
            return 0
          }
          $cutoff = (Get-Date).AddHours(-$olderHours)
          $freed = 0L
          Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try {
              if ($_.PSIsContainer) {
                if ($_.LastWriteTime -lt $cutoff) {
                  $sz = Get-DirSizeBytes $_.FullName
                  Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                  if (-not (Test-Path -LiteralPath $_.FullName)) { $freed += $sz }
                }
              } else {
                if ($_.LastWriteTime -lt $cutoff) {
                  $sz = [int64]$_.Length
                  Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                  if (-not (Test-Path -LiteralPath $_.FullName)) { $freed += $sz }
                }
              }
            } catch {
              # locked files — skip
            }
          }
          return $freed
        }

        $totalFreed = 0L

        if ($categories -contains 'recycle_bin') {
          Write-Output '--- recycle_bin ---'
          try {
            Clear-RecycleBin -Force -ErrorAction Stop
            Write-Output '  Clear-RecycleBin done.'
          } catch {
            Write-Output ("  Clear-RecycleBin: " + $_.Exception.Message)
          }
        }

        if ($categories -contains 'temp_files') {
          Write-Output '--- temp_files (>24h, skip locked) ---'
          $tempUser = [Environment]::GetEnvironmentVariable('TEMP')
          if (-not $tempUser) { $tempUser = [Environment]::GetEnvironmentVariable('TMP') }
          $winTemp = Join-Path $env:WINDIR 'Temp'
          $f1 = Remove-OldFiles $tempUser 24
          $f2 = Remove-OldFiles $winTemp 24
          $totalFreed += $f1 + $f2
          Write-Output ("  User TEMP freed ~{0:N1} MB" -f ($f1/1MB))
          Write-Output ("  Windows\\Temp freed ~{0:N1} MB" -f ($f2/1MB))
        }

        if ($categories -contains 'windows_update_cache') {
          Write-Output '--- windows_update_cache ---'
          $wu = Join-Path $env:WINDIR 'SoftwareDistribution\Download'
          try {
            Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            if (Test-Path -LiteralPath $wu) {
              $before = Get-DirSizeBytes $wu
              Get-ChildItem -LiteralPath $wu -Force -ErrorAction SilentlyContinue | ForEach-Object {
                try {
                  Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
                } catch {}
              }
              $after = Get-DirSizeBytes $wu
              $delta = [math]::Max(0, $before - $after)
              $totalFreed += $delta
              Write-Output ("  SoftwareDistribution\\Download freed ~{0:N1} MB" -f ($delta/1MB))
            } else {
              Write-Output "  missing: $wu"
            }
          } finally {
            try { Start-Service -Name wuauserv -ErrorAction SilentlyContinue } catch {}
          }
        }

        if ($categories -contains 'memory_dumps') {
          Write-Output '--- memory_dumps ---'
          $minidump = Join-Path $env:WINDIR 'Minidump'
          $memdump = Join-Path $env:WINDIR 'MEMORY.DMP'
          $fd = 0L
          if (Test-Path -LiteralPath $minidump) {
            $before = Get-DirSizeBytes $minidump
            Get-ChildItem -LiteralPath $minidump -Force -ErrorAction SilentlyContinue | ForEach-Object {
              try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue } catch {}
            }
            $fd += [math]::Max(0, $before - (Get-DirSizeBytes $minidump))
          }
          if (Test-Path -LiteralPath $memdump) {
            try {
              $len = (Get-Item -LiteralPath $memdump -Force).Length
              Remove-Item -LiteralPath $memdump -Force -ErrorAction SilentlyContinue
              if (-not (Test-Path -LiteralPath $memdump)) { $fd += [int64]$len }
            } catch {}
          }
          $totalFreed += $fd
          Write-Output ("  dumps freed ~{0:N1} MB" -f ($fd/1MB))
        }

        if ($categories -contains 'thumbnails') {
          Write-Output '--- thumbnails ---'
          $thumbDir = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
          $fd = 0L
          if (Test-Path -LiteralPath $thumbDir) {
            Get-ChildItem -LiteralPath $thumbDir -Force -Filter 'thumbcache_*.db' -ErrorAction SilentlyContinue | ForEach-Object {
              try {
                $len = [int64]$_.Length
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                if (-not (Test-Path -LiteralPath $_.FullName)) { $fd += $len }
              } catch {}
            }
            Get-ChildItem -LiteralPath $thumbDir -Force -Filter 'iconcache_*.db' -ErrorAction SilentlyContinue | ForEach-Object {
              try {
                $len = [int64]$_.Length
                Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                if (-not (Test-Path -LiteralPath $_.FullName)) { $fd += $len }
              } catch {}
            }
          }
          $totalFreed += $fd
          Write-Output ("  thumbnail caches freed ~{0:N1} MB" -f ($fd/1MB))
        }

        Write-Output ''
        Write-Output ("Total estimated freed (excl. recycle bin COM size): ~{0:N1} MB / {1:N3} GB" -f ($totalFreed/1MB), ($totalFreed/1GB))
        Write-Output 'Done. Arbitrary paths were not deleted.'
        exit 0
        """;

    private static int GetInt(JsonElement args, string name, int defaultValue, int min, int max)
    {
        if (!args.TryGetProperty(name, out var prop) || !prop.TryGetInt32(out var value))
        {
            return defaultValue;
        }

        return Math.Clamp(value, min, max);
    }
}
