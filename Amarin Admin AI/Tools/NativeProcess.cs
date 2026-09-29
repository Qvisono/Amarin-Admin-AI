using System.Diagnostics;

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
    public static ToolResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
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

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return ToolResult.Fail($"Не удалось запустить {fileName}.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var registration = cancellationToken.Register(() => Kill(process));
            if (!process.WaitForExit(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))))
            {
                Kill(process);
                return ToolResult.Fail($"{fileName} timed out after {timeoutSeconds} seconds.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return process.ExitCode == 0
                ? ToolResult.Ok(output.Trim())
                : ToolResult.Fail(string.IsNullOrWhiteSpace(output)
                    ? $"{fileName} exit {process.ExitCode}"
                    : output.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"{fileName}: {ex.Message}");
        }
    }

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
