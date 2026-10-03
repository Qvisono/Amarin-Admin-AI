using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>Как достучаться до машины.</summary>
public enum RemoteMethod
{
    /// <summary>WinRM (WS-Management): штатный путь Windows, логин и пароль.</summary>
    WinRm,

    /// <summary>SSH через PowerShell 7: только ключ, пароль этот путь не принимает.</summary>
    Ssh
}

/// <summary>Удалённая машина из списка «Машины».</summary>
public sealed class RemoteMachine
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Имя или адрес в сети.</summary>
    public string Address { get; set; } = "";

    public RemoteMethod Method { get; set; } = RemoteMethod.WinRm;

    public string User { get; set; } = "";

    /// <summary>Пароль под DPAPI текущего пользователя Windows; открытым текстом на диск не попадает.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>Путь к закрытому ключу — для SSH.</summary>
    public string? KeyFile { get; set; }

    /// <summary>Пароль в открытом виде — только в памяти, для запуска.</summary>
    [JsonIgnore]
    public string? Password => DataProtector.Unprotect(ProtectedPassword);
}

/// <summary>
/// Машина, на которой исполняются команды хода. Ambient по образцу <see cref="AgentRunScope"/>:
/// ход чата ставит её один раз, и её видят и агент, и шлюз, и исполнитель PowerShell, и аудит.
/// </summary>
internal static class ExecutionTarget
{
    private static readonly AsyncLocal<RemoteMachine?> CurrentMachine = new();

    public static RemoteMachine? Current => CurrentMachine.Value;

    public static IDisposable Push(RemoteMachine? machine)
    {
        var previous = CurrentMachine.Value;
        CurrentMachine.Value = machine;
        return new Restore(previous);
    }

    private sealed class Restore(RemoteMachine? previous) : IDisposable
    {
        public void Dispose() => CurrentMachine.Value = previous;
    }
}

/// <summary>
/// Что можно выполнить на удалённой машине. Белый список, а не чёрный: инструменты, которые
/// ходят в .NET, реестр или запускают sc.exe и netsh, сделали бы это <b>здесь</b>, на этом ПК, —
/// и молча, потому что со стороны модели разницы не видно.
/// </summary>
internal static class RemoteSupport
{
    public static readonly string[] Remotable = ["run_powershell"];

    /// <summary>Не касаются машины вовсе: сеть, картинки, инструкции, агент.</summary>
    public static readonly string[] MachineFree =
    [
        "web_search", "scrape_url", "generate_image", "fetch_image", "youtube_transcript", "read_instruction",
        "init_agent", AgentPlans.SubmitTool
    ];

