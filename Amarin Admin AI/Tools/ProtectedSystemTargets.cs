using System.Collections.Frozen;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;

namespace Amarin.Tools;

/// <summary>
/// Процессы и службы, без которых Windows не работает: их нельзя остановить ни по просьбе,
/// ни с подтверждением.
/// </summary>
/// <remarks>
/// <para>
/// Жёсткий отказ, а не вопрос — по образцу <see cref="LocalUsersSafety"/>. Остановленный csrss
/// или lsass роняет систему в синий экран, остановленный RpcSs или EventLog ломает половину
/// Windows, и человек, привычно нажавший «Да» на двадцатом вопросе, не должен одним нажатием
/// выключать машину или защиту.
/// </para>
/// <para>
/// Проверка стоит и в самих инструментах, и в разборе PowerShell — второй путь к той же
/// остановке не должен оставаться открытым.
/// </para>
/// </remarks>
internal static class ProtectedSystemTargets
{
    /// <summary>Процессы по имени без <c>.exe</c>.</summary>
    internal static readonly FrozenSet<string> Processes = new[]
    {
        "csrss", "lsass", "lsaiso", "winlogon", "wininit", "smss", "services", "svchost",
        "system", "registry", "memcompression", "memory compression", "dwm", "msmpeng",
        "nissrv", "securityhealthservice", "fontdrvhost", "idle"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Службы по имени. Центр обновления (wuauserv, BITS) сюда не входит намеренно: его
    /// перезапуск — обычный способ починить зависшее обновление.
    /// </summary>
    internal static readonly FrozenSet<string> Services = new[]
    {
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs", "EventLog", "Winmgmt",
        "WinDefend", "WdNisSvc", "WdNisDrv", "Sense", "SecurityHealthService", "mpssvc", "BFE",
        "Dnscache", "PlugPlay", "Power", "ProfSvc", "gpsvc", "BrokerInfrastructure",
        "CoreMessagingRegistrar", "SystemEventsBroker", "Schedule", "CryptSvc", "Netlogon",
        "KeyIso", "VaultSvc", "Winlogon"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Имя процесса без пути, расширения и пробелов по краям.</summary>
    internal static string NormalizeProcessName(string? name)
    {
        var value = (name ?? "").Trim().Trim('"');
        var slash = value.LastIndexOfAny(['\\', '/']);
        value = slash >= 0 ? value[(slash + 1)..] : value;
        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    internal static bool IsProtectedProcess(string? name) =>
        Processes.Contains(NormalizeProcessName(name));

    internal static bool IsProtectedService(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Services.Contains(name.Trim().Trim('"'));

    /// <summary>
    /// Отказ на остановку процесса для <c>windows_process stop/kill</c>, если цель защищена.
    /// </summary>
    /// <param name="nameOfPid">Как узнать имя процесса по PID. По умолчанию — живой список процессов.</param>
    internal static bool TryGetProcessBlock(
        JsonElement arguments,
        out string reason,
        Func<int, string?>? nameOfPid = null)
    {
        reason = "";
        var action = DangerousActionGuard.ActionOf(arguments);
        if (action is not ("stop" or "kill"))
        {
            return false;
        }

        if (arguments.TryGetProperty("pid", out var pidProp) && pidProp.TryGetInt32(out var pid))
        {
            if (pid == Environment.ProcessId)
            {
                reason = SelfRefusal();
                return true;
            }

            if (pid is 0 or 4)
            {
                reason = ProcessRefusal("System");
                return true;
            }

            var byPid = (nameOfPid ?? LiveProcessName)(pid);
            if (IsProtectedProcess(byPid))
            {
                reason = ProcessRefusal(NormalizeProcessName(byPid));
                return true;
            }

            return false;
        }

        if (arguments.TryGetProperty("process_name", out var nameProp) &&
            nameProp.ValueKind == JsonValueKind.String)
        {
            var name = NormalizeProcessName(nameProp.GetString());
            if (IsProtectedProcess(name))
            {
                reason = ProcessRefusal(name);
                return true;
            }

            if (IsSelf(name))
            {
                reason = SelfRefusal();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Отказ на остановку службы для <c>windows_service stop/restart</c>, если она защищена.
    /// </summary>
    /// <param name="serviceNameOf">
    /// Как превратить то, что прислала модель, в имя службы: отображаемое имя «Windows Event Log»
    /// ServiceController понимает так же, как «EventLog». По умолчанию — живой вызов Windows.
    /// </param>
    internal static bool TryGetServiceBlock(
        JsonElement arguments,
        out string reason,
        Func<string, string?>? serviceNameOf = null)
    {
        reason = "";
        var action = DangerousActionGuard.ActionOf(arguments);
        if (action is not ("stop" or "restart"))
        {
            return false;
        }

        if (!arguments.TryGetProperty("service_name", out var nameProp) ||
            nameProp.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var given = nameProp.GetString()?.Trim() ?? "";
        var resolved = IsProtectedService(given) ? given : (serviceNameOf ?? LiveServiceName)(given);
        if (IsProtectedService(given) || IsProtectedService(resolved))
        {
            reason = ServiceRefusal(IsProtectedService(given) ? given : resolved!);
            return true;
        }

        return false;
    }

    internal static string ProcessRefusal(string name) =>
        $"ЗАПРЕЩЕНО: процесс {name} — часть ядра Windows или её защиты; его остановка роняет " +
        "систему или отключает защиту. Действие отклонено до подтверждения. Не повторяй попытку " +
        "ни этим инструментом, ни через PowerShell: скажи пользователю, какую задачу ты решал, " +
        "и предложи другой путь.";

    internal static string ServiceRefusal(string name) =>
        $"ЗАПРЕЩЕНО: служба {name} нужна самой Windows или её защите; её остановка ломает систему " +
        "или отключает защиту. Действие отклонено до подтверждения. Не повторяй попытку ни этим " +
        "инструментом, ни через PowerShell: скажи пользователю, какую задачу ты решал, и " +
        "предложи другой путь.";

    private static string SelfRefusal() =>
        "ЗАПРЕЩЕНО: это процесс самой программы Amarin Admin AI — остановить его значит оборвать " +
        "текущую работу и закрыть окно. Действие отклонено. Не повторяй попытку; если программу " +
        "нужно закрыть, скажи об этом пользователю.";

    private static bool IsSelf(string name)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return string.Equals(self.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? LiveProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? LiveServiceName(string given)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(given))
        {
            return null;
        }

        try
        {
            using var controller = new ServiceController(given);
            return controller.ServiceName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
