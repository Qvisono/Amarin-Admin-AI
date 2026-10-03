namespace Amarin.Tools;

/// <summary>Команды Защитника Windows и удалённого рабочего стола.</summary>
internal static class SecurityCommands
{
    /// <summary>Быстрая проверка Защитника; долгая, поэтому идёт с отменой хода.</summary>
    public const string QuickScan = """
        $ErrorActionPreference = 'Stop'
        Start-MpScan -ScanType QuickScan
        Get-MpComputerStatus | Select-Object QuickScanStartTime, QuickScanEndTime, AntivirusSignatureVersion | Format-List
        Get-MpThreatDetection -ErrorAction SilentlyContinue | Select-Object -First 20 InitialDetectionTime, ThreatID, ActionSuccess, Resources | Format-List
        """;

    public const string UpdateSignatures = """
        $ErrorActionPreference = 'Stop'
        Update-MpSignature
        Get-MpComputerStatus | Select-Object AntivirusSignatureVersion, AntivirusSignatureLastUpdated, AMEngineVersion | Format-List
        """;

    /// <summary>
    /// Группа правил «Удалённый рабочий стол» ресурсной строкой, а не отображаемым именем:
    /// имя переведено на язык Windows, а ресурс один на всех.
    /// </summary>
    public const string RdpFirewallGroup = "@FirewallAPI.dll,-28752";

    public const string TerminalServerKey = @"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server";

    public static string RdpScript(bool enable) => $$"""
        $ErrorActionPreference = 'Stop'
        Set-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -Name fDenyTSConnections -Value {{(enable ? 0 : 1)}} -Type DWord
        {{(enable ? "Enable-NetFirewallRule" : "Disable-NetFirewallRule")}} -Group '{{RdpFirewallGroup}}'
        [PSCustomObject]@{
          RdpEnabled = ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server').fDenyTSConnections -eq 0)
          FirewallRulesEnabled = @(Get-NetFirewallRule -Group '{{RdpFirewallGroup}}' | Where-Object { $_.Enabled -eq 'True' }).Count
        } | Format-List
        """;

    /// <summary>Правила группы RDP и их «включено» — одной строкой JSON, для снимка отката.</summary>
    public const string RdpFirewallStateScript = $$"""
        $ErrorActionPreference = 'Stop'
        $rules = @(Get-NetFirewallRule -Group '{{RdpFirewallGroup}}' | ForEach-Object { @{ Name = $_.Name; Enabled = ($_.Enabled -eq 'True') } })
        ConvertTo-Json -Compress -InputObject @{ Rules = $rules }
        """;
}
