using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Что вернёт обратный шаг отката.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UndoKind>))]
internal enum UndoKind
{
    /// <summary>DNS-серверы адаптера: прежние статические или «от DHCP».</summary>
    Dns,

    /// <summary>Файл hosts — копией, снятой до первой правки.</summary>
    HostsFile,

    /// <summary>Сетевой адаптер включён или выключен, как был.</summary>
    Adapter,

    /// <summary>Устройство включено или отключено, как было.</summary>
    Device,

    /// <summary>Правила брандмауэра — прежнее «включено» у каждого.</summary>
    FirewallRules,

    /// <summary>Забытый профиль Wi-Fi — заново из экспорта.</summary>
    WifiProfile
}

/// <summary>
/// Обратный шаг: то, что нельзя вернуть сравнением реестра, служб и задач, — DNS адаптера,
/// файл hosts, включённость адаптера и устройства, правила брандмауэра, профиль Wi-Fi.
/// </summary>
/// <remarks>
/// В снимке лежит <b>состояние</b>, а не команда: команду собирает <see cref="UndoCommands"/> в
/// момент отката, проверяя каждое значение. Скрипт, записанный в папку снимков и исполняемый при
/// откате — возможно, программой, запущенной от администратора, — был бы готовым способом
/// подсунуть ей свой код: папка доступна пользователю на запись.
/// </remarks>
internal sealed class UndoStep
{
    public UndoKind Kind { get; set; }

    /// <summary>Имя адаптера, идентификатор устройства, имя профиля Wi-Fi.</summary>
    public string Target { get; set; } = "";

    /// <summary>Адреса DNS; имена правил брандмауэра.</summary>
    public List<string> Values { get; set; } = [];

    /// <summary>Включено ли было каждое правило из <see cref="Values"/>.</summary>
    public List<bool> States { get; set; } = [];

    /// <summary>Было ли включено (адаптер, устройство); у DNS — брались ли адреса от DHCP.</summary>
    public bool Enabled { get; set; }

    /// <summary>Имя файла в папке снимка: копия hosts, экспорт профиля Wi-Fi.</summary>
    public string? File { get; set; }

    /// <summary>Шаг с тем же видом и целью: второй такой не пишется — прежнее состояние у первого.</summary>
    public bool SameTargetAs(UndoStep other) =>
        Kind == other.Kind && string.Equals(Target, other.Target, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Команды обратных шагов. Без Windows — проверяются тестами напрямую.</summary>
internal static partial class UndoCommands
{
    /// <summary>Скрипт PowerShell шага или null, если шаг не проходит проверку.</summary>
    /// <param name="snapshotDir">Папка снимка: файлы шага ищутся только в ней.</param>
    public static string? Script(UndoStep step, string snapshotDir)
    {
        switch (step.Kind)
        {
            case UndoKind.Dns:
                if (!IsInterfaceAlias(step.Target))
                {
                    return null;
                }

                if (step.Enabled || step.Values.Count == 0)
                {
                    return NetworkCommands.ResetDnsScript(step.Target);
                }

                return step.Values.All(IsIpAddress) ? NetworkCommands.SetDnsScript(step.Target, step.Values) : null;

            case UndoKind.Adapter:
                return IsInterfaceAlias(step.Target) ? NetworkCommands.AdapterScript(step.Target, step.Enabled) : null;

            case UndoKind.Device:
                return DeviceCommands.IsInstanceId(step.Target)
                    ? DeviceCommands.SetEnabledScript(step.Target, step.Enabled)
                    : null;

            case UndoKind.FirewallRules:
                return FirewallRulesScript(step);

            default:
                // Hosts и Wi-Fi возвращаются не скриптом: файл копируется, профиль ставит netsh
                // (см. ChangeRollbackOperations.ApplyUndo).
                return null;
        }
    }

    /// <summary>Файл шага внутри папки снимка; путь из снимка наружу не выводит.</summary>
    public static string? SnapshotFile(UndoStep step, string snapshotDir)
    {
        if (string.IsNullOrWhiteSpace(step.File))
        {
            return null;
        }

        var name = Path.GetFileName(step.File);
        if (name.Length == 0 || name != step.File || name.Contains('"'))
        {
            return null;
        }

        var path = Path.Combine(snapshotDir, name);
        return System.IO.File.Exists(path) ? path : null;
    }

    /// <summary>Аргументы <c>netsh</c> для профиля Wi-Fi из экспорта или null.</summary>
    public static string? WifiRestoreArguments(UndoStep step, string snapshotDir) =>
        step.Kind == UndoKind.WifiProfile &&
        NetworkCommands.IsWifiProfileName(step.Target) &&
        SnapshotFile(step, snapshotDir) is { } xml
            ? NetworkCommands.WifiAddProfileArguments(xml)
            : null;

    /// <summary>Строка плана — на языке интерфейса.</summary>
    public static string Describe(UndoStep step) => step.Kind switch
    {
        UndoKind.Dns when step.Enabled || step.Values.Count == 0 => Loc.Format("S.Rollback.Undo.DnsDhcp", step.Target),
        UndoKind.Dns => Loc.Format("S.Rollback.Undo.Dns", step.Target, string.Join(", ", step.Values)),
        UndoKind.HostsFile => Loc.Get("S.Rollback.Undo.Hosts"),
        UndoKind.Adapter => Loc.Format(step.Enabled ? "S.Rollback.Undo.AdapterOn" : "S.Rollback.Undo.AdapterOff", step.Target),
        UndoKind.Device => Loc.Format(step.Enabled ? "S.Rollback.Undo.DeviceOn" : "S.Rollback.Undo.DeviceOff", step.Target),
        UndoKind.FirewallRules => Loc.Format("S.Rollback.Undo.Firewall", step.Values.Count),
        _ => Loc.Format("S.Rollback.Undo.Wifi", step.Target)
    };

    /// <summary>Имя сетевого интерфейса: без подстановочных знаков и управляющих символов.</summary>
    public static bool IsInterfaceAlias(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && PlainName().IsMatch(value);

    public static bool IsIpAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && IPAddress.TryParse(value.Trim(), out _);

    private static string? FirewallRulesScript(UndoStep step)
    {
        if (step.Values.Count == 0 || step.Values.Count != step.States.Count || step.Values.Count > 200)
        {
            return null;
        }

        var script = new StringBuilder("$ErrorActionPreference = 'Continue'\n");
        for (var i = 0; i < step.Values.Count; i++)
        {
            var name = step.Values[i];
            if (!PlainName().IsMatch(name) || name.Length > 512)
            {
                return null;
            }

            script.Append("Set-NetFirewallRule -Name '")
                .Append(PowerShellHelper.QuoteLiteral(name))
                .Append("' -Enabled ")
                .Append(step.States[i] ? "True" : "False")
                .Append('\n');
        }

        return script.ToString();
    }

    /// <summary>Имя без подстановочных знаков PowerShell и без управляющих символов.</summary>
    [GeneratedRegex(@"^[^\p{C}*?\[\]`]+$")]
    private static partial Regex PlainName();
}
