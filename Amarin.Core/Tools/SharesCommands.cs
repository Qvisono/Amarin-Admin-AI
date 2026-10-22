using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>Скрипты и проверки для <see cref="SharesTool"/>. Без сети — проверяются тестами.</summary>
/// <remarks>
/// Правила брандмауэра ищутся по группе в виде ресурса (<c>@FirewallAPI.dll,-32752</c>), а не по
/// названию: «Сетевое обнаружение» и «Network Discovery» — одна группа, и только ресурс от языка
/// Windows не зависит.
/// </remarks>
internal static partial class SharesCommands
{
    internal const string DiscoveryGroup = "@FirewallAPI.dll,-32752";
    internal const string FileSharingGroup = "@FirewallAPI.dll,-28502";

    /// <summary>Буква диска: одна латинская буква, двоеточие по желанию.</summary>
    public static bool TryLetter(string? value, out char letter)
    {
        letter = '\0';
        var text = (value ?? "").Trim().TrimEnd(':').ToUpperInvariant();
        if (text.Length != 1 || text[0] is < 'A' or > 'Z')
        {
            return false;
        }

        letter = text[0];
        return true;
    }

    /// <summary>Сетевой путь <c>\\сервер\общая папка[\…]</c> без подстановок и кавычек.</summary>
    public static bool IsUncPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && UncPath().IsMatch(value.Trim());

    public const string StatusScript = $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $cs = Get-CimInstance Win32_ComputerSystem
        "Computer: $env:COMPUTERNAME; $(if ($cs.PartOfDomain) { "domain $($cs.Domain)" } else { "workgroup $($cs.Workgroup)" })"
        'Networks:'
        Get-NetConnectionProfile | ForEach-Object { "  $($_.InterfaceAlias): $($_.NetworkCategory) ($($_.Name)), IPv4 $($_.IPv4Connectivity)" }
        'Firewall rule groups (enabled of total, by profile):'
        foreach ($group in @(@{ Name = 'Network Discovery'; Id = '{{DiscoveryGroup}}' }, @{ Name = 'File and Printer Sharing'; Id = '{{FileSharingGroup}}' })) {
          $rules = @(Get-NetFirewallRule -Group $group.Id)
          $parts = foreach ($kind in 'Private', 'Public', 'Domain') {
            $mine = @($rules | Where-Object { ([string]$_.Profile) -match "$kind|Any" })
            "$kind $(@($mine | Where-Object { [string]$_.Enabled -eq 'True' }).Count)/$($mine.Count)"
          }
          "  $($group.Name): $($parts -join ', ')"
        }
        'Services:'
        foreach ($name in 'FDResPub', 'fdPHost', 'SSDPSRV', 'upnphost', 'LanmanServer', 'LanmanWorkstation') {
          $s = Get-Service -Name $name
          if ($s) { "  $name ($($s.DisplayName)): $($s.Status), $($s.StartType)" } else { "  ${name}: not installed" }
        }
        $client = Get-SmbClientConfiguration
        if ($client) { "SMB client: insecure guest logons $($client.EnableInsecureGuestLogons), signing required $($client.RequireSecuritySignature)" }
        $server = Get-SmbServerConfiguration
        if ($server) { "SMB server: SMB1 $($server.EnableSMB1Protocol), SMB2/3 $($server.EnableSMB2Protocol), signing required $($server.RequireSecuritySignature)" }
        else { 'SMB server settings: not readable without administrator rights.' }
        'NetBIOS over TCP/IP by adapter (0 from DHCP, 1 on, 2 off):'
        Get-CimInstance Win32_NetworkAdapterConfiguration -Filter 'IPEnabled=TRUE' | ForEach-Object { "  $($_.Description): $($_.TcpipNetbiosOptions)" }
        """;

    public const string SharesScript = """
        try {
          Get-SmbShare -ErrorAction Stop | Select-Object Name, Path, Description, CurrentUsers | Format-Table -AutoSize -Wrap | Out-String -Width 220
        } catch {
          Get-CimInstance Win32_Share -ErrorAction SilentlyContinue | Select-Object Name, Path, Description | Format-Table -AutoSize -Wrap | Out-String -Width 220
        }
        """;

    public const string DrivesScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $maps = @(Get-SmbMapping)
        if ($maps.Count -gt 0) { $maps | Select-Object LocalPath, RemotePath, Status | Format-Table -AutoSize | Out-String -Width 220 }
        else { 'No mapped network drives in this session.' }
        Get-ChildItem -LiteralPath 'HKCU:\Network' | ForEach-Object { "Remembered for sign-in: $($_.PSChildName): $((Get-ItemProperty -LiteralPath $_.PSPath).RemotePath)" }
        'Drives mapped in a window run as administrator are not visible in a normal one, and the other way round.'
        """;

