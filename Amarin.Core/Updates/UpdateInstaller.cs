using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Amarin.Core;

/// <summary>Куда и как будет поставлено обновление. Всё, что нужно показать в вопросе к пользователю.</summary>
/// <param name="NeedsElevation">
/// Папку программы правит только администратор: скачать и сверить файл процесс может сам, а
/// подмену придётся просить через UAC.
/// </param>
public sealed record UpdatePlan(
    ReleaseAsset Asset,
    string ExePath,
    string WorkDirectory,
    bool NeedsElevation = false)
{
    public string Folder => Path.GetDirectoryName(ExePath) ?? WorkDirectory;

    /// <summary>
    /// Есть ли чем сверить файл. Без суммы обновление не ставится само — только по отдельному
    /// согласию человека (<c>allowUnverified</c>).
    /// </summary>
    public bool Verified => Asset.Sha256 is { Length: 64 };
}

/// <summary>Результат шага обновления: либо получилось, либо человеческая причина отказа.</summary>
public sealed record UpdateStepResult(bool Ok, string? Error = null)
{
    public static readonly UpdateStepResult Success = new(true);

    public static UpdateStepResult Failed(string error) => new(false, error);
}

/// <summary>
/// Обновление программы на месте: скачать файл релиза, сверить его и заменить им себя.
/// </summary>
/// <remarks>
/// <para>
/// Работающий exe в Windows нельзя перезаписать, но можно переименовать. На этом всё и держится:
/// текущий файл уезжает в <c>.old</c> рядом, скачанный встаёт на его место, программа
/// перезапускается уже из нового файла, а <c>.old</c> удаляется при следующем запуске.
/// Скачивание идёт в подпапку рядом с exe — на том же диске, поэтому подмена это переименование,
/// а не копирование через полтерабайта.
/// </para>
/// <para>
/// Путь и имя файла при этом не меняются, и это не мелочь. Флаг «Запускать от имени
/// администратора» из свойств файла Windows держит в разделе <c>AppCompatFlags</c>, где ключом
/// служит полный путь к exe; по пути же находят программу ярлыки и закреплённая иконка в панели
/// задач. Переименовать обновлённый файл под новый номер версии значило бы потерять всё это
/// разом, поэтому имя остаётся прежним, а версия внутри — новой.
/// </para>
/// <para>
/// Проверки: адрес должен быть https на домене релизов GitHub, размер обязан совпасть
/// с заявленным в API, и файл обязан сойтись по SHA-256 — из поля <c>digest</c> API или из
/// файла <c>SHA256SUMS</c> того же релиза. Суммы нет — сам по себе файл не ставится: только
/// если человек отдельно согласился обновиться без проверки. Перед самой подменой сумма
/// считается ещё раз (файл мог поменяться, пока лежал), а подпись Authenticode сверяется по
/// правилу <see cref="AuthenticodeCheck.Refusal"/>. Не сошлось — ничего не подменяется.
/// </para>
/// </remarks>
public static class UpdateInstaller
{
    /// <summary>Подпапка для скачивания рядом с программой.</summary>
    public const string WorkFolderName = ".update";

    /// <summary>
    /// Запасная папка загрузки, когда рядом с программой писать нельзя.
    /// </summary>
    /// <remarks>
    /// Рядом с exe лучше: там подмена это переименование в пределах тома, а не копирование
    /// семидесяти восьми мегабайт. Но в защищённой папке обычный процесс не пишет вовсе, и выбор
    /// там не между двумя папками, а между временной папкой и «обновиться нельзя».
    /// </remarks>
    public const string TempWorkFolderName = "Amarin-Admin-AI-update";

    private const string BackupSuffix = ".old";

    /// <summary>
    /// Прошлая версия, сбережённая ради «Вернуть прошлую версию». <c>.old</c> становится ею после
    /// удачного запуска новой — до этого он только страховка самой подмены.
    /// </summary>
    public const string PreviousSuffix = ".previous";

    /// <summary>Больше этого не скачиваем: страховка от подменённого заголовка длины.</summary>
    private const long MaxBytes = 512L * 1024 * 1024;

    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    /// <summary>Общая на систему запасная папка загрузки.</summary>
    public static string TempWorkDirectory => Path.Combine(Path.GetTempPath(), TempWorkFolderName);

