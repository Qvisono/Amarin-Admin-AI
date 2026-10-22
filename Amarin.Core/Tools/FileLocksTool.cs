using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// «Файл занят другим процессом»: кто держит файл или папку и, с согласия человека, бережное
/// закрытие этого держателя.
/// </summary>
/// <remarks>
/// Закрывают только того, кто сейчас держит именно этот путь: PID из чужого вопроса здесь
/// ничего не закроет. Сама программа, ядро Windows и её защита не закрываются никогда.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FileLocksTool : ITool
{
    public const string ToolName = "file_locks";

    /// <summary>Столько файлов папки проверяется за раз: больше Restart Manager разбирает долго.</summary>
    internal const int MaxFolderFiles = 2_000;

    public string Name => ToolName;

    public string Description =>
        "Which processes hold a file or the files of a folder open (Restart Manager), for errors that a file is in " +
        "use by another process. find lists the holders with PID, program, service and type. close asks one holder, " +
        "by its PID from find, to close the way Windows Update does; the program may save its work or refuse. " +
        "force closes it even if it does not answer, and its unsaved work is lost. close needs user confirmation; " +
        "this app, Windows core processes and protected services are never closed.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "action": {
              "type": "string",
              "enum": ["find", "close"],
              "description": "find: who holds the path; close: ask one holder to close"
            },
            "path": {
              "type": "string",
              "description": "Full path of the file or folder"
            },
            "pid": {
              "type": "integer",
              "description": "close: the PID of a holder exactly as find shows it"
            },
            "force": {
              "type": "boolean",
              "description": "close: end the program even if it does not answer or asks to save (unsaved work is lost)"
            }
          },
          "required": ["action", "path"]
        }
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default) =>
        Task.Run(() => Execute(arguments, cancellationToken), cancellationToken);

    private static ToolResult Execute(JsonElement arguments, CancellationToken cancellationToken)
    {
        var action = DangerousActionGuard.ActionOf(arguments);
        if (!TryFiles(FileToolPaths.String(arguments, "path"), out var path, out var files, out var capped, out var error))
        {
            return ToolResult.Fail(error);
        }

        var holders = RestartManager.Holders(files, out var code);
        if (holders is null)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Locks.RmFailed", code));
        }

        return action switch
        {
            "find" => Find(path, files.Count, capped, holders),
            "close" => Close(arguments, path, holders, cancellationToken),
            _ => ToolResult.Fail($"Unknown action: {action}")
        };
    }

    /// <summary>
    /// Запрет без вопроса: свой процесс и процессы ядра Windows. Его зовёт шлюз; критические
    /// процессы и службы проверяет сам инструмент — это знает только Restart Manager.
    /// </summary>
    internal static bool TryGetHardBlockReason(JsonElement arguments, out string reason)
    {
        reason = "";
        if (DangerousActionGuard.ActionOf(arguments) != "close" || FileToolPaths.Int(arguments, "pid") is not { } pid)
        {
            return false;
        }

        // Тот же разбор, что у windows_process stop: свой процесс, System и ядро Windows.
        var stop = JsonSerializer.SerializeToElement(new { action = "stop", pid });
        return ProtectedSystemTargets.TryGetProcessBlock(stop, out reason);
    }

    private static ToolResult Find(string path, int checkedFiles, bool capped, List<LockHolder> holders)
    {
        var sb = new StringBuilder();
        var folder = Directory.Exists(path);
        sb.AppendLine(folder
            ? $"Folder: {path} ({checkedFiles} files checked{(capped ? $", only the first {MaxFolderFiles}" : "")})"
            : $"File: {path}");

        if (holders.Count == 0)
        {
            sb.AppendLine("No process holds it open now.");
            if (folder)
            {
                sb.AppendLine("A folder can also be held as the current folder of a program or a command prompt; this check does not see that.");
            }

            return ToolResult.Ok(sb.ToString().TrimEnd());
        }

        sb.AppendLine($"Held by {holders.Count}:");
        foreach (var holder in holders)
        {
            sb.AppendLine($"  PID {holder.Pid}  {ProcessName(holder.Pid)}  \"{holder.App}\"  type: {Kind(holder.Type)}" +
                          (holder.Service.Length > 0 ? $"  service: {holder.Service}" : "") +
                          (holder.Restartable ? "  restartable" : ""));
        }

        sb.AppendLine(Loc.Get("S.Tool.Locks.Hint"));
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult Close(JsonElement arguments, string path, List<LockHolder> holders, CancellationToken cancellationToken)
    {
        if (FileToolPaths.Int(arguments, "pid") is not { } pid)
        {
            return ToolResult.Fail("pid is required: the PID of a holder as find shows it.");
        }

        if (TryGetHardBlockReason(arguments, out var blocked))
        {
            return ToolResult.Fail(blocked);
        }

        if (holders.FirstOrDefault(holder => holder.Pid == pid) is not { } target)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Locks.NotHolder", pid, path));
        }

        if (target.Type == RmAppType.Critical || ProtectedSystemTargets.IsProtectedService(target.Service))
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Locks.Protected", target.App.Length > 0 ? target.App : ProcessName(pid)));
        }

        var name = ProcessName(pid);
        var force = FileToolPaths.Flag(arguments, "force");
        var result = RestartManager.Shutdown(target, force, cancellationToken, out var code);
        return result switch
        {
            RestartManager.ShutdownResult.Closed => ToolResult.Ok(
                $"{name} (PID {pid}) closed{(target.Type == RmAppType.Explorer ? " and Explorer started again" : "")}." +
                (StillHeld(path) ? "\n" + Loc.Format("S.Tool.Locks.StillHeld", path) : "")),
            RestartManager.ShutdownResult.Refused => ToolResult.Fail(Loc.Format("S.Tool.Locks.Refused", name, pid)),
            RestartManager.ShutdownResult.AccessDenied => ToolResult.Fail(Loc.Get("S.Tool.NeedsAdmin")),
            _ => ToolResult.Fail(Loc.Format("S.Tool.Locks.RmFailed", code))
        };
    }

    /// <summary>Держит ли путь ещё кто-нибудь: у файла бывает несколько держателей сразу.</summary>
    private static bool StillHeld(string path) =>
        TryFiles(path, out _, out var files, out _, out _) &&
        RestartManager.Holders(files, out _) is { Count: > 0 };

    /// <summary>Файл — сам по себе; папка — файлы внутри, без перехода по ссылкам и не больше предела.</summary>
    private static bool TryFiles(string? raw, out string path, out List<string> files, out bool capped, out string error)
    {
        path = (raw ?? "").Trim().Trim('"');
        files = [];
        capped = false;
        error = "";
        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            error = "path must be a full path of a file or folder. Do not retry with a relative path.";
            return false;
        }

        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            files.Add(path);
            return true;
        }

        if (!Directory.Exists(path))
        {
            error = $"{path} does not exist.";
            return false;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            if (files.Count >= MaxFolderFiles)
            {
                capped = true;
                break;
            }

            files.Add(file);
        }

        return true;
    }

    private static string ProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "(exited)";
        }
    }

    private static string Kind(RmAppType type) => type switch
    {
        RmAppType.MainWindow or RmAppType.OtherWindow => "window",
        RmAppType.Service => "service",
        RmAppType.Explorer => "Explorer",
        RmAppType.Console => "console",
        RmAppType.Critical => "critical (never closed)",
        _ => "unknown"
    };
}
