using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Сколько всего в одной категории — то, что видно в строке с галочкой.</summary>
/// <param name="Items">Человеческое число: чатов, профилей, языков. Для прочего — число файлов.</param>
public sealed record DataBundleCategoryInfo(DataCategory Category, int Items, long Bytes);

/// <summary>Что уедет в архив.</summary>
/// <param name="ByCategory">Только непустые категории: «0 файлов» в списке — шум.</param>
public sealed record DataBundlePlan(
    IReadOnlyList<DataBundleCategoryInfo> ByCategory,
    int Files,
    long Bytes)
{
    public static DataBundlePlan Empty { get; } = new([], 0, 0);
}

/// <summary>
/// Сборка архива с данными пользователя.
/// </summary>
/// <remarks>
/// Ходит по диску, поэтому зовётся не с потока интерфейса. Раскладку архива и правило двух осей
/// описывает <see cref="DataBundle"/>.
/// </remarks>
public sealed class DataBundleExporter
{
    private readonly string _root;
    private readonly string _activeProfileId;

    /// <param name="rootDirectory">
    /// Корень данных. <c>null</c> — <see cref="AppPaths.Root"/>; тесты передают временную папку,
    /// чтобы не трогать файлы живого пользователя.
    /// </param>
    /// <param name="activeProfileId">Чей профиль ляжет в корень архива.</param>
    public DataBundleExporter(string? rootDirectory = null, string? activeProfileId = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory) ? AppPaths.Root : rootDirectory;
        _activeProfileId = string.IsNullOrWhiteSpace(activeProfileId)
            ? ProfileStore.DefaultProfileId
            : activeProfileId;
    }

    /// <summary>Сколько и чего уедет, без записи на диск.</summary>
    public DataBundlePlan Plan(DataCategory categories, CancellationToken cancellationToken = default)
    {
        var sources = Collect(categories, cancellationToken);
        if (sources.Count == 0)
        {
            return DataBundlePlan.Empty;
        }

        var byCategory = sources
            .GroupBy(source => source.Category)
            .Select(group => new DataBundleCategoryInfo(
                group.Key,
                CountItems(group.Key, group),
                group.Sum(source => source.Bytes)))
            .OrderBy(info => Order(info.Category))
            .ToList();

        return new DataBundlePlan(byCategory, sources.Count, sources.Sum(source => source.Bytes));
    }

    /// <summary>
    /// Пишет архив и возвращает его опись.
    /// </summary>
    /// <remarks>
    /// Сначала во временный файл рядом, потом переименование: прерванный на середине экспорт не
    /// должен оставить полуфабрикат под правильным именем — человек решит, что копия у него есть.
    /// </remarks>
    /// <exception cref="IOException">Диск полон, файл занят, путь недоступен.</exception>
    public DataBundleManifest Write(
        string archivePath,
        DataCategory categories,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        var sources = Collect(categories, cancellationToken);
        var manifest = new DataBundleManifest
        {
            AppVersion = AppVersion(),
            CreatedAt = DateTime.Now,
            ActiveProfileId = _activeProfileId,
            Categories = DataBundle.All.Where(c => categories.HasFlag(c)).ToList()
        };

        var temporary = archivePath + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Optimal, а не SmallestSize: чаты — это в основном base64 вложений, где разница
                // между уровнями сжатия исчисляется процентами, а временем — минутами.
                using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);

                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var bytes = ReadSource(source);
                    if (bytes is null)
                    {
                        continue;
                    }

                    var entry = archive.CreateEntry(source.ArchivePath, CompressionLevel.Optimal);
                    using (var stream = entry.Open())
                    {
                        stream.Write(bytes);
                    }

                    manifest.Entries.Add(new DataBundleEntry
                    {
                        Path = source.ArchivePath,
                        Category = source.Category,
                        ProfileId = source.ProfileId,
                        Bytes = bytes.Length
                    });
                }

                var description = archive.CreateEntry(DataBundle.ManifestName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(description.Open(), new UTF8Encoding(false));
                writer.Write(JsonSerializer.Serialize(manifest, DataBundle.Json));
            }

            File.Move(temporary, archivePath, overwrite: true);
            return manifest;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>Один файл, отобранный для архива.</summary>
    /// <param name="SourcePath">Где лежит на диске; пусто для собранного на лету реестра.</param>
    /// <param name="ArchivePath">Куда ляжет внутри архива.</param>
    /// <param name="ProfileId">Пусто — активный профиль, он живёт в корне архива.</param>
    /// <param name="Payload">Готовое содержимое: так едет санированный profiles.json.</param>
    private sealed record Source(
        string SourcePath,
        string ArchivePath,
        DataCategory Category,
        string ProfileId,
        long Bytes,
        byte[]? Payload = null);

    private List<Source> Collect(DataCategory categories, CancellationToken cancellationToken)
    {
        var sources = new List<Source>();
        if (categories == DataCategory.None || !Directory.Exists(_root))
        {
            return sources;
        }

        var registry = new ProfileStore(_root).Load();
        var activeRoot = DataRootFor(_activeProfileId);

        // Активный профиль — в корень архива, чем бы он ни был. Так его чаты приезжают к
        // получателю как чаты того профиля, под которым тот работает.
        AddFolder(sources, activeRoot, DataBundle.DataPrefix, "", "", categories, cancellationToken);

        // Языки общие для всех профилей и лежат только в корне данных, поэтому у неосновного
        // активного профиля их надо забрать отдельным проходом — в его папке их нет.
        if (!ProfileStore.IsDefault(_activeProfileId))
        {
            AddFolder(
                sources,
                Path.Combine(_root, "languages"),
                DataBundle.DataPrefix + "languages/",
                "languages/",
                "",
                categories & DataCategory.Languages,
                cancellationToken);
        }

        if (!categories.HasFlag(DataCategory.Profiles))
        {
            return sources;
        }

        // Реестр не копируется байтами: из него вычищаются пароли (см. Sanitize).
        var sanitized = JsonSerializer.Serialize(Sanitize(registry), AppJson.Options);
        var payload = Encoding.UTF8.GetBytes(sanitized);
        sources.Add(new Source(
            "",
            DataBundle.DataPrefix + "profiles.json",
            DataCategory.Profiles,
            "",
            payload.Length,
            payload));

        // Все остальные профили — в свои папки, включая default, если активен не он: его данные
        // лежат в корне данных, но в корень архива уже уехал активный.
        foreach (var profile in registry.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (profile.Id == _activeProfileId)
            {
                continue;
            }

            AddFolder(
                sources,
                DataRootFor(profile.Id),
                $"{DataBundle.DataPrefix}{DataBundle.ProfilesFolder}/{profile.Id}/",
                "",
                profile.Id,

                // Языки общие на программу и уже уехали в корень архива: у default-профиля,
                // если активен не он, папка languages лежит прямо в его корне данных, и без
                // этой оговорки переводы попали бы в архив дважды.
                categories & ~DataCategory.Languages,
                cancellationToken);
        }

        return sources;
    }

    /// <summary>Складывает в список всё подходящее из одной папки, вместе с вложенными.</summary>
    /// <param name="classifyPrefix">
    /// Приставка, которую видит классификатор. Нужна, когда папку обходят саму по себе: по имени
    /// «ru.json» категорию не определить, а по «languages/ru.json» — можно.
    /// </param>
    private void AddFolder(
        List<Source> sources,
        string folder,
        string archivePrefix,
        string classifyPrefix,
        string profileId,
        DataCategory categories,
        CancellationToken cancellationToken)
    {
        if (categories == DataCategory.None || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in FileWalk.Files(folder, cancellationToken))
        {
            var relative = DataBundle.Relative(folder, file.FullName);

            // Папки профилей лежат под тем же корнем, что и default-профиль: без этого их файлы
            // уехали бы дважды — и в корень архива, и в свою папку.
            if (relative.StartsWith(DataBundle.ProfilesFolder + "/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var category = DataBundle.CategoryOf(classifyPrefix + relative);
            if (category == DataCategory.None || !categories.HasFlag(category))
            {
                continue;
            }

            // Реестр профилей едет отдельно и не байтами: в нём чистятся пароли. Забрать его
            // здесь заодно значило бы положить в архив вторую, нетронутую копию — с хэшами.
            if (string.Equals(relative, "profiles.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (category == DataCategory.Appearance && !IsAllowedImage(file.Name, file.Length))
            {
                continue;
            }

            sources.Add(new Source(
                file.FullName,
                archivePrefix + relative,
                category,
                profileId,
                file.Length));
        }
    }

    private string DataRootFor(string profileId) => new ProfileStore(_root).DataRootFor(profileId);

    /// <summary>
    /// Копия реестра без всего, что связано с паролями.
    /// </summary>
    /// <remarks>
    /// ZIP не шифруется, и человек положит его в облако или на флешку. PasswordHash — это
    /// PBKDF2-выжимка от пароля, который люди переиспользуют: отдавать её в переносимый файл
    /// значит создать мишень для оффлайн-перебора там, где её раньше не было. Пользы при этом
    /// почти нет — пароль на новой машине ставится за три секунды, а вот запертый чужим паролем
    /// экземпляр программы это потеря доступа к собственным данным.
    /// </remarks>
    private static ProfileRegistry Sanitize(ProfileRegistry registry) => new()
    {
        ActiveProfileId = registry.ActiveProfileId,
        Profiles = registry.Profiles
            .Select(profile => new UserProfile
            {
                Id = profile.Id,
                Name = profile.Name,
                AvatarFileName = profile.AvatarFileName,
                CreatedAt = profile.CreatedAt,

                // Замок без хэша бессмыслен, и файл не должен врать, что он есть.
                LockOnStartup = false,
                PasswordHash = null,
                PasswordSalt = null
            })
            .ToList()
    };

    /// <summary>Место категории в списке — чтобы сводка шла в том же порядке, что и галочки.</summary>
    private static int Order(DataCategory category)
    {
        for (var i = 0; i < DataBundle.All.Count; i++)
        {
            if (DataBundle.All[i] == category)
            {
                return i;
            }
        }

        return DataBundle.All.Count;
    }

    private static bool IsAllowedImage(string name, long bytes) =>
        bytes <= DataBundle.MaxImageBytes && DataBundle.IsSupportedImageName(name);

    private static int CountItems(DataCategory category, IEnumerable<Source> sources) => category switch
    {
        // Индекс — служебный файл, человек считает чатами переписки.
        DataCategory.Chats => sources.Count(source =>
            !source.ArchivePath.EndsWith("/index.json", StringComparison.Ordinal)),
        _ => sources.Count()
    };

    private byte[]? ReadSource(Source source)
    {
        if (source.Payload is not null)
        {
            return source.Payload;
        }

        try
        {
            return File.ReadAllBytes(source.SourcePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Файл занят или исчез между обходом и чтением — обычное дело на живой папке.
            // Пропустить один файл лучше, чем уронить весь экспорт.
            return null;
        }
    }

    private static string AppVersion()
    {
        var version = typeof(DataBundle).Assembly.GetName().Version;
        return version is null ? "" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Останется мусорный .tmp — это лучше, чем подменить исходную ошибку этой.
        }
    }
}
