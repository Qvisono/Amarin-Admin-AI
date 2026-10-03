using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Разбор аргументов командной строки: <c>--model</c>, <c>--prompt</c>, <c>--prompt-file</c>,
/// <c>--send</c>, <c>--smoke-tools</c>, <c>--smoke-report</c>, <c>--await-exit</c>, <c>--apply-update</c>,
/// <c>--rollback-update</c>, <c>--wipe</c>,
/// <c>--open-chat</c> и действия интеграции с Windows (G): <c>--new-chat</c>, <c>--health</c>,
/// <c>--tray</c>, <c>--ask-path</c>.
/// </summary>
/// <remarks>
/// <c>--prompt-file</c> удаляет файл сразу после чтения: через него ярлык передаёт длинный
/// запрос, который не помещается в командную строку, и оставлять его на диске незачем.
/// </remarks>
internal sealed class StartupArgs
{
    public string? Model { get; private set; }
    public string? Prompt { get; private set; }

    /// <summary>
    /// Отправить <see cref="Prompt"/> сразу, а не только положить его в поле ввода.
    /// </summary>
    /// <remarks>
    /// До 1.28.0 запрос из командной строки при первом запуске уходил сам, а переданный уже
    /// открытой программе — только ложился в поле. Одна и та же команда вела себя по-разному
    /// в зависимости от того, открыто ли окно, а отправка без взгляда человека запускала агента
    /// с инструментами по строке из ярлыка. Теперь по умолчанию — только поле, в обоих случаях;
    /// сразу отправить — отдельный явный флаг.
    /// </remarks>
    public bool Send { get; private set; }

    /// <summary>Запрос есть и его велено отправить.</summary>
    public bool ShouldSend => Send && !string.IsNullOrWhiteSpace(Prompt);

    public bool SmokeTools { get; private set; }

    /// <summary>
    /// Куда записать итог прогона инструментов (<c>--smoke-report &lt;файл&gt;</c>, H6). Включает
    /// <see cref="SmokeTools"/> сам. Нужен CI: программа собрана как оконная, и <c>AllocConsole</c>
    /// открывает новое окно консоли, которое раннер не читает, — вывод обязан уйти в файл.
    /// </summary>
    public string? SmokeReportPath { get; private set; }

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

    /// <summary>
    /// Сумма SHA-256 файла для <see cref="ApplyUpdateFrom"/>: повышенный процесс файла не
    /// скачивал и сверяет его сам, прямо перед подменой. Null — обновление без проверки, на
    /// которое человек отдельно согласился.
    /// </summary>
    public string? ApplyUpdateSha256 { get; private set; }

    /// <summary>
    /// Вернуть сбережённую прошлую версию от имени администратора (H4). Значения нет намеренно:
    /// что ставить, повышенный процесс вычисляет из своего же пути, а не берёт из командной строки.
    /// </summary>
    public bool RollbackUpdate { get; private set; }

    /// <summary>
    /// Метка просьбы стереть профиль, оставленной прежним запуском (<see cref="PendingWipe"/>).
    /// Сама по себе ничего не стирает: без файла просьбы с той же меткой это пустой звук.
    /// </summary>
    public string? WipeToken { get; private set; }

    /// <summary>
    /// Чат, который открыть при запуске, — так перезапуск от администратора возвращает человека
    /// туда, где он был. Только идентификатор вида, который пишет <see cref="ChatStore"/>: он
    /// становится именем файла, и путь из командной строки сюда не пройдёт.
    /// </summary>
    public string? OpenChatId { get; private set; }

    /// <summary>Что сделать при запуске или в уже открытом окне (список переходов, Проводник, автозапуск).</summary>
    public StartupAction Action { get; private set; }

    /// <summary>
    /// Путь из пункта «Спросить Amarin» в Проводнике. Только полный путь разумной длины: он
    /// ложится в поле ввода текстом, а не открывается программой.
    /// </summary>
    public string? AskPath { get; private set; }

    internal static bool IsAskPath(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 1024 &&
        value.IndexOfAny(['\r', '\n', '\0']) < 0 && Path.IsPathFullyQualified(value);

    internal static bool IsChatId(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

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

            if (TryTakeValue(args, ref i, "--smoke-report", out var report))
            {
                if (!string.IsNullOrWhiteSpace(report))
                {
                    result.SmokeReportPath = report;
                    result.SmokeTools = true;
                }

                continue;
            }

            if (a.Equals("--rollback-update", StringComparison.OrdinalIgnoreCase))
            {
                result.RollbackUpdate = true;
                continue;
            }

            if (a.Equals("--send", StringComparison.OrdinalIgnoreCase))
            {
                result.Send = true;
                continue;
            }

            if (a.Equals("--new-chat", StringComparison.OrdinalIgnoreCase))
            {
                result.Action = StartupAction.NewChat;
                continue;
            }

            if (a.Equals("--health", StringComparison.OrdinalIgnoreCase))
            {
                result.Action = StartupAction.Health;
                continue;
            }

            if (a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
            {
                result.Action = StartupAction.Tray;
                continue;
            }

            if (TryTakeValue(args, ref i, "--ask-path", out var askPath))
            {
                if (IsAskPath(askPath))
                {
                    result.Action = StartupAction.AskPath;
                    result.AskPath = askPath;
                }

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

            if (TryTakeValue(args, ref i, "--sha256", out var sha))
            {
                result.ApplyUpdateSha256 = sha.Length == 64 && sha.All(Uri.IsHexDigit) ? sha : null;
                continue;
            }

            if (TryTakeValue(args, ref i, "--wipe", out var wipe))
            {
                result.WipeToken = string.IsNullOrWhiteSpace(wipe) ? null : wipe;
                continue;
            }

            if (TryTakeValue(args, ref i, "--open-chat", out var chat))
            {
                result.OpenChatId = IsChatId(chat) ? chat : null;
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
            {
                return false;
            }

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
        {
            return null;
        }

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

/// <summary>Действие запуска (G6): из списка переходов, Проводника или автозапуска.</summary>
internal enum StartupAction
{
    None,

    /// <summary>Открыть новый чат.</summary>
    NewChat,

    /// <summary>Открыть «Состояние ПК».</summary>
    Health,

    /// <summary>Запуститься без окна, только значком в трее (автозапуск).</summary>
    Tray,

    /// <summary>Новый чат с путём из Проводника в поле ввода.</summary>
    AskPath
}