    public static string SetPrivateScript(string adapter) => $$"""
        $ErrorActionPreference = 'Stop'
        $connection = Get-NetConnectionProfile -InterfaceAlias '{{PowerShellHelper.QuoteLiteral(adapter)}}'
        if ([string]$connection.NetworkCategory -eq 'DomainAuthenticated') { 'DOMAIN'; return }
        Set-NetConnectionProfile -InterfaceAlias '{{PowerShellHelper.QuoteLiteral(adapter)}}' -NetworkCategory Private
        'OK'
        """;

    /// <summary>
    /// Правила группы для частных и доменных профилей и службы. В общедоступной сети ничего не
    /// включается: обнаружение и общий доступ там открыли бы компьютер всем вокруг.
    /// </summary>
    /// <remarks>
    /// Группа включается API брандмауэра по профилям (<c>EnableRuleGroup</c>, 3 — домен и частный),
    /// как это делают параметры общего доступа Windows. Но встроенное правило бывает заведено сразу
    /// на частный и общедоступный профиль, и включённое оно открыто в обоих. Поэтому правило с Public
    /// или Any, которое включилось этим вызовом, выключается обратно, и об этом говорит метка
    /// <c>PUBLIC_RULES</c>; включённые раньше остаются как были.
    /// </remarks>
    public static string EnableScript(bool fileSharing) => $$"""
        $ErrorActionPreference = 'Stop'
        $trusted = @(Get-NetConnectionProfile | Where-Object { [string]$_.NetworkCategory -ne 'Public' })
        if ($trusted.Count -eq 0) { "PUBLIC $((Get-NetConnectionProfile | ForEach-Object { $_.InterfaceAlias }) -join ', ')"; return }
        $group = '{{(fileSharing ? FileSharingGroup : DiscoveryGroup)}}'
        function OpenToPublic { @(Get-NetFirewallRule -Group $group -ErrorAction SilentlyContinue | Where-Object { ([string]$_.Profile) -match 'Public|Any' -and [string]$_.Enabled -eq 'True' }) }
        $before = @(OpenToPublic | ForEach-Object { $_.Name })
        (New-Object -ComObject HNetCfg.FwPolicy2).EnableRuleGroup(3, $group, $true)
        $reopened = @(OpenToPublic | Where-Object { $before -notcontains $_.Name })
        if ($reopened.Count -gt 0) { $reopened | Disable-NetFirewallRule }
        {{(fileSharing
            ? "Set-Service -Name LanmanServer -StartupType Automatic\nStart-Service -Name LanmanServer"
            : "sc.exe config FDResPub start= delayed-auto | Out-Null\nforeach ($name in 'fdPHost', 'FDResPub', 'SSDPSRV', 'upnphost') { Start-Service -Name $name }")}}
        if ($reopened.Count -gt 0) { "PUBLIC_RULES $($reopened.Count)"; return }
        'OK'
        """;

    public static string MapScript(char letter, string remote, bool persistent) => $$"""
        $ErrorActionPreference = 'Stop'
        New-SmbMapping -LocalPath '{{letter}}:' -RemotePath '{{PowerShellHelper.QuoteLiteral(remote)}}' -Persistent ${{(persistent ? "true" : "false")}} | Out-Null
        'OK'
        """;

    public static string UnmapScript(char letter) => $$"""
        $ErrorActionPreference = 'Stop'
        if (-not (Get-SmbMapping -LocalPath '{{letter}}:' -ErrorAction SilentlyContinue)) { 'NOT_MAPPED'; return }
        Remove-SmbMapping -LocalPath '{{letter}}:' -Force -UpdateProfile
        'OK'
        """;

    [GeneratedRegex(@"^\\\\[^\\/:*?""<>|\p{C}]+(\\[^\\/:*?""<>|\p{C}]+)+\\?$")]
    private static partial Regex UncPath();
}
