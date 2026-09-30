using System.Net;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

/// <summary>
/// Сборка команд сетевых инструментов — отдельно от исполнения, чтобы её можно было проверить
/// тестами, ничего не запуская.
/// </summary>
/// <remarks>
/// Все значения от модели проходят здесь через перечень допустимых или через проверку формы. В
/// PowerShell они попадают строкой в одинарных кавычках (<see cref="PowerShellHelper.QuoteLiteral"/>),
/// программам — списком аргументов (<see cref="NativeProcess"/>); строкой — только там, где
/// программа (netsh) разбирает кавычки по-своему, и тогда значение не может содержать кавычку.
/// </remarks>
internal static partial class NetworkCommands
{
    private static readonly string[] FirewallProfiles = ["domain", "private", "public", "all"];

    /// <summary>Больше адресов DNS адаптеру Windows всё равно не отдаёт.</summary>
    public const int MaxDnsServers = 6;

    public const int MaxHops = 30;

    /// <summary>Профиль брандмауэра из перечня схемы; «all» — у netsh своё слово <c>allprofiles</c>.</summary>
    public static bool TryFirewallProfile(string? value, out string netshProfile)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        netshProfile = normalized switch
        {
            "all" => "allprofiles",
            _ when normalized is not null && FirewallProfiles.Contains(normalized) => normalized + "profile",
            _ => ""
        };
        return netshProfile.Length > 0;
    }

    public static IReadOnlyList<string> FirewallState(string netshProfile, bool enabled) =>
        ["advfirewall", "set", netshProfile, "state", enabled ? "on" : "off"];

    // --- DNS ---------------------------------------------------------------------------------

    /// <summary>Адреса DNS от модели: каждый — IP-адрес, без повторов, не больше шести.</summary>
    public static bool TryDnsServers(IEnumerable<string?> values, out List<string> servers, out string? error)
    {
        servers = [];
        foreach (var raw in values)
        {
            var value = raw?.Trim() ?? "";
            if (!IPAddress.TryParse(value, out var address))
            {
                error = $"«{value}» is not an IP address.";
                return false;
            }

            var text = address.ToString();
            if (!servers.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                servers.Add(text);
            }
        }

        if (servers.Count is 0 or > MaxDnsServers)
        {
            error = $"Give 1-{MaxDnsServers} DNS server addresses.";
            return false;
        }

        error = null;
        return true;
    }

    public static string SetDnsScript(string alias, IReadOnlyList<string> servers)
    {
        var list = string.Join(",", servers.Select(server => "'" + PowerShellHelper.QuoteLiteral(server) + "'"));
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-DnsClientServerAddress -InterfaceAlias '{{PowerShellHelper.QuoteLiteral(alias)}}' -ServerAddresses @({{list}})
            Clear-DnsClientCache
            Get-DnsClientServerAddress -InterfaceAlias '{{PowerShellHelper.QuoteLiteral(alias)}}' | Select-Object InterfaceAlias, AddressFamily, ServerAddresses | Format-Table -Wrap
            """;
    }

    public static string ResetDnsScript(string alias) => $$"""
        $ErrorActionPreference = 'Stop'
        Set-DnsClientServerAddress -InterfaceAlias '{{PowerShellHelper.QuoteLiteral(alias)}}' -ResetServerAddresses
        Clear-DnsClientCache
        Get-DnsClientServerAddress -InterfaceAlias '{{PowerShellHelper.QuoteLiteral(alias)}}' | Select-Object InterfaceAlias, AddressFamily, ServerAddresses | Format-Table -Wrap
        """;

    /// <summary>
    /// Статические адреса DNS адаптера — из реестра, а не из <c>Get-DnsClientServerAddress</c>:
    /// тот показывает и полученные от DHCP, а вернуть надо именно «как было настроено».
    /// </summary>
    /// <remarks>Пустой список — адреса брались от DHCP. Выход — одна строка JSON.</remarks>
    public static string DnsStateScript(string alias) => $$"""
        $ErrorActionPreference = 'Stop'
        $guid = (Get-NetAdapter -Name '{{PowerShellHelper.QuoteLiteral(alias)}}').InterfaceGuid
        $found = @()
        foreach ($stack in @('Tcpip', 'Tcpip6')) {
          $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$stack\Parameters\Interfaces\$guid"
          $value = (Get-ItemProperty -LiteralPath $key -Name NameServer -ErrorAction SilentlyContinue).NameServer
          if ($value) { $found += @($value -split '[ ,;]' | Where-Object { $_ }) }
        }
        ConvertTo-Json -Compress -InputObject @{ Static = @($found) }
        """;

    // --- Адаптеры ------------------------------------------------------------------------------

    public static string AdapterScript(string alias, bool enable) => $$"""
        $ErrorActionPreference = 'Stop'
        {{(enable ? "Enable-NetAdapter" : "Disable-NetAdapter")}} -Name '{{PowerShellHelper.QuoteLiteral(alias)}}' -Confirm:$false
        Get-NetAdapter -Name '{{PowerShellHelper.QuoteLiteral(alias)}}' | Select-Object Name, InterfaceDescription, AdminStatus, Status | Format-List
        """;

    /// <summary>«Up» или «Down» — включён ли адаптер администратором.</summary>
    public static string AdapterStateScript(string alias) =>
        $"(Get-NetAdapter -Name '{PowerShellHelper.QuoteLiteral(alias)}' -ErrorAction Stop).AdminStatus";

    // --- Сброс стека -------------------------------------------------------------------------

    public static IReadOnlyList<string> WinsockReset => ["winsock", "reset"];

    public static IReadOnlyList<string> IpReset => ["int", "ip", "reset"];

    // --- Wi-Fi ---------------------------------------------------------------------------------

    public static string WifiListArguments => "wlan show profiles";

    /// <summary>
    /// Имя профиля Wi-Fi: netsh читает <c>name="…"</c> своими правилами, и кавычка внутри
    /// закрыла бы значение и добавила ключ. Поэтому кавычек и управляющих знаков нет вовсе.
    /// </summary>
    public static bool IsWifiProfileName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 64 && WifiName().IsMatch(value);

    public static string WifiDeleteArguments(string profile) => $"wlan delete profile name=\"{profile}\"";

    public static string WifiExportArguments(string profile, string folder) =>
        $"wlan export profile name=\"{profile}\" folder=\"{folder}\"";

    public static string WifiAddProfileArguments(string xmlFile) => $"wlan add profile filename=\"{xmlFile}\"";

    /// <summary>Путь, который можно вставить в кавычки netsh.</summary>
    public static bool IsNetshPath(string path) => !path.Contains('"') && !path.Any(char.IsControl);

    // --- Трассировка ---------------------------------------------------------------------------

    public static IReadOnlyList<string> TracertArguments(string host, int maxHops) =>
        ["-d", "-h", Math.Clamp(maxHops, 1, MaxHops).ToString(System.Globalization.CultureInfo.InvariantCulture), "-w", "1000", host];

    /// <summary>
    /// Потолок трассировки: на каждый узел tracert шлёт три пробы по секунде ожидания, плюс запас.
    /// </summary>
    public static int TracertTimeoutSeconds(int maxHops) => Math.Clamp(maxHops, 1, MaxHops) * 4 + 15;

    // --- hosts ---------------------------------------------------------------------------------

    public static string HostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");

    /// <summary>
    /// Дописывает строку «адрес имя». Если имя уже указано на тот же адрес — ничего не меняет;
    /// на другой адрес — прежние строки с этим именем снимаются, иначе Windows взяла бы первую.
    /// </summary>
    public static (List<string> Lines, bool Changed) HostsAdd(IReadOnlyList<string> lines, string address, string host)
    {
        var result = new List<string>(lines.Count + 1);
        var alreadyThere = false;
        var removed = false;
        foreach (var line in lines)
        {
            if (HostsEntry(line) is { } entry && entry.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                if (entry.Address.Equals(address, StringComparison.OrdinalIgnoreCase) && entry.Hosts.Count == 1)
                {
                    alreadyThere = true;
                    result.Add(line);
                    continue;
                }

                removed = true;
                var others = entry.Hosts.Where(name => !name.Equals(host, StringComparison.OrdinalIgnoreCase)).ToList();
                if (others.Count > 0)
                {
                    result.Add(entry.Address + "\t" + string.Join(" ", others) + entry.Comment);
                }

                continue;
            }

            result.Add(line);
        }

        if (!alreadyThere)
        {
            while (result.Count > 0 && result[^1].Length == 0)
            {
                result.RemoveAt(result.Count - 1);
            }

            result.Add(address + "\t" + host);
        }

        return (result, removed || !alreadyThere);
    }

    /// <summary>Снимает имя со всех строк hosts. Строка без других имён уходит целиком.</summary>
    public static (List<string> Lines, int Removed) HostsRemove(IReadOnlyList<string> lines, string host)
    {
        var result = new List<string>(lines.Count);
        var removed = 0;
        foreach (var line in lines)
        {
            if (HostsEntry(line) is { } entry && entry.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                removed++;
                var others = entry.Hosts.Where(name => !name.Equals(host, StringComparison.OrdinalIgnoreCase)).ToList();
                if (others.Count > 0)
                {
                    result.Add(entry.Address + "\t" + string.Join(" ", others) + entry.Comment);
                }

                continue;
            }

            result.Add(line);
        }

        return (result, removed);
    }

    public static string JoinHosts(IEnumerable<string> lines) => string.Join("\r\n", lines) + "\r\n";

    private sealed record HostsLine(string Address, List<string> Hosts, string Comment);

    /// <summary>Разбор строки hosts; комментарий и пустая строка — не запись.</summary>
    private static HostsLine? HostsEntry(string line)
    {
        var hash = line.IndexOf('#');
        var body = hash < 0 ? line : line[..hash];
        var comment = hash < 0 ? "" : " " + line[hash..];
        var parts = body.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length < 2 || !IPAddress.TryParse(parts[0], out _)
            ? null
            : new HostsLine(parts[0], [.. parts[1..]], comment);
    }

    [GeneratedRegex(@"^[^""\p{C}]+$")]
    private static partial Regex WifiName();
}
