using System.Diagnostics;
using System.Text;

namespace Amarin.Tools;

internal static class PowerShellProcessRunner
{
    public static async Task<ToolResult> RunAsync(
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        int maxStdout = 32_000,
        int maxStderr = 16_000)
    {
        timeoutSeconds = Math.Clamp(timeoutSeconds, 5, 600);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            process.Start();

            var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEnd(), CancellationToken.None);
            var stderrTask = Task.Run(() => process.StandardError.ReadToEnd(), CancellationToken.None);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await AwaitOutputAsync(stdoutTask);
            var stderr = await AwaitOutputAsync(stderrTask);

            return BuildResult(process.ExitCode, stdout, stderr, maxStdout, maxStderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            return ToolResult.Fail($"PowerShell command timed out after {timeoutSeconds} seconds.");
        }
        catch (Exception ex)
        {
            KillProcess(process);
            return ToolResult.Fail($"Failed to execute PowerShell: {ex.Message}");
        }
    }

    private static async Task<string> AwaitOutputAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ToolResult BuildResult(
        int exitCode,
        string stdout,
        string stderr,
        int maxStdout,
        int maxStderr)
    {
        var result = new StringBuilder();
        result.AppendLine($"Exit code: {exitCode}");

        if (stdout.Length > 0)
        {
            result.AppendLine("--- stdout ---");
            result.Append(Truncate(stdout, maxStdout));
        }

        if (stderr.Length > 0)
        {
            result.AppendLine("--- stderr ---");
            result.Append(Truncate(stderr, maxStderr));
        }

        var text = result.ToString().TrimEnd();
        return exitCode == 0 ? ToolResult.Ok(text) : ToolResult.Fail(text);
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "\n... [truncated]";
}