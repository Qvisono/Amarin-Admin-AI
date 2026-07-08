using System.Diagnostics;
using System.Text;

namespace Amarin.Tools;

internal static class PowerShellHelper
{
    public static Task<ToolResult> RunAsync(
        string script,
        int timeoutSeconds = 120,
        CancellationToken cancellationToken = default,
        int maxOutput = 48_000) =>
        PowerShellProcessRunner.RunAsync(script, timeoutSeconds, cancellationToken, maxOutput, maxOutput / 2);

    public static ToolResult Run(string script, int timeoutSeconds = 120, int maxOutput = 48_000)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 600);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

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

            var result = new StringBuilder();
            result.AppendLine($"Exit code: {process.ExitCode}");

            if (stdout.Length > 0)
            {
                result.AppendLine("--- stdout ---");
                result.Append(Truncate(stdout, maxOutput));
            }

            if (stderr.Length > 0)
            {
                result.AppendLine("--- stderr ---");
                result.Append(Truncate(stderr, maxOutput / 2));
            }

            var text = result.ToString().TrimEnd();
            return process.ExitCode == 0 ? ToolResult.Ok(text) : ToolResult.Fail(text);
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

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n... [truncated]";
}