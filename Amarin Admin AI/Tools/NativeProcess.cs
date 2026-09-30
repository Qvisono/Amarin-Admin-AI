using System.Diagnostics;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Запуск внешней программы Windows (schtasks, reg, sc) с аргументами списком и с таймаутом.
/// </summary>
/// <remarks>
/// <para>
/// Аргументы — только через <see cref="ProcessStartInfo.ArgumentList"/>, а не строкой. Строку
/// собирали подстановкой того, что прислала модель, и кавычка или пробел в значении добавляли
/// программе свои ключи: <c>schedule = "daily /ru SYSTEM"</c> превращал задачу планировщика в
/// задачу от имени системы. Список аргументов экранирует каждое значение целиком.
/// </para>
/// <para>
/// Таймаут обязателен: <c>WaitForExit()</c> без предела держал вызов инструмента вечно, если
/// программа ждала ввода или зависла.
/// </para>
/// </remarks>
internal static class NativeProcess
{
    /// <summary>Синхронный запуск — для синхронных путей (снимки отката); та же реализация.</summary>
    public static ToolResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken = default) =>
        RunAsync(fileName, arguments, timeoutSeconds, cancellationToken).GetAwaiter().GetResult();

    /// <summary>
    /// Запуск с ожиданием без занятого потока. Отмена хода гасит всё дерево процесса и уходит
    /// наверх <see cref="OperationCanceledException"/> — как у <see cref="PowerShellProcessRunner"/>.
    /// </summary>
    public static async Task<ToolResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken = default,
        int maxOutput = 16_000)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"{fileName}: {ex.Message}");
        }

        if (process is null)
        {
            return ToolResult.Fail(Loc.Format("S.Tool.Native.StartFailed", fileName));
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                cancellationToken.ThrowIfCancellationRequested();
                return ToolResult.Fail($"{fileName} timed out after {timeoutSeconds} seconds.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var output = Truncate((string.IsNullOrWhiteSpace(stdout) ? stderr : stdout).Trim(), maxOutput);
            return process.ExitCode == 0
                ? ToolResult.Ok(output)
                : ToolResult.Fail(string.IsNullOrWhiteSpace(output) ? $"{fileName} exit {process.ExitCode}" : output);
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n… [truncated]";

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Процесс мог завершиться сам между проверкой и Kill.
        }
    }
}
