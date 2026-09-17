namespace Amarin.UI;

/// <summary>
/// Разбор аргументов командной строки: <c>--model</c>, <c>--prompt</c>, <c>--prompt-file</c>,
/// <c>--smoke-tools</c>, <c>--await-exit</c> и <c>--apply-update</c>.
/// </summary>
/// <remarks>
/// <c>--prompt-file</c> удаляет файл сразу после чтения: через него ярлык передаёт длинный
/// запрос, который не помещается в командную строку, и оставлять его на диске незачем.
/// </remarks>
internal sealed class StartupArgs
{
    public string? Model { get; private set; }
    public string? Prompt { get; private set; }
    public bool SmokeTools { get; private set; }

    /// <summary>
    /// Дождаться выхода этого процесса перед всем остальным; <c>null</c> — ждать некого.
    /// </summary>
    /// <remarks>
    /// Так возвращается программа после обновления. Старый процесс запускает новый и только
    /// потом закрывается, а замок единственного экземпляра держится до конца процесса: без
    /// ожидания новый видит живого владельца, уходит по ветке передачи запроса и выходит —
    /// человек остаётся вообще без окна.
    /// </remarks>
    public int? AwaitExitPid { get; private set; }

    /// <summary>
    /// Поставить этот файл на место программы и сразу выйти; <c>null</c> — обычный запуск.
    /// </summary>
    /// <remarks>
    /// Тот случай, когда папка программы пишется только администратором: обычный процесс
    /// скачивает и сверяет файл сам, а через UAC поднимается только подмена — и ничего больше.
    /// Целью служит собственный <see cref="Environment.ProcessPath"/>: повышенный процесс
    /// запускается из того самого exe, который и надо заменить.
    /// </remarks>
    public string? ApplyUpdateFrom { get; private set; }

    public static StartupArgs Parse(string[] args)
    {
        var result = new StartupArgs();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--smoke-tools", StringComparison.OrdinalIgnoreCase))
            {
                result.SmokeTools = true;
                continue;
            }

            if (TryTakeValue(args, ref i, "--model", "-m", out var model))
            {
                result.Model = model;
                continue;
            }

            if (TryTakeValue(args, ref i, "--prompt", "-p", out var prompt))
            {
                result.Prompt = prompt;
                continue;
            }

            if (TryTakeValue(args, ref i, "--await-exit", out var pid))
            {
                result.AwaitExitPid = int.TryParse(pid, out var parsed) && parsed > 0 ? parsed : null;
                continue;
            }

            if (TryTakeValue(args, ref i, "--apply-update", out var incoming))
            {
                result.ApplyUpdateFrom = string.IsNullOrWhiteSpace(incoming) ? null : incoming;
                continue;
            }

            if (TryTakeValue(args, ref i, "--prompt-file", out var promptFile))
            {
                result.Prompt = ReadPromptFile(promptFile);
            }
        }

        return result;
    }

    private static bool TryTakeValue(
        string[] args, ref int i, string longName, out string value) =>
        TryTakeValue(args, ref i, longName, shortName: null, out value);

    private static bool TryTakeValue(
        string[] args, ref int i, string longName, string? shortName, out string value)
    {
        value = string.Empty;
        var a = args[i];

        if (a.Equals(longName, StringComparison.OrdinalIgnoreCase) ||
            (shortName is not null && a.Equals(shortName, StringComparison.OrdinalIgnoreCase)))
        {
            if (i + 1 >= args.Length)
                return false;
            value = args[++i];
            return true;
        }

        var prefix = longName + "=";
        if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = a[prefix.Length..];
            return true;
        }

        if (shortName is not null)
        {
            var shortPrefix = shortName + "=";
            if (a.StartsWith(shortPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = a[shortPrefix.Length..];
                return true;
            }
        }

        return false;
    }

    private static string? ReadPromptFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var text = File.ReadAllText(path);
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Temp file cleanup is best-effort.
            }

            return text;
        }
        catch
        {
            return null;
        }
    }
}
