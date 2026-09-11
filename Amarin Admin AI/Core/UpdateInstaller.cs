using System.Buffers;
using System.Security.Cryptography;

namespace Amarin.Core;

/// <summary>Куда и как будет поставлено обновление. Всё, что нужно показать в вопросе к пользователю.</summary>
public sealed record UpdatePlan(ReleaseAsset Asset, string ExePath, string WorkDirectory)
{
    public string Folder => Path.GetDirectoryName(ExePath) ?? WorkDirectory;
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
/// Проверок три, и все обязательные: адрес должен быть https на домене релизов GitHub, размер
/// обязан совпасть с заявленным в API, и файл обязан сойтись по SHA-256, который тот же API
/// отдаёт вместе с релизом. Не сошлось — файл удаляется и ничего не подменяется.
/// </para>
/// </remarks>
public static class UpdateInstaller
{
    /// <summary>Подпапка для скачивания рядом с программой.</summary>
    public const string WorkFolderName = ".update";

    private const string BackupSuffix = ".old";

    /// <summary>Больше этого не скачиваем: страховка от подменённого заголовка длины.</summary>
    private const long MaxBytes = 512L * 1024 * 1024;

    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com"
    ];

    /// <summary>
    /// Можно ли обновиться на месте: программа должна быть обычным exe, а её папка — доступной
    /// на запись. В <c>Program Files</c> без прав администратора это не так, и честнее сказать
    /// об этом заранее, чем упасть на середине.
    /// </summary>
    public static bool TryPlan(ReleaseInfo release, string? exePath, out UpdatePlan plan, out string error)
    {
        plan = null!;
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

        var work = Path.Combine(folder, WorkFolderName);
        if (!TryEnsureWritable(work, out var writeError))
        {
            error = Loc.Format("S.Updates.NoWriteAccess", writeError);
            return false;
        }

        plan = new UpdatePlan(asset, path, work);
        return true;
    }

    /// <summary>Скачивает файл релиза и сверяет его. Возвращает путь к проверенному файлу.</summary>
    public static async Task<(UpdateStepResult Result, string? File)> DownloadAsync(
        UpdatePlan plan,
        HttpClient http,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(http);

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
    /// Ставит скачанный файл на место работающей программы. При любой осечке возвращает
    /// прежний exe обратно — остаться без исполняемого файла программа не должна.
    /// </summary>
    public static UpdateStepResult Swap(string downloadedFile, string exePath)
    {
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

    /// <summary>Убирает следы прошлого обновления. Зовётся при запуске, ошибки проглатывает.</summary>
    public static void CleanupLeftovers(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || Path.GetDirectoryName(exePath) is not { } folder)
        {
            return;
        }

        TryDelete(exePath + BackupSuffix);

        try
        {
            var work = Path.Combine(folder, WorkFolderName);
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Адрес обязан быть https и вести на домен, с которого GitHub отдаёт релизы.</summary>
    public static bool IsTrustedUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedHosts.Any(host =>
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> CopyAsync(
        HttpResponseMessage response,
        string path,
        long expected,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(path);
        using var hasher = SHA256.Create();

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

                hasher.TransformBlock(buffer, 0, read, null, 0);
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

        hasher.TransformFinalBlock([], 0, 0);
        progress?.Report(1);
        return Convert.ToHexString(hasher.Hash!).ToLowerInvariant();
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
}