    /// <summary>
    /// Где и как обновляться: программа должна быть обычным exe, а файл — куда-то скачаться.
    /// Недоступная на запись папка программы больше не отказ, а лишь повод спросить права.
    /// </summary>
    public static bool TryPlan(ReleaseInfo release, string? exePath, [NotNullWhen(true)] out UpdatePlan? plan, out string error)
    {
        plan = null;
        error = "";

        if (release.WindowsBuild is not { } asset)
        {
            error = Loc.Get("S.Updates.NoWindowsBuild");
            return false;
        }

        if (!IsTrustedUrl(asset.Url))
        {
            error = Loc.Get("S.Updates.NotOnGitHub");
            return false;
        }

        var path = exePath;
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            error = Loc.Get("S.Updates.NoExePath");
            return false;
        }

        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
        {
            error = Loc.Get("S.Updates.NoExeFolder");
            return false;
        }

        var near = Path.Combine(folder, WorkFolderName);
        if (TryEnsureWritable(near, out _))
        {
            plan = new UpdatePlan(asset, path, near);
            return true;
        }

        // Писать рядом нельзя — значит, и подменить файл сам процесс не сможет. Скачиваем во
        // временную папку, а на подмену попросим права отдельным шагом.
        if (!TryEnsureWritable(TempWorkDirectory, out var writeError))
        {
            error = Loc.Format("S.Updates.NoWriteAccess", writeError);
            return false;
        }

