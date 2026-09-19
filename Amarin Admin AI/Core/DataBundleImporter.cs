using System.IO.Compression;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Что программа увидела в архиве, не тронув ни одного своего файла.</summary>
/// <param name="Available">Категории, которые в архиве действительно есть.</param>
public sealed record DataBundleInspection(
    DataBundleError Error,
    DataCategory Available,
    IReadOnlyList<DataBundleCategoryInfo> Categories,
    string AppVersion,
    DateTime CreatedAt,
    long TotalBytes,
    string ActiveProfileId)
{
    public bool Ok => Error == DataBundleError.None;

    public static DataBundleInspection Failed(DataBundleError error) =>
        new(error, DataCategory.None, [], "", default, 0, "");
}

/// <summary>Что импорт сделал. Числа отсюда человек читает в окне «готово».</summary>
/// <param name="Skipped">Повреждённые файлы, которые пропустили поштучно.</param>
public sealed record DataImportResult(
    DataBundleError Error,
    int ChatsAdded,
    int ChatsReplaced,
    int ProfilesAdded,
    int LanguagesAdded,
    bool SettingsChanged,
    bool AppearanceChanged,
    int Skipped)
{
    public bool Ok => Error == DataBundleError.None;

    public bool ChangedAnything =>
        ChatsAdded > 0 || ChatsReplaced > 0 || ProfilesAdded > 0 ||
        LanguagesAdded > 0 || SettingsChanged || AppearanceChanged;

    public static DataImportResult Failed(DataBundleError error) =>
        new(error, 0, 0, 0, 0, false, false, 0);
}

/// <summary>
/// Разбор архива с данными и раскладка его содержимого по папкам программы.
/// </summary>
/// <remarks>
/// <para>
/// Работа идёт в две фазы: сначала архив целиком распаковывается и проверяется во временную папку
/// под корнем данных, и только потом содержимое переносится на места. Иначе архив, у которого
/// сороковая запись пытается вылезти за пределы папки, успел бы затереть тридцать девять живых
/// файлов, прежде чем его остановили.
/// </para>
/// <para>
/// Ничего из архива не копируется байтами, кроме картинок оформления: JSON проходит через
/// типизированный разбор, поэтому незнакомые поля отбрасываются, а кривой файл превращается в
/// «пропущено», а не в неработающую программу.
/// </para>
/// </remarks>
public sealed class DataBundleImporter
{
    private readonly string _root;
    private readonly string _activeProfileId;
    private readonly DataBundleLimits _limits;
    private readonly ProfileStore _profiles;

    /// <param name="rootDirectory">
    /// Корень данных. <c>null</c> — <see cref="AppPaths.Root"/>; тесты передают временную папку.
    /// </param>
    public DataBundleImporter(
        string? rootDirectory = null,
        string? activeProfileId = null,
        DataBundleLimits? limits = null)
    {
        _root = string.IsNullOrWhiteSpace(rootDirectory) ? AppPaths.Root : rootDirectory;
        _activeProfileId = string.IsNullOrWhiteSpace(activeProfileId)
            ? ProfileStore.DefaultProfileId
            : activeProfileId;
        _limits = limits ?? DataBundleLimits.Default;
        _profiles = new ProfileStore(_root);
    }

    /// <summary>
    /// Читает одну только опись. Не бросает: чужой или битый файл — обычный ответ, а не авария.
    /// </summary>
    public DataBundleInspection Inspect(string archivePath)
    {
        var (manifest, error) = ReadManifest(archivePath);
        if (manifest is null)
        {
            return DataBundleInspection.Failed(error);
        }

        var available = DataCategory.None;
        foreach (var entry in manifest.Entries)
        {
            available |= entry.Category;
        }

        var categories = manifest.Entries
            .GroupBy(entry => entry.Category)
            .Where(group => group.Key != DataCategory.None)
            .Select(group => new DataBundleCategoryInfo(
                group.Key,
                CountItems(group.Key, group),
                group.Sum(entry => entry.Bytes)))
            .OrderBy(info => Order(info.Category))
            .ToList();

        return new DataBundleInspection(
            DataBundleError.None,
            available,
            categories,
            manifest.AppVersion,
            manifest.CreatedAt,
            manifest.Entries.Sum(entry => entry.Bytes),
            manifest.ActiveProfileId);
    }

