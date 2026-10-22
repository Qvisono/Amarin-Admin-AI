using Microsoft.Win32;

namespace Amarin.Tools;

/// <summary>Скрипты и проверки для <see cref="PrintQueueTool"/>. Без принтеров — проверяются тестами.</summary>
internal static class PrintQueueCommands
{
    /// <summary>Задание старше этого считается застрявшим, даже если Windows пишет, что всё в порядке.</summary>
    internal const int StuckMinutes = 10;

    /// <summary>Файлы очереди: данные задания и его описание. Ничего другого очистка не трогает.</summary>
    internal static readonly string[] SpoolPatterns = ["*.SPL", "*.SHD"];

    private const string WindowsKey = @"HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Windows";

    /// <summary>Имя принтера: непустое, без управляющих знаков. В скрипт оно идёт только в кавычках.</summary>
    public static bool IsPrinterName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 256 && !name.Any(char.IsControl);

    public const string ListScript = $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $spooler = Get-Service -Name Spooler
        "Print Spooler service: $($spooler.Status)"
        $mode = (Get-ItemProperty -LiteralPath '{{WindowsKey}}' -Name LegacyDefaultPrinterMode).LegacyDefaultPrinterMode
        "Windows chooses the default printer itself: $(if ($mode -eq 1) { 'no' } else { 'yes' })"
        if ($spooler.Status -ne 'Running') { return }
        $wmi = @{}
        Get-CimInstance Win32_Printer | ForEach-Object { $wmi[$_.Name] = $_ }
        Get-Printer | ForEach-Object {
          $w = $wmi[$_.Name]
          [PSCustomObject]@{
            Name = $_.Name
            Default = [bool]$w.Default
            Status = [string]$_.PrinterStatus
            Offline = [bool]$w.WorkOffline
            Jobs = $_.JobCount
            Port = $_.PortName
            Driver = $_.DriverName
            Shared = $_.Shared
          }
        } | Format-List
        """;

    public static string JobsScript(string? printer) => $$"""
        $ErrorActionPreference = 'Stop'
        $printers = if ('{{PowerShellHelper.QuoteLiteral(printer)}}' -ne '') { Get-Printer -Name '{{PowerShellHelper.QuoteLiteral(printer)}}' } else { Get-Printer }
        $ErrorActionPreference = 'SilentlyContinue'
        $rows = & {
          foreach ($p in $printers) {
            Get-PrintJob -PrinterName $p.Name | ForEach-Object {
              $age = [int]((Get-Date) - $_.SubmittedTime).TotalMinutes
              $status = [string]$_.JobStatus
              [PSCustomObject]@{
                Printer = $p.Name
                Id = $_.Id
                Document = $_.DocumentName
                User = $_.UserName
                Status = $status
                Pages = $_.TotalPages
                SizeKB = [int]($_.Size / 1KB)
                AgeMin = $age
                Stuck = ($status -match 'Error|Paused|Blocked|Deleting|Offline|PaperOut|UserIntervention|Restart') -or ($age -ge {{StuckMinutes}})
              }
            }
          }
        }
        if (-not $rows) { 'No print jobs.' } else { $rows | Format-Table -AutoSize -Wrap | Out-String -Width 220 }
        """;

    public static string CancelScript(string printer, int jobId) => $$"""
        $ErrorActionPreference = 'Stop'
        Remove-PrintJob -PrinterName '{{PowerShellHelper.QuoteLiteral(printer)}}' -ID {{jobId}}
        'OK'
        """;

    public static string ClearScript(string printer) => $$"""
        $ErrorActionPreference = 'Stop'
        $jobs = @(Get-PrintJob -PrinterName '{{PowerShellHelper.QuoteLiteral(printer)}}')
        $jobs | Remove-PrintJob
        "REMOVED $($jobs.Count)"
        """;

    /// <summary>
    /// Принтер по умолчанию. Заодно выключается «Windows сама выбирает принтер по умолчанию»: при
    /// включённом выбор человека Windows сменила бы на последний использованный при первой печати.
    /// </summary>
    public static string DefaultScript(string printer) => $$"""
        $ErrorActionPreference = 'Stop'
        $printer = Get-CimInstance Win32_Printer | Where-Object { $_.Name -eq '{{PowerShellHelper.QuoteLiteral(printer)}}' }
        if (-not $printer) { 'PRINTER_NOT_FOUND'; return }
        Set-ItemProperty -LiteralPath '{{WindowsKey}}' -Name LegacyDefaultPrinterMode -Value 1 -Type DWord
        $result = Invoke-CimMethod -InputObject $printer -MethodName SetDefaultPrinter
        if ($result.ReturnValue -ne 0) { "SET_DEFAULT_FAILED $($result.ReturnValue)"; return }
        'OK'
        """;

    /// <summary>
    /// Папка очереди диспетчера печати — только если она внутри папки Windows. Путь берётся из
    /// реестра, и значение, уводящее в чужую папку, не должно превратить очистку в удаление там.
    /// </summary>
    public static string? SpoolDirectory(string? configured, string windows)
    {
        var expanded = Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(windows, @"System32\spool\PRINTERS")
            : configured.Trim());
        if (!Path.IsPathFullyQualified(expanded))
        {
            return null;
        }

        var full = PermissionTargets.Canonical(Path.GetFullPath(expanded));
        var root = PermissionTargets.Canonical(windows);
        return root.Length > 0 && full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static string? ConfiguredSpoolDirectory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        return key?.GetValue("DefaultSpoolDirectory") as string;
    }

    /// <summary>Удаляет файлы заданий из папки очереди; подпапки и прочие файлы не трогает.</summary>
    public static int Purge(string directory)
    {
        var removed = 0;
        foreach (var pattern in SpoolPatterns)
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    // Файл ещё держит драйвер печати — останется до следующей очистки.
                }
            }
        }

        return removed;
    }
}
