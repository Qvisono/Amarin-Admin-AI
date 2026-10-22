using System.Collections.Frozen;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Переменные среды пользователя и машины и PATH: показать, разобрать и поправить с откатом.
/// </summary>
/// <remarks>
/// <para>
/// PATH меняется только записями (<c>path_add</c>, <c>path_remove</c>, <c>path_clean</c>), а не
/// целиком: одно неверное <c>set Path</c> стёрло бы все пути разом, и система перестала бы
/// находить свои же программы.
/// </para>
/// <para>
/// Значения, похожие на секрет, модели не показываются: ключ API, оставленный человеком в
/// переменной для другой программы, ушёл бы провайдеру модели в первом же <c>list</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class EnvironmentTool : ITool
{
    public const string ToolName = "environment";

    /// <summary>
    /// Переменные, которые Windows задаёт сама: значение в разделе окружения перекрыло бы
    /// настоящее, и сломалось бы то, что на них опирается, вплоть до самой оболочки и PowerShell.
    /// </summary>
    private static readonly FrozenSet<string> ProtectedNames = new[]
    {
        "ComSpec", "windir", "SystemRoot", "SystemDrive", "OS", "PATHEXT", "PSModulePath", "DriverData",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
        "NUMBER_OF_PROCESSORS", "USERNAME", "USERPROFILE", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE",
        "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "HOMESHARE", "ProgramData", "ProgramFiles",
        "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "CommonProgramW6432", "PUBLIC", "ALLUSERSPROFILE", "COMPUTERNAME", "LOGONSERVER", "SESSIONNAME"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Части имени, по которым значение считается секретом, каким бы ни был его вид.</summary>
    private static readonly string[] SecretMarkers =
        ["KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PWD", "CREDENTIAL", "AUTH", "PRIVATE", "CONNECTIONSTRING"];

    /// <summary>Длиннее этого значение в списке обрезается: целиком его отдаёт <c>get</c>.</summary>
    private const int ListValueLimit = 300;

    private readonly Func<IEnumerable<string?>> _knownSecrets;
    private readonly EnvironmentStore _store;
    private readonly Func<string, PathExistence> _exists;
    private readonly Func<string, string> _expand;

    public EnvironmentTool()
        : this(() => [])
    {
    }

    /// <param name="knownSecrets">Ключи API программы: их значения скрыты, как бы ни звалась переменная.</param>
    /// <param name="store">Хранилище; тесты пишут в свой раздел реестра.</param>
    /// <param name="exists">Есть ли папка записи PATH; тесты подставляют свой диск.</param>
    internal EnvironmentTool(
        Func<IEnumerable<string?>> knownSecrets,
        EnvironmentStore? store = null,
        Func<string, PathExistence>? exists = null)
    {
        _knownSecrets = knownSecrets;
        _store = store ?? new EnvironmentStore();
        _exists = exists ?? PathAnalysis.Probe;
        _expand = Environment.ExpandEnvironmentVariables;
    }

    public string Name => ToolName;

    public string Description =>
        "Environment variables of the user and of the machine, and the PATH list. list, get and path_check read; " +
        "path_check finds duplicate, missing, empty and relative PATH entries, undefined variables and length limits. " +
        "set, delete, path_add, path_remove and path_clean change the registry with the value type kept, need user " +
        "confirmation and are snapshotted for rollback; programs started afterwards see the change. PATH changes only " +
        "through path_add, path_remove and path_clean. Values that look like secrets are hidden.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["list", "get", "path_check", "set", "delete", "path_add", "path_remove", "path_clean"],
              "description": "Environment operation"
            },
            "scope": {
              "type": "string",
              "enum": ["user", "machine"],
              "description": "user: the current account; machine: every account, changes need administrator rights. Reads show both when omitted, changes default to user"
            },
            "name": {
              "type": "string",
              "description": "Variable name for get, set and delete"
            },
            "value": {
              "type": "string",
              "description": "New value for set. %NAME% references stay as written and Windows expands them"
            },
            "path": {
              "type": "string",
              "description": "path_add: the folder to add; path_remove: the entry as path_check shows it"
            },
            "position": {
              "type": "string",
              "enum": ["start", "end"],
              "description": "path_add: put the folder first or last (default end)"
            }
          },
          "required": ["action"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.FromResult(Execute(arguments));

    private ToolResult Execute(JsonElement arguments)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        if (!TryScope(arguments, out var scope))
        {
            return ToolResult.Fail("scope must be user or machine. Do not retry with the same value.");
        }

        // Второй рубеж после шлюза — тот же запрет, если инструмент позвали мимо него.
        if (TryGetHardBlockReason(arguments, out var refusal))
        {
            return ToolResult.Fail(refusal);
        }

        try
        {
            return action switch
            {
                "list" => List(scope),
                "get" => Get(scope, FileToolPaths.String(arguments, "name")?.Trim()),
                "path_check" => PathCheck(scope),
                "set" => Set(scope ?? EnvironmentScope.User, arguments),
                "delete" => Delete(scope ?? EnvironmentScope.User, FileToolPaths.String(arguments, "name")?.Trim()),
                "path_add" => PathAdd(scope ?? EnvironmentScope.User, arguments),
                "path_remove" => PathRemove(scope ?? EnvironmentScope.User, FileToolPaths.String(arguments, "path")?.Trim()),
                "path_clean" => PathClean(scope ?? EnvironmentScope.User),
                _ => ToolResult.Fail($"Unknown action: {action}")
            };
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin"));
        }
        catch (IOException ex)
        {
            return ToolResult.Fail($"Registry error: {ex.Message}");
        }
    }

    /// <summary>
    /// Запрет без вопроса: системные переменные, PATH целиком и записи PATH внутри папки Windows.
    /// Его же зовёт шлюз, чтобы человека не спрашивали о том, что всё равно не выполнится.
    /// </summary>
    internal static bool TryGetHardBlockReason(JsonElement arguments, out string reason)
    {
        reason = "";
        var action = DangerousActionGuard.ActionOf(arguments);
        var name = FileToolPaths.String(arguments, "name")?.Trim();
        if (action is "set" or "delete" && !string.IsNullOrEmpty(name))
        {
            if (name.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                reason = Loc.Get("S.Tool.Env.WholePath");
                return true;
            }

            if (ProtectedNames.Contains(name))
            {
                reason = Loc.Format("S.Tool.Env.Protected", name);
                return true;
            }
        }

        if (action == "path_remove" && FileToolPaths.String(arguments, "path")?.Trim() is { Length: > 0 } entry &&
            IsWindowsEntry(entry))
        {
            reason = Loc.Format("S.Tool.Env.WindowsEntry", entry);
            return true;
        }

        return false;
    }

    private static bool IsWindowsEntry(string entry)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            return false;
        }

        var key = PathAnalysis.Key(Environment.ExpandEnvironmentVariables(entry));
        var root = PathAnalysis.Key(windows);
        return key == root || key.StartsWith(root + "\\", StringComparison.Ordinal);
    }

    private ToolResult List(EnvironmentScope? scope)
    {
        var sb = new StringBuilder();
        foreach (var current in Scopes(scope))
        {
            var variables = _store.Read(current);
            sb.AppendLine($"{PathAnalysis.Name(current)} variables ({_store.KeyOf(current)}): {variables.Count}");
            foreach (var variable in variables)
            {
                var shown = variable.Name.Equals("Path", StringComparison.OrdinalIgnoreCase)
                    ? $"({variable.Value.Length} chars, {PathAnalysis.Split(variable.Value).Count} entries; see path_check)"
                    : Shown(variable, ListValueLimit);
                sb.AppendLine($"  {variable.Name}{(variable.Expandable ? " [expand]" : "")} = {shown}");
            }

            sb.AppendLine();
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult Get(EnvironmentScope? scope, string? name)
    {
        if (!IsValidName(name))
        {
            return ToolResult.Fail("name is required: a variable name without '=' or control characters.");
        }

        var sb = new StringBuilder();
        foreach (var current in Scopes(scope))
        {
            if (_store.Get(current, name) is not { } variable)
            {
                sb.AppendLine($"{PathAnalysis.Name(current)}: not set");
                continue;
            }

            sb.AppendLine($"{PathAnalysis.Name(current)} ({(variable.Expandable ? "REG_EXPAND_SZ" : "REG_SZ")}): {Shown(variable, int.MaxValue)}");
            if (variable.Expandable && !IsSecret(variable))
            {
                sb.AppendLine($"  expanded: {_expand(variable.Value)}");
            }
        }

        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult PathCheck(EnvironmentScope? scope)
    {
        var machine = _store.Get(EnvironmentScope.Machine, "Path");
        var user = _store.Get(EnvironmentScope.User, "Path");
        var entries = PathAnalysis.Analyze(machine?.Value, user?.Value, _expand, _exists);

        var sb = new StringBuilder();
        foreach (var (current, variable) in new[] { (EnvironmentScope.Machine, machine), (EnvironmentScope.User, user) })
        {
            if (scope is { } only && only != current)
            {
                continue;
            }

            if (variable is null)
            {
                sb.AppendLine($"{PathAnalysis.Name(current)} PATH: not set");
                sb.AppendLine();
                continue;
            }

            var mine = entries.Where(entry => entry.Scope == current).ToList();
            sb.AppendLine($"{PathAnalysis.Name(current)} PATH ({(variable.Expandable ? "REG_EXPAND_SZ" : "REG_SZ")}): " +
                          $"{mine.Count} entries, {variable.Value.Length} chars stored, {_expand(variable.Value).Length} expanded");
            foreach (var entry in mine)
            {
                var problems = entry.Problems.Count == 0 ? "" : "  [" + string.Join("; ", entry.Problems) + "]";
                sb.AppendLine($"  {entry.Number}. {entry.Raw}{problems}");
            }

            if (!variable.Expandable && variable.Value.Contains('%'))
            {
                sb.AppendLine("  warning: stored as REG_SZ but contains %NAME%: Windows does not expand these references.");
            }

            if (variable.Value.Length == PathAnalysis.SetxLimit)
            {
                sb.AppendLine($"  warning: exactly {PathAnalysis.SetxLimit} chars, the length setx cuts values to: the end was probably lost.");
            }

            sb.AppendLine();
        }

        var combined = EnvironmentStore.Effective("Path", machine, user)?.Length ?? 0;
        sb.AppendLine($"Combined expanded PATH: {combined} of {EnvironmentStore.MaxValueLength} chars.");
        if (combined > EnvironmentStore.MaxValueLength)
        {
            sb.AppendLine("warning: over the limit of one variable: new processes get a cut PATH.");
        }

        var troubled = entries.Count(entry => entry.Problems.Count > 0);
        sb.AppendLine(troubled == 0 ? "No problems found." : $"Entries with problems: {troubled}.");
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private ToolResult Set(EnvironmentScope scope, JsonElement arguments)
    {
        var name = FileToolPaths.String(arguments, "name")?.Trim();
        if (!IsValidName(name))
        {
            return ToolResult.Fail("name is required: a variable name without '=' or control characters.");
        }

        var value = FileToolPaths.String(arguments, "value");
        if (value is null || value.Length > EnvironmentStore.MaxValueLength || value.Contains('\0'))
        {
            return ToolResult.Fail($"value is required: text up to {EnvironmentStore.MaxValueLength} chars. To remove a variable use delete.");
        }

        var existing = _store.Get(scope, name);
        var expandable = existing?.Expandable == true || value.Contains('%');
        _store.Write(scope, existing?.Name ?? name, value, expandable);
        return Applied($"{PathAnalysis.Name(scope)}: {name} {(existing is null ? "created" : "changed")} ({(expandable ? "REG_EXPAND_SZ" : "REG_SZ")}).");
    }

    private ToolResult Delete(EnvironmentScope scope, string? name)
    {
        if (!IsValidName(name))
        {
            return ToolResult.Fail("name is required: a variable name without '=' or control characters.");
        }

        return _store.Delete(scope, name)
            ? Applied($"{PathAnalysis.Name(scope)}: {name} deleted.")
            : ToolResult.Fail(Loc.Format("S.Tool.Env.NotFound", name, PathAnalysis.Name(scope)));
    }

    private ToolResult PathAdd(EnvironmentScope scope, JsonElement arguments)
    {
        var entry = FileToolPaths.String(arguments, "path")?.Trim();
        if (string.IsNullOrEmpty(entry) || entry.Length > 2048 || entry.Contains(';') || entry.Contains('"') ||
            entry.Any(char.IsControl) || (!Path.IsPathFullyQualified(_expand(entry)) && !entry.StartsWith('%')))
        {
            return ToolResult.Fail(
                "path must be one full folder path (a drive letter or %NAME% at the start), without ';' or quotes. Do not retry with the same value.");
        }

        var current = _store.Get(scope, "Path");
        if (PathAnalysis.Contains(current?.Value, entry, _expand))
        {
            return ToolResult.Ok($"{PathAnalysis.Name(scope)} PATH already contains {entry}. Nothing changed.");
        }

        var atStart = FileToolPaths.String(arguments, "position")?.Trim().Equals("start", StringComparison.OrdinalIgnoreCase) == true;
        var updated = PathAnalysis.Add(current?.Value, entry, atStart);
        if (Combined(scope, updated) > EnvironmentStore.MaxValueLength)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Env.TooLong", EnvironmentStore.MaxValueLength));
        }

        var expandable = current?.Expandable == true || updated.Contains('%');
        _store.Write(scope, current?.Name ?? "Path", updated, expandable);
        var note = _exists(_expand(entry)) == PathExistence.Missing ? "\nwarning: the folder does not exist yet." : "";
        return Applied($"{PathAnalysis.Name(scope)} PATH: {entry} added at the {(atStart ? "start" : "end")}.{note}");
    }

    private ToolResult PathRemove(EnvironmentScope scope, string? entry)
    {
        if (string.IsNullOrEmpty(entry))
        {
            return ToolResult.Fail("path is required: the entry to remove, as path_check shows it.");
        }

        var current = _store.Get(scope, "Path");
        var (updated, removed) = PathAnalysis.Remove(current?.Value, entry, _expand);
        if (current is null || removed == 0)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Env.EntryNotFound", entry, PathAnalysis.Name(scope)));
        }

        _store.Write(scope, current.Name, updated, current.Expandable);
        return Applied($"{PathAnalysis.Name(scope)} PATH: {removed} entr{(removed == 1 ? "y" : "ies")} {entry} removed.");
    }

    private ToolResult PathClean(EnvironmentScope scope)
    {
        var current = _store.Get(scope, "Path");
        var machine = scope == EnvironmentScope.User ? _store.Get(EnvironmentScope.Machine, "Path")?.Value : null;
        var (updated, removed) = PathAnalysis.Clean(scope, current?.Value, machine, _expand, _exists);
        if (current is null || removed.Count == 0)
        {
            return ToolResult.Ok($"{PathAnalysis.Name(scope)} PATH: nothing to clean.");
        }

        _store.Write(scope, current.Name, updated, current.Expandable);
        return Applied($"{PathAnalysis.Name(scope)} PATH: removed {removed.Count}:\n  " + string.Join("\n  ", removed));
    }

    /// <summary>Длина PATH процесса после правки одной из областей.</summary>
    private int Combined(EnvironmentScope scope, string updated)
    {
        var machine = _store.Get(EnvironmentScope.Machine, "Path");
        var user = _store.Get(EnvironmentScope.User, "Path");
        var changed = new EnvironmentVariable("Path", updated, updated.Contains('%'));
        return (scope == EnvironmentScope.User
            ? EnvironmentStore.Effective("Path", machine, changed)
            : EnvironmentStore.Effective("Path", changed, user))?.Length ?? 0;
    }

    private static ToolResult Applied(string line) => ToolResult.Ok(line + "\n" + Loc.Get("S.Tool.Env.Applied"));

    private string Shown(EnvironmentVariable variable, int limit)
    {
        if (IsSecret(variable))
        {
            return $"<hidden: looks like a secret, {variable.Value.Length} chars>";
        }

        return variable.Value.Length <= limit ? variable.Value : variable.Value[..limit] + $"… ({variable.Value.Length} chars, use get)";
    }

    private bool IsSecret(EnvironmentVariable variable) =>
        SecretMarkers.Any(marker => variable.Name.Contains(marker, StringComparison.OrdinalIgnoreCase)) ||
        ClipboardWriteTool.SecretReason(variable.Value, _knownSecrets()) is not null;

    private static IEnumerable<EnvironmentScope> Scopes(EnvironmentScope? scope) =>
        scope is { } only ? [only] : [EnvironmentScope.User, EnvironmentScope.Machine];

    private static bool TryScope(JsonElement arguments, out EnvironmentScope? scope)
    {
        scope = null;
        var raw = FileToolPaths.String(arguments, "scope")?.Trim().ToLowerInvariant();
        switch (raw)
        {
            case null or "":
                return true;
            case "user":
                scope = EnvironmentScope.User;
                return true;
            case "machine":
                scope = EnvironmentScope.Machine;
                return true;
            default:
                return false;
        }
    }

    private static bool IsValidName([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 255 && !name.Contains('=') && !name.Any(char.IsControl);
}
