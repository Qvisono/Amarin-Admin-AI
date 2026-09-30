using System.Security.Cryptography;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Итог стирания: что удалить не удалось (путь относительно папки профиля).</summary>
public sealed record WipeResult(IReadOnlyList<string> Failed)
{
    public static readonly WipeResult Nothing = new([]);

    public bool Ok => Failed.Count == 0;
}

/// <summary>
/// «Удалить все данные» одного профиля: чаты, настройки, ключи, инструкции, заготовки и
/// оформление.
/// </summary>
/// <remarks>
/// <para>
/// Профиль по умолчанию живёт в самом корне данных программы, а там же лежат вещи всех
/// профилей: <c>profiles.json</c>, папки других профилей, переводы интерфейса. Поэтому у него
/// стирается только перечисленное здесь — всё остальное в корне принадлежит не ему. Папка
/// дополнительного профиля целиком его, и стирается всё её содержимое.
/// </para>
/// <para>
/// Журнал аудита уходит только по отдельной галочке: он и нужен затем, чтобы пережить удаление
/// переписок, и молча терять его вместе с ними значило бы терять ответ на вопрос «что программа
/// делала на этой машине».
/// </para>
/// </remarks>
public static class ProfileDataWiper
{
    /// <summary>Файлы профиля по умолчанию в корне данных.</summary>
    internal static readonly string[] DefaultProfileFiles =
        ["settings.json", "keys.json", "prompts.json", "avatar.png", "balance.json"];

    /// <summary>
    /// Папки профиля по умолчанию. <c>shared</c> — сохранённые копии чатов из «Поделиться»:
    /// оставить их значило бы оставить переписку, которую человек велел удалить.
    /// </summary>
    internal static readonly string[] DefaultProfileFolders =
        ["chats", "usage", InstructionLibrary.FolderName, "shared"];

    internal const string AuditFolder = "audit";

    /// <summary>Стирает данные профиля. Не бросает: недоступный файл попадает в итог.</summary>
    public static WipeResult Wipe(string dataRoot, bool isDefaultProfile, bool includeAudit)
    {
        var failed = new List<string>();
        if (!Directory.Exists(dataRoot))
        {
            return WipeResult.Nothing;
        }

        if (isDefaultProfile)
        {
            foreach (var name in DefaultProfileFiles)
            {
                DeleteFile(dataRoot, name, failed);
            }

            foreach (var file in SafeFiles(dataRoot))
            {
                var name = Path.GetFileName(file);

                // Картинка фона с готовыми снимками (background.<отпечаток>.cache.png) и
                // недописанные «.tmp» от записи тех же файлов.
                if (name.StartsWith("background.", StringComparison.OrdinalIgnoreCase) ||
                    IsLeftoverOf(name))
                {
                    DeleteFile(dataRoot, name, failed);
                }
            }

            foreach (var folder in DefaultProfileFolders)
            {
                DeleteFolder(dataRoot, folder, failed);
            }
        }
        else
        {
            foreach (var entry in SafeEntries(dataRoot))
            {
                var name = Path.GetFileName(entry);
                if (string.Equals(name, AuditFolder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    DeleteFolder(dataRoot, name, failed);
                }
                else
                {
                    DeleteFile(dataRoot, name, failed);
                }
            }
        }

        if (includeAudit)
        {
            DeleteFolder(dataRoot, AuditFolder, failed);
        }

        return new WipeResult(failed);
    }

    /// <summary>
    /// Набранное совпадает со словом-подтверждением: без учёта регистра и пробелов по краям,
    /// но целиком — начало слова не годится.
    /// </summary>
    public static bool WordMatches(string? typed, string word) =>
        !string.IsNullOrWhiteSpace(typed) &&
        string.Equals(typed.Trim(), word, StringComparison.CurrentCultureIgnoreCase);

    private static bool IsLeftoverOf(string name) =>
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
        DefaultProfileFiles.Any(file => name.StartsWith(file + ".", StringComparison.OrdinalIgnoreCase));

    private static void DeleteFile(string root, string name, List<string> failed)
    {
        var path = Path.Combine(root, name);
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(name);
        }
    }

