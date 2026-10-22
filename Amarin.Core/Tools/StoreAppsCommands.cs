using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Скрипты и проверки для <see cref="StoreAppsTool"/>. Без Windows — проверяются тестами.</summary>
/// <remarks>
/// Проверка «framework, неудаляемое, системная подпись» стоит внутри скрипта: эти признаки знает
/// только сам пакет, а имя от модели может совпасть с несколькими пакетами разом.
/// </remarks>
internal static partial class StoreAppsCommands
{
    /// <summary>
    /// Пакеты, без которых не поставить и не вернуть остальные: сам Store, «Установщик приложений»
    /// (с ним приходит winget), покупки Store и окно «Безопасность Windows».
    /// </summary>
    internal static readonly FrozenSet<string> ProtectedNames = new[]
    {
        "Microsoft.WindowsStore", "Microsoft.DesktopAppInstaller", "Microsoft.StorePurchaseApp", "Microsoft.SecHealthUI"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private const string ProvisionedKey = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";

    /// <summary>Имя пакета или его полное имя: буквы, цифры, точки, дефисы, подчёркивания и тильды.</summary>
    public static bool IsPackageName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && PackageName().IsMatch(value);

    /// <summary>Имя пакета без версии и издателя: часть полного имени до первого «_».</summary>
    public static string BaseName(string value) => value.Split('_', 2)[0];

    public static bool IsProtected(string value) => ProtectedNames.Contains(BaseName(value.Trim()));

    public static string ListScript(string filter, bool frameworks, int max) => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $preinstalled = @{}
        Get-ChildItem -LiteralPath '{{ProvisionedKey}}' | ForEach-Object { $preinstalled[($_.PSChildName -split '_')[0]] = $true }
        $filter = '{{PowerShellHelper.QuoteLiteral(filter)}}'
        $all = @(Get-AppxPackage | Where-Object { ($filter -eq '' -or $_.Name -like "*$filter*") -and ({{(frameworks ? "$true" : "$false")}} -or -not $_.IsFramework) } | Sort-Object Name)
        $rows = $all | Select-Object -First {{max}} Name, Version,
          @{ n = 'Signature'; e = { [string]$_.SignatureKind } },
          @{ n = 'Flags'; e = { $f = @(); if ($_.IsFramework) { $f += 'framework' }; if ($_.NonRemovable) { $f += 'non-removable' }; if ($preinstalled[$_.Name]) { $f += 'preinstalled' }; $f -join ',' } }
        if ($preinstalled.Count -eq 0) { 'Note: the list of preinstalled packages could not be read.' }
        $rows | Format-Table -AutoSize | Out-String -Width 200
        "Shown $(@($rows).Count) of $($all.Count)."
        """;

    /// <summary>Удаление у текущего пользователя и, при <paramref name="forNewUsers"/>, снятие предустановки.</summary>
    public static string RemoveScript(string name, bool forNewUsers) => $$"""
        $ErrorActionPreference = 'Stop'
        $name = '{{PowerShellHelper.QuoteLiteral(name)}}'
        $packages = @(Get-AppxPackage | Where-Object { $_.Name -eq $name -or $_.PackageFullName -eq $name })
        if ($packages.Count -eq 0) { 'NOT_FOUND'; return }
        foreach ($p in $packages) {
          if ($p.IsFramework -or $p.NonRemovable -or [string]$p.SignatureKind -eq 'System') { "PROTECTED $($p.Name)"; return }
        }
        foreach ($p in $packages) { Remove-AppxPackage -Package $p.PackageFullName }
        "REMOVED $(($packages | ForEach-Object { $_.PackageFullName }) -join ', ')"
        if ({{(forNewUsers ? "$true" : "$false")}}) {
          $base = $packages[0].Name
          $staged = @(Get-AppxProvisionedPackage -Online | Where-Object { $_.DisplayName -eq $base })
          foreach ($q in $staged) { Remove-AppxProvisionedPackage -Online -PackageName $q.PackageName | Out-Null }
          "DEPROVISIONED $($staged.Count)"
        }
        """;

    public static string ResetScript(string name) => $$"""
        $ErrorActionPreference = 'Stop'
        $name = '{{PowerShellHelper.QuoteLiteral(name)}}'
        $packages = @(Get-AppxPackage | Where-Object { $_.Name -eq $name -or $_.PackageFullName -eq $name })
        if ($packages.Count -eq 0) { 'NOT_FOUND'; return }
        if (-not (Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue)) { 'NO_RESET'; return }
        foreach ($p in $packages) { if ($p.IsFramework) { "PROTECTED $($p.Name)"; return } }
        foreach ($p in $packages) { Reset-AppxPackage -Package $p.PackageFullName }
        "RESET $(($packages | ForEach-Object { $_.PackageFullName }) -join ', ')"
        """;

    [GeneratedRegex(@"^[A-Za-z0-9._~-]+$")]
    private static partial Regex PackageName();
}
