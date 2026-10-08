using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Amarin.Core;

/// <summary>Откуда узнать о новой версии. Подменяется в тестах.</summary>
public interface IUpdateSource
{
    /// <summary>
    /// Проверить. Отказ сети или GitHub — обычный ответ с текстом (<see cref="UpdateCheckResult.Failed"/>),
    /// исключение — только отмена.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, bool beta, CancellationToken cancellationToken);
}

/// <summary>Файлы обновления: куда ставить, загрузка со сверкой, подмена. Подменяется в тестах.</summary>
public interface IUpdateFiles
{
    /// <summary>Где и как обновляться; false — причина отказа в <paramref name="error"/>.</summary>
    bool TryPlan(ReleaseInfo release, [NotNullWhen(true)] out UpdatePlan? plan, out string error);

    /// <summary>Скачать и сверить по размеру и SHA-256. Исключение — только отмена.</summary>
    Task<(UpdateStepResult Result, string? File)> DownloadAsync(
        UpdatePlan plan,
        IProgress<double> progress,
        bool allowUnverified,
        CancellationToken cancellationToken);

    /// <summary>Подмена без прав администратора: сверка суммы и подписи, затем два переименования.</summary>
    UpdateStepResult Swap(StagedUpdate staged);

    /// <summary>Подмена через UAC: короткий повышенный запуск той же программы.</summary>
    Task<UpdateStepResult> SwapElevatedAsync(StagedUpdate staged);
}

/// <summary>Программа вокруг обновлений: настройки, ходы, сохранение, перезапуск.</summary>
public interface IUpdateApp
{
    /// <summary>Галка «Автообновление».</summary>
    bool AutoUpdate { get; }

    /// <summary>Бета-канал.</summary>
    bool Beta { get; }

    /// <summary>Версия, от которой человек откатился (<see cref="AppSettings.DeclinedUpdate"/>).</summary>
    ReleaseVersion? Declined { get; }

    /// <summary>Идут ответы — перезапуск их оборвал бы.</summary>
    bool TurnsRunning { get; }

    /// <summary>Записать время проверки в настройки — для строки «Последняя проверка».</summary>
    void RememberCheck(DateTime utc);

    /// <summary>Сохранить всё перед подменой: дальше процесс уже завершается.</summary>
    void BeforeSwap();

    /// <summary>Запустить подменённый exe и закрыться. Null — получилось; иначе причина.</summary>
    string? RestartInto(string exePath);
}

/// <summary>Версии с GitHub: API, а при любом его отказе — страница релизов и SHA256SUMS.</summary>
public sealed class GitHubUpdateSource : IUpdateSource
{
    public Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, bool beta, CancellationToken cancellationToken) =>
        UpdateChecker.CheckAsync(current, beta, cancellationToken);
}

/// <summary>
/// Файлы обновления через <see cref="UpdateInstaller"/>: проверки там — сверка суммы, подписи,
/// адреса — не переписываются, а только зовутся.
/// </summary>
/// <param name="downloadHttp">Клиент загрузок. Функцией: службы окна заводятся позже, чем оно само.</param>
/// <param name="exePath">Путь к работающему exe.</param>
public sealed class InstallerUpdateFiles(Func<HttpClient?> downloadHttp, string? exePath) : IUpdateFiles
{
    public bool TryPlan(ReleaseInfo release, [NotNullWhen(true)] out UpdatePlan? plan, out string error) =>
        UpdateInstaller.TryPlan(release, exePath, out plan, out error);

    public async Task<(UpdateStepResult Result, string? File)> DownloadAsync(
        UpdatePlan plan,
        IProgress<double> progress,
        bool allowUnverified,
        CancellationToken cancellationToken)
    {
        if (downloadHttp() is not { } http)
        {
            return (UpdateStepResult.Failed(Loc.Format("S.Updates.NoConnection", "-")), null);
        }

        return await UpdateInstaller.DownloadAsync(plan, http, progress, cancellationToken, allowUnverified).ConfigureAwait(false);
    }

    public UpdateStepResult Swap(StagedUpdate staged) =>
        UpdateInstaller.Swap(staged.File, staged.Plan.ExePath, staged.Plan.Asset.Sha256);

    /// <remarks>
    /// Через UAC поднимается отдельный короткий запуск той же программы: он переставляет файл и
    /// выходит. Новую версию запускает потом обычный процесс — иначе программа после обновления
    /// осталась бы работать с правами администратора, о которых человек не просил. Сумма уходит
    /// ключом <c>--sha256</c>: повышенный процесс файла не скачивал и сверяет его сам.
    /// </remarks>
    public Task<UpdateStepResult> SwapElevatedAsync(StagedUpdate staged)
    {
        List<string> arguments = ["--apply-update", staged.File];
        if (staged.Plan.Asset.Sha256 is { Length: 64 } sha)
        {
            arguments.Add("--sha256");
            arguments.Add(sha);
        }

        return ElevatedRun.RunAsync(staged.Plan.ExePath, arguments);
    }
}

/// <summary>Короткий повышенный запуск этой же программы — подмена файла или откат.</summary>
public static class ElevatedRun
{
    /// <summary>Win32 ERROR_CANCELLED: человек нажал «Нет» в окне UAC. Обычный ответ, а не сбой.</summary>
    private const int ErrorCancelled = 1223;

    public static async Task<UpdateStepResult> RunAsync(string exePath, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo { FileName = exePath, UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var elevated = Process.Start(start);
            if (elevated is null)
            {
                return UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationFailed"));
            }

            await elevated.WaitForExitAsync().ConfigureAwait(false);
            return elevated.ExitCode == 0
                ? UpdateStepResult.Success
                : UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationFailed"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return UpdateStepResult.Failed(Loc.Get("S.Updates.ElevationRefused"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return UpdateStepResult.Failed(ex.Message);
        }
    }
}

/// <summary>Запуск преемника — новой версии после подмены или отката.</summary>
public static class AppRelaunch
{
    /// <summary>
    /// Запускает exe с <c>--await-exit</c> этого процесса. Null — запустился; иначе причина отказа.
    /// </summary>
    /// <remarks>
    /// <c>--await-exit</c> обязателен. Замок единственного экземпляра держится до конца процесса,
    /// а закрыться раньше, чем запустить преемника, этот процесс не может: без ожидания новый видит
    /// живого владельца, отдаёт ему запрос и выходит — оба процесса исчезают, и человек остаётся
    /// без окна.
    /// </remarks>
    public static string? StartSuccessor(string exePath)
    {
        var start = new ProcessStartInfo { FileName = exePath, UseShellExecute = true };
        start.ArgumentList.Add("--await-exit");
        start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        try
        {
            Process.Start(start);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return ex.Message;
        }
    }
}
