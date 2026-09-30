using System.Text.Json;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Что второй экземпляр передаёт первому.</summary>
internal sealed class HandoffRequest
{
    public string? Prompt { get; init; }

    /// <summary>Второй запуск был с <c>--send</c>: запрос отправляется, а не только ложится в поле.</summary>
    /// <remarks>Файлы прежних версий этого поля не несут и читаются как «только в поле» — как и было.</remarks>
    public bool Send { get; init; }

    /// <summary>Действие из списка переходов или Проводника (G6). Нет поля — только текст, как раньше.</summary>
    public StartupAction Action { get; init; }

    /// <summary>Чат, который открыть (<c>--open-chat</c>).</summary>
    public string? ChatId { get; init; }

    /// <summary>Путь из «Спросить Amarin» в Проводнике.</summary>
    public string? AskPath { get; init; }

    /// <summary>Есть ли что делать: текст, действие или чат.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Prompt) && Action == StartupAction.None && string.IsNullOrWhiteSpace(ChatId);

    /// <summary>
    /// Последний запрос с действием или чатом — его и выполняет окно. Текст берётся отдельно
    /// (<see cref="Latest"/>): несколько запусков подряд с разными ключами не должны терять ни
    /// последнюю вкладку, ни последний набранный текст.
    /// </summary>
    public static HandoffRequest? LatestAction(IEnumerable<HandoffRequest> requests) =>
        requests.LastOrDefault(item => item.Action != StartupAction.None || !string.IsNullOrWhiteSpace(item.ChatId));

    /// <summary>
    /// Что делать с накопившимися запросами: берётся последний непустой, вместе с его флагом.
    /// </summary>
    /// <remarks>
    /// Флаг от того же запроса, что и текст: иначе <c>--send</c> одного запуска отправил бы текст,
    /// который другой запуск велел лишь положить в поле.
    /// </remarks>
    public static HandoffRequest? Latest(IEnumerable<HandoffRequest> requests) =>
        requests.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.Prompt));
}

/// <summary>
/// Передача запроса от второго экземпляра первому — через файл, а не через оконное сообщение.
/// </summary>
/// <remarks>
/// <c>WM_COPYDATA</c> потребовал бы HWND адресата (то есть всё равно предварительной рассылки)
/// и <c>ChangeWindowMessageFilterEx</c>, когда экземпляры запущены с разными правами; именованный
/// канал — потока-слушателя и своего ACL. Файл в папке данных проще и уже применяется:
/// <c>--prompt-file</c> в <see cref="StartupArgs"/> работает ровно так же — прочитать и удалить.
/// </remarks>
internal static class SingleInstanceHandoff
{
    private const string FolderName = "handoff";

    /// <summary>Разводит запросы этого процесса, записанные в один и тот же тик часов.</summary>
    private static long _sequence;

    public static string DirectoryFor(string root) => Path.Combine(root, FolderName);

    /// <summary>Кладёт запрос. Ничего не бросает: несостоявшаяся передача не повод падать.</summary>
    public static void Write(
        string root,
        string? prompt,
        bool send = false,
        StartupAction action = StartupAction.None,
        string? chatId = null,
        string? askPath = null)
    {
        var request = new HandoffRequest { Prompt = prompt, Send = send, Action = action, ChatId = chatId, AskPath = askPath };
        if (request.IsEmpty)
        {
            return;
        }

        try
        {
            var directory = DirectoryFor(root);
            Directory.CreateDirectory(directory);
            // Имя начинается со времени записи: «последний запрос» — последний по имени. Пока имя
            // было голым GUID, порядок файлов был случайным, и из двух запусков подряд побеждал
            // любой — окно открывало то один чат, то другой.
            var name = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow.Ticks:D19}-{Interlocked.Increment(ref _sequence):D10}-{Guid.NewGuid():N}.json");
            var path = Path.Combine(directory, name);
            AppDataFile.WriteAtomic(path, JsonSerializer.Serialize(request, AppJson.Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Первый экземпляр всё равно поднимется — просто без текста.
        }
    }

    /// <summary>Забирает всё накопившееся и стирает файлы. Битые молча пропускает.</summary>
    public static List<HandoffRequest> TryTakeAll(string root)
    {
        var found = new List<HandoffRequest>();
        var directory = DirectoryFor(root);
        if (!Directory.Exists(directory))
        {
            return found;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            try
            {
                var request = JsonSerializer.Deserialize<HandoffRequest>(
                    File.ReadAllText(file), AppJson.Options);
                if (request is not null && !request.IsEmpty)
                {
                    found.Add(request);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Битый файл — не авария: удалим его вместе с остальными.
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Останется до следующего запуска.
            }
        }

        return found;
    }
}
