using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Disk space analysis and safe cleanup of fixed categories only (never arbitrary paths on cleanup).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DiskSpaceTool : ITool
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "recycle_bin",
        "temp_files",
        "windows_update_cache",
        "memory_dumps",
        "thumbnails"
    };

    // Path for largest_items only — basic safety (no free-form command injection).
    private static readonly Regex SafePathChars = new(
        @"^[A-Za-z]:\\(?:[^<>:""|?*\x00-\x1F]+\\)*[^<>:""|?*\x00-\x1F]*$",
        RegexOptions.Compiled);

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
                "analyze" => Task.FromResult(PowerShellHelper.Run(AnalyzeScript(), 180, maxOutput: 4000)),
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
            !SafePathChars.IsMatch(path.TrimEnd('\\')))
        {
            // Allow root like C:\
            var isRoot = path.Length is 3 && path[1] == ':' && path[2] == '\\';
            if (!isRoot)
            {
                return ToolResult.Fail("path contains unsupported characters");
            }
        }

        var top = GetInt(arguments, "top", 20, 1, 50);
        var safePath = path.Replace("'", "''", StringComparison.Ordinal);
        return PowerShellHelper.Run(LargestItemsScript(safePath, top), 180, maxOutput: 4000);
    }

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

    private static string AnalyzeScript() => """
        Write-Output '=== Volumes (free / used) ==='
        Get-Volume -ErrorAction SilentlyContinue |
          Where-Object { $_.DriveType -eq 'Fixed' -or $_.DriveLetter } |
          Select-Object DriveLetter, FileSystemLabel, FileSystem,
            @{n='SizeGB';e={ if ($_.Size) { [math]::Round($_.Size/1GB, 2) } else { $null } }},
            @{n='FreeGB';e={ if ($_.SizeRemaining) { [math]::Round($_.SizeRemaining/1GB, 2) } else { $null } }},
            @{n='UsedGB';e={
              if ($_.Size -and $null -ne $_.SizeRemaining) {
                [math]::Round(($_.Size - $_.SizeRemaining)/1GB, 2)
              } else { $null }
            }},
            @{n='UsedPct';e={
              if ($_.Size -and $_.Size -gt 0) {
                [math]::Round(100.0 * ($_.Size - $_.SizeRemaining) / $_.Size, 1)
              } else { $null }
            }} |
          Sort-Object DriveLetter |
          Format-Table -AutoSize | Out-String -Width 200

        function Get-DirSizeGB([string]$p) {
          if (-not $p -or -not (Test-Path -LiteralPath $p)) { return $null }
          try {
            $sum = (Get-ChildItem -LiteralPath $p -Force -Recurse -ErrorAction SilentlyContinue |
              Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum
            if ($null -eq $sum) { return 0 }
            return [math]::Round([double]$sum / 1GB, 3)
          } catch { return $null }
        }

        function Get-FileSizeGB([string]$p) {
          if (-not $p -or -not (Test-Path -LiteralPath $p)) { return $null }
          try {
            $len = (Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue).Length
            return [math]::Round([double]$len / 1GB, 3)
          } catch { return $null }
        }

        $tempUser = [Environment]::GetEnvironmentVariable('TEMP')
        if (-not $tempUser) { $tempUser = [Environment]::GetEnvironmentVariable('TMP') }
        $winTemp = Join-Path $env:WINDIR 'Temp'
        $wuCache = Join-Path $env:WINDIR 'SoftwareDistribution\Download'
        $minidump = Join-Path $env:WINDIR 'Minidump'
        $memdump = Join-Path $env:WINDIR 'MEMORY.DMP'

        Write-Output '=== Common reclaimable locations (GB) ==='
        $rows = @()

        # Recycle Bin approximate size via Shell.Application
        $rbGb = $null
        try {
          $shell = New-Object -ComObject Shell.Application
          $rb = $shell.NameSpace(0x0a)
          if ($rb) {
            $sum = 0L
            foreach ($i in @($rb.Items())) {
              try {
                $sz = $i.Size
                if ($sz) { $sum += [int64]$sz }
              } catch {}
            }
            $rbGb = [math]::Round($sum / 1GB, 3)
          }
        } catch {}
        $rows += [PSCustomObject]@{ Location = 'RecycleBin'; Path = 'shell:RecycleBinFolder'; SizeGB = $rbGb }

        $rows += [PSCustomObject]@{ Location = 'UserTemp'; Path = $tempUser; SizeGB = (Get-DirSizeGB $tempUser) }
        $rows += [PSCustomObject]@{ Location = 'WindowsTemp'; Path = $winTemp; SizeGB = (Get-DirSizeGB $winTemp) }
        $rows += [PSCustomObject]@{ Location = 'WindowsUpdateCache'; Path = $wuCache; SizeGB = (Get-DirSizeGB $wuCache) }
        $rows += [PSCustomObject]@{ Location = 'Minidump'; Path = $minidump; SizeGB = (Get-DirSizeGB $minidump) }
        $rows += [PSCustomObject]@{ Location = 'MemoryDmp'; Path = $memdump; SizeGB = (Get-FileSizeGB $memdump) }

        $thumb = Join-Path $env:LOCALAPPDATA 'Microsoft\Windows\Explorer'
        $thumbSize = $null
        if (Test-Path -LiteralPath $thumb) {
          try {
            $sum = (Get-ChildItem -LiteralPath $thumb -Force -Filter 'thumbcache_*.db' -ErrorAction SilentlyContinue |
              Measure-Object -Property Length -Sum).Sum
            $thumbSize = if ($null -eq $sum) { 0 } else { [math]::Round([double]$sum / 1GB, 3) }
          } catch {}
        }
        $rows += [PSCustomObject]@{ Location = 'Thumbnails'; Path = $thumb; SizeGB = $thumbSize }

        $rows | Format-Table -AutoSize | Out-String -Width 200
        Write-Output 'cleanup categories: recycle_bin, temp_files, windows_update_cache, memory_dumps, thumbnails'
        exit 0
        """;

    private static string LargestItemsScript(string safePath, int top) => $$"""
        $root = '{{safePath}}'
        $topN = {{top}}
        $maxDepth = 4
        $deadline = (Get-Date).AddSeconds(90)

        Write-Output "=== largest_items under: $root (top $topN, depth<=$maxDepth, timeout ~90s) ==="
        if (-not (Test-Path -LiteralPath $root)) {
          Write-Output "Path not found: $root"
          exit 1
        }

        $items = New-Object System.Collections.Generic.List[object]
        $queue = New-Object System.Collections.Generic.Queue[object]
        $queue.Enqueue([PSCustomObject]@{ Path = $root; Depth = 0 })

        while ($queue.Count -gt 0) {
          if ((Get-Date) -gt $deadline) {
            Write-Output 'Scan time limit reached; results may be partial.'
            break
          }
          $cur = $queue.Dequeue()
          $p = $cur.Path
          $depth = [int]$cur.Depth
          try {
            $entries = Get-ChildItem -LiteralPath $p -Force -ErrorAction SilentlyContinue
          } catch { continue }

          foreach ($e in $entries) {
            if ((Get-Date) -gt $deadline) { break }
            try {
              if ($e.PSIsContainer) {
                $size = $null
                # immediate children size only (fast approx) + recurse for top dirs
                try {
                  $size = (Get-ChildItem -LiteralPath $e.FullName -Force -File -ErrorAction SilentlyContinue |
                    Measure-Object -Property Length -Sum -ErrorAction SilentlyContinue).Sum
                  if ($null -eq $size) { $size = 0 }
                } catch { $size = 0 }
                $items.Add([PSCustomObject]@{
                  Kind = 'dir'
                  Path = $e.FullName
                  SizeBytes = [int64]$size
                  Note = 'immediate-files-only'
                })
                if ($depth + 1 -le $maxDepth) {
                  $queue.Enqueue([PSCustomObject]@{ Path = $e.FullName; Depth = $depth + 1 })
                }
              } else {
                $items.Add([PSCustomObject]@{
                  Kind = 'file'
                  Path = $e.FullName
                  SizeBytes = [int64]$e.Length
                  Note = ''
                })
              }
            } catch {}
          }
        }

        $topItems = $items | Sort-Object SizeBytes -Descending | Select-Object -First $topN
        $topItems | ForEach-Object {
          $gb = [math]::Round($_.SizeBytes / 1GB, 3)
          $mb = [math]::Round($_.SizeBytes / 1MB, 1)
          [PSCustomObject]@{
            Kind = $_.Kind
            SizeMB = $mb
            SizeGB = $gb
            Path = $_.Path
            Note = $_.Note
          }
        } | Format-Table -AutoSize -Wrap | Out-String -Width 200

        Write-Output "Items scanned (listed): $($items.Count)"
        exit 0
        """;

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