    /// <remarks>
    /// <see cref="Directory.Delete(string, bool)"/> в точку соединения не заходит, а удаляет саму
    /// ссылку: подложенный внутрь папки профиля junction не уведёт стирание за её пределы.
    /// </remarks>
    private static void DeleteFolder(string root, string name, List<string> failed)
    {
        var path = Path.Combine(root, name);
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failed.Add(name);
        }
    }

    private static IEnumerable<string> SafeFiles(string root)
    {
        try
        {
            return Directory.GetFiles(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEntries(string root)
    {
        try
        {
            return Directory.GetFileSystemEntries(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>Просьба стереть профиль, оставленная работающей программой для своего преемника.</summary>
/// <param name="Token">Одноразовая метка; её же преемник получает в командной строке.</param>
/// <param name="ProfileId">Чей профиль стирать.</param>
/// <param name="IncludeAudit">Стереть и журнал аудита.</param>
/// <param name="RequestedAt">Когда попросили: старая просьба не исполняется.</param>
public sealed record WipeRequest(string Token, string ProfileId, bool IncludeAudit, DateTime RequestedAt);

/// <summary>
/// Передача просьбы о стирании следующему запуску программы.
/// </summary>
/// <remarks>
/// <para>
/// Стирает не та программа, в которой нажали кнопку, а её преемник — после того как прежний
/// процесс вышел. Пока окно живо, файлы пишутся отовсюду: закрытие сохраняет размер окна в
/// <c>settings.json</c> и дописывает чаты и журнал трат, запоздавшие сводка и заголовок
/// сохраняют свой чат, остаток ключа ложится в <c>balance.json</c>. Стёртое на ходу вернулось
/// бы на диск через секунду. Преемник же стирает до того, как сам прочтёт хоть один файл.
/// </para>
/// <para>
/// Одного ключа командной строки для этого мало: ярлык с ним стёр бы данные без всякого
/// вопроса. Поэтому просьба лежит файлом в папке данных, куда пишет только сама программа
/// (запись туда любым инструментом модели отклоняется), а ключ несёт лишь метку к ней. Нет
/// файла, не совпала метка или просьба старше <see cref="MaxAge"/> — ничего не стирается.
/// </para>
/// </remarks>
public static class PendingWipe
{
    internal const string FileName = "wipe-request.json";

    /// <summary>Сколько просьба ждёт преемника: перезапуск занимает секунды.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>Оставляет просьбу и отдаёт метку для командной строки преемника.</summary>
    public static string Request(string appRoot, string profileId, bool includeAudit)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Directory.CreateDirectory(appRoot);
        AppDataFile.WriteAtomic(
            Path.Combine(appRoot, FileName),
            JsonSerializer.Serialize(new WipeRequest(token, profileId, includeAudit, DateTime.UtcNow), AppJson.Options));
        return token;
    }

    /// <summary>
    /// Забирает просьбу, если метка совпала и срок не вышел. Файл удаляется в любом случае:
    /// неисполненная просьба не должна сработать при каком-нибудь следующем запуске.
    /// </summary>
    public static WipeRequest? TryTake(string appRoot, string? token, DateTime nowUtc)
    {
        var path = Path.Combine(appRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        WipeRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<WipeRequest>(File.ReadAllText(path), AppJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            request = null;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не удалось убрать — тогда и не исполняем: иначе просьба сработала бы дважды.
            return null;
        }

        if (request is null ||
            string.IsNullOrEmpty(token) ||
            string.IsNullOrEmpty(request.Token) ||
            !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(request.Token),
                System.Text.Encoding.ASCII.GetBytes(token)) ||
            nowUtc - request.RequestedAt > MaxAge ||
            request.RequestedAt - nowUtc > TimeSpan.FromMinutes(1))
        {
            return null;
        }

        return request;
    }

    /// <summary>
    /// Исполняет просьбу прежнего запуска, если она есть и адресована этому. Зовётся до того,
    /// как программа прочтёт хоть один файл профиля.
    /// </summary>
    /// <returns>Итог стирания; null — стирать было нечего.</returns>
    public static WipeResult? Run(string appRoot, string? token, DateTime nowUtc)
    {
        if (TryTake(appRoot, token, nowUtc) is not { } request)
        {
            return null;
        }

        var store = new ProfileStore(appRoot);
        var registry = store.Load();
        var profile = registry.Profiles.FirstOrDefault(
            item => string.Equals(item.Id, request.ProfileId, StringComparison.Ordinal));
        if (profile is null)
        {
            return null;
        }

        var result = ProfileDataWiper.Wipe(
            store.DataRootFor(profile.Id), ProfileStore.IsDefault(profile.Id), request.IncludeAudit);

        // Аватар стёрт вместе с папкой, а ссылка на него живёт в profiles.json: без этого
        // экран входа искал бы несуществующий файл.
        if (profile.AvatarFileName is not null)
        {
            profile.AvatarFileName = null;
            try
            {
                store.Save(registry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Пропавший аватар экран входа переживает и так — просто без картинки.
            }
        }

        return result;
    }
}