    public static bool Allows(string tool) =>
        Remotable.Contains(tool.Trim(), StringComparer.OrdinalIgnoreCase) ||
        MachineFree.Contains(tool.Trim(), StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Обёртка скрипта для удалённой машины: внешний <c>powershell.exe</c> (или <c>pwsh</c> для SSH)
/// выполняет <c>Invoke-Command</c>, а сам скрипт едет внутрь строкой Base64.
/// </summary>
/// <remarks>
/// Пароль читается из stdin дочернего процесса: в командной строке его видел бы любой процесс
/// машины (список процессов показывает аргументы), в переменной окружения — любой его потомок.
/// </remarks>
internal static class RemoteScript
{
    public static string Wrap(string script, RemoteMachine machine)
    {
        var inner = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var host = PowerShellHelper.QuoteLiteral(machine.Address.Trim());
        var user = PowerShellHelper.QuoteLiteral(machine.User.Trim());
        var body = $"$__inner = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{inner}'))\n" +
                   "$__block = [scriptblock]::Create($__inner)\n";

        if (machine.Method == RemoteMethod.Ssh)
        {
            var key = string.IsNullOrWhiteSpace(machine.KeyFile)
                ? ""
                : $" -KeyFilePath '{PowerShellHelper.QuoteLiteral(machine.KeyFile.Trim())}'";
            return body + $"Invoke-Command -HostName '{host}' -UserName '{user}'{key} -ScriptBlock $__block -ErrorAction Stop";
        }

        return body +
               "$__secret = [Console]::In.ReadLine()\n" +
               "$__secure = ConvertTo-SecureString -String $__secret -AsPlainText -Force\n" +
               "Remove-Variable __secret\n" +
               $"$__cred = New-Object System.Management.Automation.PSCredential('{user}', $__secure)\n" +
               $"Invoke-Command -ComputerName '{host}' -Credential $__cred -ScriptBlock $__block -ErrorAction Stop";
    }

    /// <summary>Что подать на stdin: пароль для WinRM, ничего для SSH.</summary>
    public static string? Input(RemoteMachine machine) =>
        machine.Method == RemoteMethod.WinRm ? machine.Password ?? "" : null;

    /// <summary>Проверка связи: WinRM отвечает, и на той стороне исполняется команда.</summary>
    public const string ProbeScript = "\"$env:COMPUTERNAME\"";

    /// <summary>Где искать PowerShell 7 для SSH. Null — не установлен.</summary>
    public static string? FindPwsh()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetEnvironmentVariable("ProgramW6432")
                 })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var candidate = Path.Combine(root, "PowerShell", "7", "pwsh.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Список машин профиля — <c>machines.json</c>.</summary>
internal sealed class MachineBook
{
    internal const string FileName = "machines.json";

    private readonly Lock _gate = new();
    private string _root;

    public MachineBook(string root) => _root = root;

    public void UseRoot(string root)
    {
        lock (_gate)
        {
            _root = root;
        }
    }

    public List<RemoteMachine> Load()
    {
        string path;
        lock (_gate)
        {
            path = Path.Combine(_root, FileName);
        }

        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<List<RemoteMachine>>(File.ReadAllText(path), AppJson.Options) ?? [])
                    .Where(machine => machine is not null && !string.IsNullOrWhiteSpace(machine.Id)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public RemoteMachine? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : Load().FirstOrDefault(machine => machine.Id == id);

    public bool Save(IReadOnlyList<RemoteMachine> machines)
    {
        try
        {
            lock (_gate)
            {
                AppDataFile.WriteAtomic(Path.Combine(_root, FileName), JsonSerializer.Serialize(machines, AppJson.Options));
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Адрес, логин и путь к ключу подставляются в скрипт — поэтому только то, что может быть
    /// именем узла, учётной записью и путём, без кавычек и управляющих знаков.
    /// </summary>
    internal static string? Validate(RemoteMachine machine)
    {
        if (string.IsNullOrWhiteSpace(machine.Name))
        {
            return Loc.Get("S.Machines.NeedName");
        }

        if (!DnsConfigTool.IsHostName(machine.Address.Trim()))
        {
            return Loc.Get("S.Machines.BadAddress");
        }

        if (string.IsNullOrWhiteSpace(machine.User) || machine.User.Any(c => char.IsControl(c) || c is '\'' or '"' or '`' or '$' or ';'))
        {
            return Loc.Get("S.Machines.BadUser");
        }

        if (machine.Method == RemoteMethod.Ssh && machine.KeyFile is { Length: > 0 } key &&
            key.Any(c => char.IsControl(c) || c is '\'' or '"' or '`' or '$'))
        {
            return Loc.Get("S.Machines.BadKey");
        }

        return null;
    }
}

/// <summary>Правило для модели о цели хода — без примеров, только граница.</summary>
internal static class RemoteBriefing
{
    public static string For(RemoteMachine? machine) =>
        machine is null
            ? ""
            : $"""
               TARGET MACHINE: every command runs on the remote computer "{machine.Name}" ({machine.Address}) as {machine.User}, not on this PC.
               - Only run_powershell reaches it; other system tools are refused in this chat.
               - Paths, services, disks and settings are that computer's. Do not use paths of this PC.
               """;
}