        plan = new UpdatePlan(asset, path, TempWorkDirectory, NeedsElevation: true);
        return true;
    }

    /// <summary>Скачивает файл релиза и сверяет его. Возвращает путь к проверенному файлу.</summary>
    /// <param name="allowUnverified">
    /// Человек отдельно согласился поставить сборку, которую нечем сверить. Без этого сборка без
    /// контрольной суммы не скачивается вовсе.
    /// </param>
    public static async Task<(UpdateStepResult Result, string? File)> DownloadAsync(
        UpdatePlan plan,
        HttpClient http,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        bool allowUnverified = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(http);

        if (!plan.Verified && !allowUnverified)
        {
            return (UpdateStepResult.Failed(Loc.Get("S.Updates.NoChecksum")), null);
        }

        var target = Path.Combine(plan.WorkDirectory, plan.Asset.Name);
        var partial = target + ".part";

        try
        {
            Directory.CreateDirectory(plan.WorkDirectory);
            File.Delete(partial);

            using var request = new HttpRequestMessage(HttpMethod.Get, plan.Asset.Url);
            request.Headers.UserAgent.ParseAdd("Amarin-Admin-AI-Updater");

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return (UpdateStepResult.Failed(Loc.Format("S.Updates.Status", (int)response.StatusCode)), null);
            }

            var expected = plan.Asset.Size > 0 ? plan.Asset.Size : response.Content.Headers.ContentLength ?? 0;
            if (expected > MaxBytes)
            {
                return (UpdateStepResult.Failed(Loc.Get("S.Updates.TooBig")), null);
            }

            var hash = await CopyAsync(response, partial, expected, progress, cancellationToken)
                .ConfigureAwait(false);

            var actualSize = new FileInfo(partial).Length;
            if (plan.Asset.Size > 0 && actualSize != plan.Asset.Size)
            {
                File.Delete(partial);
                return (UpdateStepResult.Failed(
                    Loc.Format("S.Updates.SizeMismatch", plan.Asset.Size, actualSize)), null);
            }

            if (plan.Asset.Sha256 is { Length: 64 } expectedHash &&
                !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return (UpdateStepResult.Failed(Loc.Get("S.Updates.HashMismatch")), null);
            }

            File.Move(partial, target, overwrite: true);
            return (UpdateStepResult.Success, target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            return (UpdateStepResult.Failed(ex.Message), null);
        }
    }

    /// <summary>
    /// Последняя проверка перед подменой: сумма ещё раз и подпись по правилу.
    /// </summary>
    /// <remarks>
    /// Сумма — ещё раз, а не только при скачивании: между ними файл лежит на диске, и во
    /// временной папке его может подменить любой процесс этого пользователя. Для подмены с
    /// правами администратора это вообще единственная проверка — повышенный процесс файла не
    /// скачивал и ничему в нём не обязан верить.
    /// </remarks>
    /// <returns>Текст отказа; null — можно ставить.</returns>
    public static string? VerifyBeforeSwap(string downloadedFile, string exePath, string? expectedSha256)
    {
        if (expectedSha256 is { Length: > 0 })
        {
            string actual;
            try
            {
                using var stream = File.OpenRead(downloadedFile);
                actual = Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Loc.Format("S.Updates.InstallFailed", ex.Message);
            }

            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                return Loc.Get("S.Updates.HashMismatch");
            }
        }

        return AuthenticodeCheck.Refusal(AuthenticodeCheck.Inspect(exePath), AuthenticodeCheck.Inspect(downloadedFile));
    }

    /// <summary>
    /// Ставит скачанный файл на место работающей программы. При любой осечке возвращает
    /// прежний exe обратно — остаться без исполняемого файла программа не должна.
    /// </summary>
    /// <param name="expectedSha256">Сумма из релиза; null — обновление без проверки, с согласия.</param>
    public static UpdateStepResult Swap(string downloadedFile, string exePath, string? expectedSha256 = null)
    {
        if (VerifyBeforeSwap(downloadedFile, exePath, expectedSha256) is { } refusal)
        {
            return UpdateStepResult.Failed(refusal);
        }

        var backup = exePath + BackupSuffix;

        try
        {
            TryDelete(backup);

            // Работающий exe переименовать можно, перезаписать — нельзя.
            File.Move(exePath, backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UpdateStepResult.Failed(Loc.Format("S.Updates.CannotFreeExe", ex.Message));
        }

        try
        {
            // Тот же путь и то же имя, что были: по ним программу находят ярлыки и флаг
            // «Запускать от имени администратора» из свойств файла.
            File.Move(downloadedFile, exePath);
            return UpdateStepResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Move(backup, exePath);
            }
            catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
            {
                return UpdateStepResult.Failed(
                    Loc.Format("S.Updates.RollbackFailed", ex.Message, backup));
            }

            return UpdateStepResult.Failed(Loc.Format("S.Updates.InstallFailed", ex.Message));
        }
    }

    /// <summary>
    /// Подмена по просьбе неповышенного процесса: то же самое, но сначала проверив, что просят
    /// именно об обновлении.
    /// </summary>
    /// <remarks>
    /// Этот путь выполняется с правами администратора, поэтому «переложи любой файл поверх
    /// любого другого» он давать не должен. Отсюда две проверки: цель — сам работающий exe,
    /// источник — файл из папки загрузки обновлений, и ничей больше.
    /// </remarks>
    /// <param name="expectedSha256">Сумма, переданная обычным процессом ключом <c>--sha256</c>.</param>
    public static UpdateStepResult ApplyElevated(string? source, string? exePath, string? expectedSha256 = null)
    {
        if (string.IsNullOrWhiteSpace(exePath) ||
            !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(exePath))
        {
            return UpdateStepResult.Failed(Loc.Get("S.Updates.NoExePath"));
        }

        if (!IsUpdateWorkFile(source, exePath))
        {
            return UpdateStepResult.Failed(Loc.Get("S.Updates.NotOurFile"));
        }

        return Swap(source, exePath, expectedSha256);
    }

    /// <summary>
    /// Лежит ли файл в одной из папок, куда программа скачивает обновления.
    /// </summary>
    /// <remarks>
    /// Папка рядом с программой сверяется целым путём, временная — одним лишь именем. Причина в
    /// UAC: если у человека обычная учётная запись, повышение спрашивает пароль администратора, и
    /// повышенный процесс идёт уже от другого пользователя — со своим <c>%TEMP%</c>. Полный путь
    /// там не сойдётся никогда, и обновление отказывало бы ровно тем, кому без прав труднее всего.
    /// </remarks>
    public static bool IsUpdateWorkFile([NotNullWhen(true)] string? source, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            !source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(source))
        {
            return false;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(source));
        if (folder is null)
        {
            return false;
        }

        if (string.Equals(
                Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)),
                TempWorkFolderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var exeFolder = string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(exePath);
        return exeFolder is not null && SamePath(folder, Path.Combine(exeFolder, WorkFolderName));
    }

    /// <summary>
    /// Убирает следы прошлого обновления, а прежний exe сберегает как прошлую версию. Зовётся при
    /// запуске, ошибки проглатывает.
    /// </summary>
    /// <remarks>
    /// Раз новая версия дошла до окна, подмена удалась, и <c>.old</c> больше не страховка — но это
    /// единственная копия того, что работало вчера. До 1.28.0 он удалялся, и вернуться к прошлой
    /// версии после неудачного обновления можно было только скачав её руками. Хранится одна: на
    /// диске лежит ровно одна лишняя копия программы, а не по копии на каждое обновление.
    /// В защищённой папке переименовать нечем (прав нет) — там <c>.old</c> просто остаётся лежать,
    /// и <see cref="PreviousVersionPath"/> находит его сам.
    /// </remarks>
    public static void CleanupLeftovers(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || Path.GetDirectoryName(exePath) is not { } folder)
        {
            return;
        }

        var backup = exePath + BackupSuffix;
        if (File.Exists(backup))
        {
            try
            {
                File.Move(backup, exePath + PreviousSuffix, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        TryDeleteFolder(Path.Combine(folder, WorkFolderName));
        TryDeleteFolder(TempWorkDirectory);
    }

    /// <summary>Сбережённая прошлая версия рядом с программой; <c>null</c> — её нет.</summary>
    public static string? PreviousVersionPath(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        foreach (var candidate in new[] { exePath + PreviousSuffix, exePath + BackupSuffix })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Версия файла программы по его ресурсу версии; <c>null</c>, если её не прочесть.
    /// </summary>
    /// <remarks>
    /// <c>ProductVersion</c> у сборки .NET — это <c>AssemblyInformationalVersion</c>, с пометкой
    /// беты и хешем коммита после «+»; разбор отрезает хеш сам.
    /// </remarks>
    public static ReleaseVersion? FileVersionOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return ReleaseVersion.Parse(info.ProductVersion) ?? ReleaseVersion.Parse(info.FileVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ставит сбережённую прошлую версию на место работающей.
    /// </summary>
    /// <remarks>
    /// Тот же <see cref="Swap"/>, что и у обновления: работающий exe уезжает в <c>.old</c> (и после
    /// запуска прошлой версии станет уже её «прошлой» — вернуться обратно можно тем же путём), а
    /// подпись проверяется по тому же правилу. Суммы нет и быть не может — этот файл не
    /// скачивался, а уже работал на этой машине. Источник вычисляется из пути программы, а не
    /// берётся аргументом: повышенный процесс не должен уметь «переложить любой файл».
    /// </remarks>
    public static UpdateStepResult RollBack(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) ||
            !exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(exePath))
        {
            return UpdateStepResult.Failed(Loc.Get("S.Updates.NoExePath"));
        }

        if (PreviousVersionPath(exePath) is not { } previous)
        {
            return UpdateStepResult.Failed(Loc.Get("S.Updates.NoPrevious"));
        }

        // Swap первым делом освобождает место под .old — прошлую версию, лежащую там же, он бы
        // стёр. Поэтому она сначала переезжает на своё постоянное имя.
        if (previous.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var kept = exePath + PreviousSuffix;
            try
            {
                File.Move(previous, kept, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return UpdateStepResult.Failed(Loc.Format("S.Updates.InstallFailed", ex.Message));
            }

            previous = kept;
        }

        return Swap(previous, exePath);
    }

    /// <summary>
    /// Может ли этот процесс сам переставлять файлы в папке программы. Нет — откат, как и
    /// обновление, просит права через UAC. Проба — та же, что у <see cref="TryPlan"/>: папка
    /// загрузки рядом с программой (её убирает следующий запуск).
    /// </summary>
    public static bool CanSwapWithoutElevation(string exePath) =>
        Path.GetDirectoryName(exePath) is { Length: > 0 } folder &&
        TryEnsureWritable(Path.Combine(folder, WorkFolderName), out _);

    /// <summary>
    /// Понимает ли версия <see cref="AppSettings.DeclinedUpdate"/>. Более ранние о ней не знают и
    /// автообновлением поставили бы только что отвергнутую версию обратно при первом закрытии.
    /// </summary>
    public static bool KnowsDeclinedUpdate(ReleaseVersion? version) =>
        version is { } known && known >= new Version(1, 28, 0);

    /// <summary>Адрес обязан быть https и вести на домен, с которого GitHub отдаёт релизы.</summary>
    public static bool IsTrustedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static async Task<string> CopyAsync(
        HttpResponseMessage response,
        string path,
        long expected,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(path);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long total = 0;
        var lastReported = -1.0;

        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    throw new IOException(Loc.Get("S.Updates.TooBig"));
                }

                hasher.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (progress is null || expected <= 0)
                {
                    continue;
                }

                // Доля считается раз в процент: иначе на семидесяти мегабайтах интерфейс
                // получит десятки тысяч обновлений подряд.
                var share = Math.Min(1.0, (double)total / expected);
                if (share - lastReported >= 0.01)
                {
                    lastReported = share;
                    progress.Report(share);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress?.Report(1);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool TryEnsureWritable(string directory, out string error)
    {
        error = "";
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, "write.probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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
        }
    }
}