    /// <summary>Раскладывает выбранные категории. Первая фаза — всё или ничего.</summary>
    public DataImportResult Apply(
        string archivePath,
        DataCategory categories,
        DataImportMode mode,
        CancellationToken cancellationToken = default)
    {
        var (manifest, error) = ReadManifest(archivePath);
        if (manifest is null)
        {
            return DataImportResult.Failed(error);
        }

        // Временная папка под корнем данных, а не в %TEMP%: перенос на места должен идти в
        // пределах одного тома, иначе File.Move превращается в копирование гигабайтов.
        var staging = Path.Combine(_root, ".import-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var unpacked = Unpack(archivePath, manifest, categories, staging, cancellationToken);
            if (unpacked != DataBundleError.None)
            {
                return DataImportResult.Failed(unpacked);
            }

            return Distribute(staging, categories, mode, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DataImportResult.Failed(DataBundleError.WriteFailed);
        }
        finally
        {
            TryDeleteFolder(staging);
        }
    }

    // ───────────────────────── опись ─────────────────────────

    private (DataBundleManifest? Manifest, DataBundleError Error) ReadManifest(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return (null, DataBundleError.Unreadable);
        }

        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > _limits.MaxEntries)
            {
                return (null, DataBundleError.TooLarge);
            }

            var description = archive.GetEntry(DataBundle.ManifestName);
            if (description is null)
            {
                return (null, DataBundleError.NotOurArchive);
            }

            // Потолок и на саму опись: без него разжать «сводку» в гигабайт можно было бы ещё до
            // того, как человек увидит окно импорта.
            var bytes = ReadCapped(description, _limits.MaxManifestBytes);
            if (bytes is null)
            {
                return (null, DataBundleError.TooLarge);
            }

            var manifest = JsonSerializer.Deserialize<DataBundleManifest>(bytes, DataBundle.Json);
            if (manifest is null || !string.Equals(manifest.Format, DataBundle.FormatId, StringComparison.Ordinal))
            {
                return (null, DataBundleError.NotOurArchive);
            }

            if (manifest.FormatVersion > DataBundle.FormatVersion)
            {
                return (null, DataBundleError.NewerFormat);
            }

            // Опись, в которой есть путь наружу, — это не «архив с одной плохой записью», а
            // враждебный файл: у честного экспорта такого пути не бывает ни при каких данных.
            // Отказываем целиком и до распаковки, иначе остальное содержимое выглядело бы
            // безобидным и молча приехало бы на диск.
            if (manifest.Entries.Any(entry =>
                    !DataBundle.IsSafeEntryName(entry.Path) ||
                    !entry.Path.StartsWith(DataBundle.DataPrefix, StringComparison.Ordinal)))
            {
                return (null, DataBundleError.Unreadable);
            }

            manifest.Entries.RemoveAll(entry => entry.Category == DataCategory.None);

            if (manifest.Entries.Count == 0)
            {
                return (null, DataBundleError.EmptyArchive);
            }

            if (manifest.Entries.Sum(entry => Math.Max(0, entry.Bytes)) > _limits.MaxUnpackedBytes)
            {
                // Отказать по заявленному размеру дешевле, чем по факту распаковки.
                return (null, DataBundleError.TooLarge);
            }

