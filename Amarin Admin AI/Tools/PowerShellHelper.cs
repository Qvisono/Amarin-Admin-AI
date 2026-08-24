using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.Tools;

internal static partial class PowerShellHelper
{
    /// <summary>
    /// Shared preamble prepended by <see cref="WrapScript"/> to every script run via
    /// <see cref="Run"/> / <see cref="RunAsync"/>. Suppresses progress CLIXML on stderr and forces UTF-8.
    /// Do not set ErrorActionPreference here — callers choose Stop vs SilentlyContinue per action.
    /// </summary>
    internal const string ScriptPreamble = """
        $ProgressPreference = 'SilentlyContinue'
        $InformationPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        if ($Host -and $Host.UI -and $Host.UI.RawUI) {
          try { $Host.UI.RawUI.WindowTitle = $Host.UI.RawUI.WindowTitle } catch { }
        }
        $OutputEncoding = [System.Text.Encoding]::UTF8
        $PSDefaultParameterValues['*:Encoding'] = 'utf8'
        """;

    public static Task<ToolResult> RunAsync(
        string script,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default,
        int maxOutput = 48_000) =>
        PowerShellProcessRunner.RunAsync(
            WrapScript(script),
            timeoutSeconds,
            cancellationToken,
            maxOutput,
            maxOutput / 2);

    public static ToolResult Run(string script, int timeoutSeconds = 120, int maxOutput = 48_000)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 600);
        var encoded = EncodeUtf16Base64(WrapScript(script));

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            process.Start();

            // Read stdout/stderr on thread-pool threads to avoid pipe buffer deadlocks.
            var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd());
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd());

            if (!process.WaitForExit(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return ToolResult.Fail($"PowerShell timed out after {timeoutSeconds} seconds.");
            }

            Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(10));

            var stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var stderr = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;

            return BuildResult(process.ExitCode, stdout, stderr, maxOutput, maxOutput / 2);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"PowerShell error: {ex.Message}");
        }
    }

    public static string ExtractStdout(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        const string stdoutMarker = "--- stdout ---";
        var markerIndex = output.IndexOf(stdoutMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return output.Trim();
        }

        var text = output[(markerIndex + stdoutMarker.Length)..];
        var stderrIndex = text.IndexOf("--- stderr ---", StringComparison.Ordinal);
        if (stderrIndex >= 0)
        {
            text = text[..stderrIndex];
        }

        return text.Trim();
    }

    internal static string WrapScript(string script) =>
        ScriptPreamble + Environment.NewLine + script;

    internal static string EncodeUtf16Base64(string command)
    {
        var byteCount = Encoding.Unicode.GetByteCount(command);
        var rented = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            var written = Encoding.Unicode.GetBytes(command.AsSpan(), rented.AsSpan());
            return Convert.ToBase64String(rented, 0, written);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Exit code is authoritative. Stderr that is only PowerShell progress CLIXML is ignored
    /// (and can turn a spurious non-zero exit into success when there is no real error record).
    /// </summary>
    internal static ToolResult BuildResult(
        int exitCode,
        string stdout,
        string stderr,
        int maxStdout,
        int maxStderr)
    {
        // Exit code is authoritative. Progress CLIXML alone is not a failure and may
        // accompany a spurious non-zero exit (Get-ComputerRestorePoint, Repair-Volume, etc.).
        // Empty stderr does NOT override a non-zero exit.
        var progressOnlyStderr = IsCliXmlProgressOnly(stderr);
        var success = exitCode == 0 || progressOnlyStderr;
        var reportStderr = progressOnlyStderr ? string.Empty : stderr;

        var result = new StringBuilder();
        result.AppendLine($"Exit code: {exitCode}");

        if (stdout.Length > 0)
        {
            result.AppendLine("--- stdout ---");
            result.Append(Truncate(stdout, maxStdout));
        }

        if (reportStderr.Length > 0)
        {
            result.AppendLine("--- stderr ---");
            result.Append(Truncate(reportStderr, maxStderr));
        }

        var text = result.ToString().TrimEnd();
        return success ? ToolResult.Ok(text) : ToolResult.Fail(text);
    }

    /// <summary>
    /// True when stderr is empty or contains only PowerShell CLIXML progress/informational records
    /// (no Error records). Common for Get-ComputerRestorePoint, Repair-Volume, winget, etc.
    /// </summary>
    internal static bool IsCliXmlProgressOnly(string? stderr)
    {
        // Empty is not "progress-only" — do not override a real non-zero exit.
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return false;
        }

        var trimmed = stderr.Trim();
        if (!trimmed.Contains("CLIXML", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Any non-whitespace text outside CLIXML documents → not progress-only.
        var withoutCliXml = CliXmlDocument().Replace(trimmed, string.Empty);

        if (!string.IsNullOrWhiteSpace(withoutCliXml))
        {
            return false;
        }

        return !HasRealErrorIndicators(trimmed);
    }

    private static bool HasRealErrorIndicators(string text) =>
        text.Contains("S=\"Error\"", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("S='Error'", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("FullyQualifiedErrorId", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n... [truncated]";

    [GeneratedRegex(@"#<\s*CLIXML[\s\S]*?(?=(#<\s*CLIXML)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CliXmlDocument();
}
