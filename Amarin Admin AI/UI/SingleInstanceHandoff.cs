using System.Text.Json;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Что второй экземпляр передаёт первому.</summary>
internal sealed class HandoffRequest
{
    public string? Prompt { get; init; }
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

    public static string DirectoryFor(string root) => Path.Combine(root, FolderName);

    /// <summary>Кладёт запрос. Ничего не бросает: несостоявшаяся передача не повод падать.</summary>
    public static void Write(string root, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        try
        {
            var directory = DirectoryFor(root);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            AppDataFile.WriteAtomic(path, JsonSerializer.Serialize(
                new HandoffRequest { Prompt = prompt }, AppJson.Options));
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
                if (request is not null && !string.IsNullOrWhiteSpace(request.Prompt))
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