            return (manifest, DataBundleError.None);
        }
        catch (InvalidDataException)
        {
            return (null, DataBundleError.NotAZip);
        }
        catch (JsonException)
        {
            return (null, DataBundleError.NotOurArchive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, DataBundleError.Unreadable);
        }
    }

    // ───────────────────────── фаза 1: распаковка ─────────────────────────

    /// <summary>
    /// Раскладывает архив во временную папку, проверяя каждую запись.
    /// </summary>
    /// <remarks>
    /// Любое нарушение пути — отказ всего импорта, а не пропуск одной записи: архив, который
    /// пытается вылезти за пределы папки данных, не «частично плохой», он враждебный.
    /// </remarks>
    private DataBundleError Unpack(
        string archivePath,
        DataBundleManifest manifest,
        DataCategory categories,
        string staging,
        CancellationToken cancellationToken)
    {
        // Опись работает белым списком: чего в ней нет, того не будет и на диске.
        var allowed = manifest.Entries
            .Where(entry => categories.HasFlag(entry.Category))
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        if (allowed.Count == 0)
        {
            return DataBundleError.EmptyArchive;
        }

        Directory.CreateDirectory(staging);
        var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        var total = 0L;

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Запись-каталог: имени файла у неё нет, содержимого тоже.
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                continue;
            }

            var name = entry.FullName.Replace('\\', '/');
            if (name == DataBundle.ManifestName)
            {
                continue;
            }

            if (!allowed.ContainsKey(name))
            {
                // Файл не из описи. Это не обязательно злой умысел, но и брать его не за что.
                continue;
            }

            if (!DataBundle.IsSafeEntryName(name) || !name.StartsWith(DataBundle.DataPrefix, StringComparison.Ordinal))
            {
                return DataBundleError.Unreadable;
            }

            var target = Path.GetFullPath(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
            {
                // Последний рубеж: имя прошло проверки, а путь всё равно указал наружу.
                return DataBundleError.Unreadable;
            }

            var bytes = ReadCapped(entry, Math.Min(_limits.MaxEntryBytes, _limits.MaxUnpackedBytes - total));
            if (bytes is null)
            {
                return DataBundleError.TooLarge;
            }

            total += bytes.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
        }

        return DataBundleError.None;
    }

    /// <summary>
    /// Читает запись архива с жёстким потолком.
    /// </summary>
    /// <remarks>
    /// Ручной цикл, а не CopyTo, и счёт по реально прочитанным байтам, а не по
    /// <c>entry.Length</c>: заголовок ZIP пишет тот, кто собрал архив, и соврать в нём ничего не
    /// стоит. Тот же приём защищает разбор кода чата в <see cref="ChatShareCodec"/>.
    /// </remarks>
    private static byte[]? ReadCapped(ZipArchiveEntry entry, long limit)
    {
        if (limit <= 0)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // ───────────────────────── фаза 2: раскладка ─────────────────────────

    private DataImportResult Distribute(
        string staging,
        DataCategory categories,
        DataImportMode mode,
        CancellationToken cancellationToken)
    {
        var data = Path.Combine(staging, "data");
        if (!Directory.Exists(data))
        {
            return DataImportResult.Failed(DataBundleError.EmptyArchive);
        }

        var state = new ImportState();
        var registry = _profiles.Load();

        // Профили идут первыми: пока не известно, под каким id ляжет чужой профиль, непонятно и
        // в какую папку раскладывать его чаты.
        var map = ApplyProfiles(data, registry, categories, mode, state);

        ApplyTree(
            data,
            _profiles.DataRootFor(_activeProfileId),
            categories,
            mode,
            isNewProfile: false,
            state,
            cancellationToken);

        foreach (var (archiveId, localId) in map)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folder = Path.Combine(data, DataBundle.ProfilesFolder, archiveId);
            if (Directory.Exists(folder))
            {
                ApplyTree(
                    folder,
                    _profiles.DataRootFor(localId),
                    categories,
                    mode,
                    isNewProfile: true,
                    state,
                    cancellationToken);
            }
        }

        if (categories.HasFlag(DataCategory.Languages))
        {
            ApplyLanguages(Path.Combine(data, "languages"), mode, state);
        }

        return new DataImportResult(
            DataBundleError.None,
            state.ChatsAdded,
            state.ChatsReplaced,
            state.ProfilesAdded,
            state.LanguagesAdded,
            state.SettingsChanged,
            state.AppearanceChanged,
            state.Skipped);
    }

    /// <summary>
    /// Заводит приехавшие профили и возвращает карту «id в архиве — id на этой машине».
    /// </summary>
    private Dictionary<string, string> ApplyProfiles(
        string data,
        ProfileRegistry registry,
        DataCategory categories,
        DataImportMode mode,
        ImportState state)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!categories.HasFlag(DataCategory.Profiles))
        {
            return map;
        }

        var incoming = ReadJson<ProfileRegistry>(Path.Combine(data, "profiles.json"), state);
        if (incoming is null)
        {
            return map;
        }

        var mine = _profiles.Active(registry);
        var kept = registry.Profiles.ToDictionary(p => p.Id, StringComparer.Ordinal);

        if (mode == DataImportMode.Replace)
        {
            // Активный профиль обязан остаться в реестре: под ним сейчас работает окно, и
            // программа, запущенная под профилем, которого в списке нет, — это сломанный список.
            registry.Profiles = [mine];
            registry.ActiveProfileId = mine.Id;

            // Замена — это восстановление копии, поэтому активный профиль забирает у архива имя
            // и аватар: иначе «переустановил Windows и вернул всё как было» возвращало бы чаты,
            // но оставляло человека «Гостем». Id, пароль и замок остаются своими — они про эту
            // машину, а не про перенесённые данные.
            var source = incoming.Profiles.FirstOrDefault(p => p.Id == incoming.ActiveProfileId);
            if (source is not null && !string.IsNullOrWhiteSpace(source.Name))
            {
                mine.Name = source.Name;
                mine.AvatarFileName = source.AvatarFileName;
            }
        }

        foreach (var profile in incoming.Profiles)
        {
            // Активный профиль архива лежал в его корне, и все его данные только что уехали в
            // папку активного профиля получателя. Завести под него ещё и отдельный профиль
            // значило бы добавить в список пустого двойника.
            if (profile.Id == incoming.ActiveProfileId)
            {
                continue;
            }

            var id = TakeId(profile.Id, registry);
            map[profile.Id] = id;
            registry.Profiles.Add(Adopt(profile, id, kept));
            state.ProfilesAdded++;
        }

        _profiles.Save(registry);
        return map;
    }

    /// <summary>
    /// Свободный id для приехавшего профиля.
    /// </summary>
    /// <remarks>
    /// Совпадение id ломает <see cref="ProfileStore.DataRootFor"/> — два разных профиля указали бы
    /// в одну папку. Id <c>default</c> не берут никогда: его папка — это сам корень данных, где
    /// уже живёт профиль получателя.
    /// </remarks>
    private static string TakeId(string archiveId, ProfileRegistry registry)
    {
        var taken = ProfileStore.IsDefault(archiveId) ||
                    registry.Profiles.Any(p => p.Id == archiveId);

        return taken ? Guid.NewGuid().ToString("N")[..12] : archiveId;
    }

    /// <param name="kept">Мои прежние профили: у совпавших по id забираем пароль обратно.</param>
    private static UserProfile Adopt(UserProfile profile, string id, Dictionary<string, UserProfile>? kept)
    {
        UserProfile? mine = null;
        kept?.TryGetValue(profile.Id, out mine);

        return new UserProfile
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(profile.Name) ? Loc.Get("S.Bundle.ImportedSuffix") : profile.Name,
            AvatarFileName = profile.AvatarFileName,
            CreatedAt = profile.CreatedAt == default ? DateTime.Now : profile.CreatedAt,

            // Пароль — свойство этой машины и этого человека, а не переносимых данных. Свой
            // возвращаем, чужой не берём никогда: архив мог быть собран руками, и подсунутый
            // хэш с lockOnStartup запер бы программу насмерть.
            PasswordHash = mine?.PasswordHash,
            PasswordSalt = mine?.PasswordSalt,
            LockOnStartup = mine?.LockOnStartup ?? false
        };
    }

    /// <summary>Чаты, настройки и оформление одного профиля.</summary>
    /// <param name="isNewProfile">
    /// Профиль только что заведён импортом. В слиянии это и есть условие, при котором настройки
    /// и оформление применяются: своё человек менять не просил.
    /// </param>
    private void ApplyTree(
        string source,
        string targetRoot,
        DataCategory categories,
        DataImportMode mode,
        bool isNewProfile,
        ImportState state,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var overwrite = mode == DataImportMode.Replace || isNewProfile;

        if (categories.HasFlag(DataCategory.Chats))
        {
            ApplyChats(Path.Combine(source, "chats"), targetRoot, mode, state, cancellationToken);
        }

        if (categories.HasFlag(DataCategory.Settings) && overwrite)
        {
            ApplySettings(Path.Combine(source, "settings.json"), targetRoot, state);
        }

        // Заготовки промптов — список, а не набор полей, поэтому они переносятся и в слиянии,
        // как чаты: «добавить недостающее, своё оставить» для списка выполнимо, а для настроек
        // нет. Условие overwrite здесь поэтому не проверяется.
        if (categories.HasFlag(DataCategory.Settings))
        {
            ApplyPrompts(Path.Combine(source, "prompts.json"), targetRoot, mode, state);
        }

        if (categories.HasFlag(DataCategory.Appearance) && overwrite)
        {
            ApplyAppearance(source, targetRoot, state);
        }
    }

    private void ApplyChats(
        string source,
        string targetRoot,
        DataImportMode mode,
        ImportState state,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var store = new ChatStore(targetRoot);
        if (mode == DataImportMode.Replace)
        {
            store.DeleteAll();
        }

        // Индекс — справочник: IsPinned живёт только в нём, в файле самого чата закрепления нет.
        var index = ReadJson<ChatIndex>(Path.Combine(source, "index.json"), state);
        var pinned = index?.Items.Where(item => item.IsPinned).Select(item => item.Id).ToHashSet(StringComparer.Ordinal)
                     ?? [];

        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.Equals(Path.GetFileName(file), "index.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var session = ReadJson<ChatSession>(file, state);
            if (session is null)
            {
                // Повреждённый файл уже посчитан внутри ReadJson — второй раз не считаем.
                continue;
            }

            if (string.IsNullOrWhiteSpace(session.Id))
            {
                state.Skipped++;
                continue;
            }

            var archiveId = session.Id;
            var replaced = false;

            if (mode == DataImportMode.Merge && store.TryLoad(session.Id) is not null)
            {
                // Отличить «мой же чат, который я сам экспортировал» от «чужой чат с тем же id»
                // нельзя, а перезаписать свой разговор чужим — худший из исходов. Приём тот же,
                // что у ChatShareCodec при открытии чата по коду.
                session.Id = Guid.NewGuid().ToString("N");
                session.Title = $"{session.Title} {Loc.Get("S.Bundle.ImportedSuffix")}".Trim();
            }
            else if (mode == DataImportMode.Replace)
            {
                replaced = true;
            }

            store.Save(session);
            if (pinned.Contains(archiveId))
            {
                store.SetPinned(session.Id, true);
            }

            if (replaced)
            {
                state.ChatsReplaced++;
            }
            else
            {
                state.ChatsAdded++;
            }
        }

        // Хранилище пишет в фоне, а это хранилище тут же выбрасывается: без этого импорт
        // возвращал бы «готово» ещё до того, как чаты легли на диск.
        store.Flush();
    }

    private void ApplySettings(string file, string targetRoot, ImportState state)
    {
        var incoming = ReadJson<AppSettings>(file, state);
        if (incoming is null)
        {
            return;
        }

        var store = new AppSettingsStore(targetRoot);
        var mine = store.Load();

        // Белый список загрузок — это настройка безопасности: он решает, откуда агенту разрешено
        // качать файлы на этот компьютер. Чужой архив не должен добавлять туда ничего.
        incoming.DownloadAllowedDomains = mine.DownloadAllowedDomains;

        // «Подтверждать всё автоматически» — осознанный выбор человека, а не чужого файла.
        incoming.ApprovalMode = mine.ApprovalMode;

        store.Save(incoming);
        state.SettingsChanged = true;
    }

    /// <summary>
    /// Библиотека заготовок основного промпта.
    /// </summary>
    /// <remarks>
    /// В слиянии совпавшие заготовки пропускаются дважды: по <c>Id</c> — это «я экспортировал,
    /// переустановил Windows и вернул своё», и по паре «имя + текст» — это одна и та же
    /// заготовка, заведённая руками на двух машинах, у которой id разошлись. Без второй проверки
    /// человек получил бы две неотличимые плитки и не понял бы, какую из них удалять.
    /// </remarks>
    private static void ApplyPrompts(
        string file,
        string targetRoot,
        DataImportMode mode,
        ImportState state)
    {
        var incoming = ReadJson<List<PromptPreset>>(file, state);
        if (incoming is null || incoming.Count == 0)
        {
            return;
        }

        var library = new PromptLibrary(targetRoot);
        List<PromptPreset> result = mode == DataImportMode.Replace ? [] : library.Load();

        var ids = result.Select(preset => preset.Id).ToHashSet(StringComparer.Ordinal);
        var texts = result
            .Select(preset => preset.Name + "\n" + preset.Text)
            .ToHashSet(StringComparer.Ordinal);

        var added = 0;
        foreach (var preset in incoming)
        {
            // Пустая заготовка не показывается плиткой и удалить её человеку будет нечем —
            // то же правило, по которому её отбрасывает PromptLibrary.Load.
            if (string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrWhiteSpace(preset.Text))
            {
                state.Skipped++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(preset.Id))
            {
                preset.Id = Guid.NewGuid().ToString("N");
            }

            if (!ids.Add(preset.Id) || !texts.Add(preset.Name + "\n" + preset.Text))
            {
                continue;
            }

            result.Add(preset);
            added++;
        }

        if (added == 0 && mode != DataImportMode.Replace)
        {
            return;
        }

        library.Save(result);
        state.SettingsChanged = true;
    }

    private static void ApplyAppearance(string source, string targetRoot, ImportState state)
    {
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);
            var isAppearance = string.Equals(name, "avatar.png", StringComparison.OrdinalIgnoreCase) ||
                               name.StartsWith("background.", StringComparison.OrdinalIgnoreCase);

            if (!isAppearance || !IsAllowedImage(file))
            {
                continue;
            }

            Directory.CreateDirectory(targetRoot);
            File.Copy(file, Path.Combine(targetRoot, name), overwrite: true);
            state.AppearanceChanged = true;
        }
    }

    /// <summary>
    /// Переводы интерфейса. Они общие на программу и лежат только в корне данных — в папку
    /// профиля их класть нельзя, там их никто не ищет.
    /// </summary>
    private void ApplyLanguages(string source, DataImportMode mode, ImportState state)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        var target = Path.Combine(_root, "languages");
        if (mode == DataImportMode.Replace && Directory.Exists(target))
        {
            foreach (var stale in Directory.EnumerateFiles(target, "*.json"))
            {
                TryDeleteFile(stale);
            }
        }

        foreach (var file in Directory.EnumerateFiles(source, "*.json"))
        {
            var code = Path.GetFileNameWithoutExtension(file);
            if (!IsLanguageCode(code))
            {
                state.Skipped++;
                continue;
            }

            if (mode == DataImportMode.Merge && File.Exists(Path.Combine(target, code + ".json")))
            {
                continue;
            }

            var strings = ReadJson<Dictionary<string, string>>(file, state);
            if (strings is null)
            {
                continue;
            }

            // Только подписи интерфейса: чужой файл не подсунет сюда ничего другого.
            var clean = strings
                .Where(pair => pair.Key.StartsWith("S.", StringComparison.Ordinal))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            if (clean.Count == 0)
            {
                continue;
            }

            Directory.CreateDirectory(target);
            AppDataFile.WriteAtomic(
                Path.Combine(target, code + ".json"),
                JsonSerializer.Serialize(clean, AppJson.Options));
            state.LanguagesAdded++;
        }
    }

    // ───────────────────────── мелочи ─────────────────────────

    /// <summary>Счётчики по ходу дела: тащить их семью out-параметрами было бы нечитаемо.</summary>
    private sealed class ImportState
    {
        public int ChatsAdded;
        public int ChatsReplaced;
        public int ProfilesAdded;
        public int LanguagesAdded;
        public bool SettingsChanged;
        public bool AppearanceChanged;
        public int Skipped;
    }

    /// <summary>Разбирает файл из временной папки. Битый — не авария, а «пропущено».</summary>
    private static T? ReadJson<T>(string path, ImportState state) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), AppJson.Options);
            if (value is null)
            {
                state.Skipped++;
            }

            return value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Один повреждённый чат не должен отменять перенос сорока остальных.
            state.Skipped++;
            return null;
        }
    }

    private static bool IsAllowedImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Length <= DataBundle.MaxImageBytes &&
                   DataBundle.ImageExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsLanguageCode(string code) =>
        code.Length is > 0 and <= 16 &&
        code.All(symbol => char.IsAsciiLetterOrDigit(symbol) || symbol == '-');

    private static int CountItems(DataCategory category, IEnumerable<DataBundleEntry> entries) =>
        category switch
        {
            DataCategory.Chats => entries.Count(entry =>
                !entry.Path.EndsWith("/index.json", StringComparison.Ordinal)),
            _ => entries.Count()
        };

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занятый файл — не повод валить импорт целиком.
        }
    }

    private static void TryDeleteFolder(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Останется папка .import-… рядом с данными. Неприятно, но безопасно.
        }
    }
}
