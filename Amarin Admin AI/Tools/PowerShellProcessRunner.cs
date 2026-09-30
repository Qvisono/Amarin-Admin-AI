using System.Diagnostics;
using System.Text;

namespace Amarin.Tools;

internal static class PowerShellProcessRunner
{
    /// <summary>Потолок обычного вызова PowerShell.</summary>
    internal const int DefaultMaxSeconds = 600;

    /// <summary>
    /// Потолок долгих системных операций: DISM, SFC, chkdsk.
    /// </summary>
    /// <remarks>
    /// Общий потолок в десять минут молча урезал их: <c>system_repair</c> просил для DISM
    /// 900 секунд, а получал 600, и восстановление образа обрывалось на середине.
    /// </remarks>
    internal const int LongOperationMaxSeconds = 7200;

    /// <summary>Сколько на самом деле ждать при запрошенном <paramref name="requested"/>.</summary>
    internal static int EffectiveTimeout(int requested, bool longOperation) =>
        Math.Clamp(requested, 5, longOperation ? LongOperationMaxSeconds : DefaultMaxSeconds);

    public static async Task<ToolResult> RunAsync(
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        int maxStdout = 32_000,
        int maxStderr = 16_000,
        bool longOperation = false)
    {
        timeoutSeconds = EffectiveTimeout(timeoutSeconds, longOperation);
        // Caller may already wrap; PowerShellHelper.RunAsync wraps before calling here.
        var encoded = PowerShellHelper.EncodeUtf16Base64(command);

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

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await AwaitOutputAsync(stdoutTask).ConfigureAwait(false);
            var stderr = await AwaitOutputAsync(stderrTask).ConfigureAwait(false);

            return PowerShellHelper.BuildResult(process.ExitCode, stdout, stderr, maxStdout, maxStderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillProcess(process);
            return ToolResult.Fail($"PowerShell command timed out after {timeoutSeconds} seconds.");
        }
        catch (OperationCanceledException)
        {
            // Стоп хода. Дерево процессов гасится здесь же, а отмена уходит наверх исключением:
            // прежний Fail читался агентом как обычная ошибка инструмента, и остановленный ход
            // шёл на следующий раунд с моделью.
            KillProcess(process);
            throw;
        }
        catch (Exception ex)
        {
            KillProcess(process);
            cancellationToken.ThrowIfCancellationRequested();
            return ToolResult.Fail($"Failed to execute PowerShell: {ex.Message}");
        }
    }

    private static async Task<string> AwaitOutputAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
            return string.Empty;
        }
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
            // Процесс мог завершиться сам между HasExited и Kill.
        }
    }
}
