using System.Globalization;

namespace Amarin.Core;

/// <summary>Что делает главная кнопка плашки обновлений.</summary>
public enum UpdateAction
{
    /// <summary>Кнопки нет.</summary>
    None,

    /// <summary>«Обновить»: вопрос, затем загрузка (или присоединение к фоновой) и установка.</summary>
    Update,

    /// <summary>«Установить сейчас»: скачанное — на место программы и перезапуск.</summary>
    Install,

    /// <summary>«Отмена»: оборвать загрузку, которую ждёт человек.</summary>
    Cancel
}

/// <summary>
/// Плашка обновлений, как её надо нарисовать: пилюля, строка, кнопки, полоса, метка в боковой
/// колонке настроек. Окно только переносит это на контролы.
/// </summary>
/// <param name="Progress">Доля скачанного; null — полосы нет.</param>
/// <param name="SidebarAccent">Метку в боковой колонке красить акцентом: есть новая версия.</param>
public sealed record UpdateView(
    UpdatePhase Phase,
    string Status,
    bool StatusAccent,
    UpdateAction Action,
    bool ShowOpenRelease,
    bool CanCheck,
    double? Progress,
    string SidebarText,
    bool SidebarAccent)
{
    /// <summary>Подпись главной кнопки.</summary>
    public string ActionLabel => Action switch
    {
        UpdateAction.Install => Loc.Get("S.Updates.Install"),
        UpdateAction.Cancel => Loc.Get("S.Common.Cancel"),
        _ => Loc.Get("S.Updates.Update")
    };

    /// <summary>
    /// Плашка по состоянию. Одно место на все случаи: пока каждое событие красило плашку само,
    /// повторная проверка поверх скачанной версии возвращала «Есть обновление» вместо «Готово к
    /// установке», а страница, открытая посреди загрузки, писала «Доступна версия» над кнопкой
    /// «Отменить».
    /// </summary>
    /// <param name="current">Установленная версия, как её показывать.</param>
    public static UpdateView From(UpdateState state, string current)
    {
        ArgumentNullException.ThrowIfNull(state);

        var phase = state.Phase;
        var (status, accent) = StatusOf(state, phase, current);
        var (sidebar, sidebarAccent) = SidebarOf(state, current);

        return new UpdateView(
            phase,
            status,
            accent,
            ActionOf(state),
            ShowOpenRelease: phase is not (UpdatePhase.UpToDate or UpdatePhase.Checking),
            CanCheck: !state.CheckRunning && !state.SwapDone && state.Installing is null && state.Download is not { Wanted: true },
            Progress: state.Download?.Share,
            sidebar,
            sidebarAccent);
    }

    private static (string Text, bool Accent) StatusOf(UpdateState state, UpdatePhase phase, string current)
    {
        // Файл подменён, перезапуск не случился: важнее этого на плашке ничего нет.
        if (state.RestartError is { } restart)
        {
            return (Loc.Format("S.Updates.RestartFailed", restart), false);
        }

        // Разовая строка — поверх обычного статуса: «отменено», «дождитесь ответов».
        if (state.Notice is { } notice)
        {
            return NoticeText(notice, state);
        }

        switch (phase)
        {
            case UpdatePhase.Installing when state.Installing is { } installing:
                return (Loc.Format("S.Updates.InstallingVersion", installing.Version), false);

            case UpdatePhase.Downloading when state.Download is { } download:
            {
                var percent = (download.Share * 100).ToString("0", CultureInfo.CurrentCulture);
                return (Loc.Format(
                    download.InstallRequested ? "S.Updates.DownloadingToInstall" : "S.Updates.DownloadingVersion",
                    download.Version,
                    percent), false);
            }

            case UpdatePhase.Checking:
                return (Loc.Get("S.Updates.Checking"), false);

            case UpdatePhase.Downloaded when state.Staged is { } staged:
                return (Loc.Format(staged.Plan.NeedsElevation ? "S.Updates.ReadyAdmin" : "S.Updates.Ready", staged.Version), true);

            // Без файла сборки кнопке нечего ставить — тогда строка прямо говорит, где взять
            // версию, а не оставляет человека гадать, куда делась кнопка.
            case UpdatePhase.Found when state.Latest is { } latest:
                return (Loc.Format(
                    latest.WindowsBuild is null ? "S.Updates.AvailableNoBuild" : "S.Updates.Available",
                    latest.Release,
                    current), true);

            case UpdatePhase.Failed:
                return (FailureText(state), false);

            default:
                return (Loc.Format(state.LastSuccessUtc is null ? "S.Updates.Installed" : "S.Updates.UpToDate", current), false);
        }
    }

    private static string FailureText(UpdateState state) => state.Failure switch
    {
        { Version: { } version } failure => Loc.Format("S.Updates.FailedVersion", version, failure.Error),
        { } failure => Loc.Format("S.Updates.Failed", failure.Error),
        _ => state.LastCheckError ?? Loc.Get("S.Updates.CheckFailed")
    };

    private static (string Text, bool Accent) NoticeText(UpdateNotice notice, UpdateState state) => notice.Kind switch
    {
        UpdateNoticeKind.DownloadCancelled => (Loc.Get("S.Updates.DownloadCancelled"), false),
        UpdateNoticeKind.WaitForTurns => (Loc.Get("S.Updates.WaitForTurns"), false),
        UpdateNoticeKind.ReadyAfterTurns => (Loc.Format("S.Updates.ReadyAfterTurns", state.Staged?.Version.ToString() ?? ""), true),
        UpdateNoticeKind.BrowserFailed => (Loc.Format("S.Updates.BrowserFailed", notice.Detail ?? ""), false),
        UpdateNoticeKind.RollingBack => (Loc.Get("S.Updates.RollingBack"), false),
        _ => ("", false)
    };

    /// <summary>
    /// Главная кнопка — по фактам, а не по пилюле: на время ручной проверки найденная версия не
    /// теряет кнопку «Обновить».
    /// </summary>
    private static UpdateAction ActionOf(UpdateState state)
    {
        if (state.Installing is not null || state.SwapDone)
        {
            return UpdateAction.None;
        }

        // Фоновую загрузку кнопка не отменяет, а присоединяет к ней установку: подпись «Обновить»
        // и значит «обновить», а не «отменить». «Отмена» — только у загрузки, которую уже ждут.
        if (state.Download is { } download)
        {
            return download.Wanted ? UpdateAction.Cancel : UpdateAction.Update;
        }

        // Неудача — повод попробовать ещё раз сразу, а не после следующей проверки.
        if (state.Failure is not null)
        {
            return state.Staged is not null || state.Latest?.WindowsBuild is not null ? UpdateAction.Update : UpdateAction.None;
        }

        if (state.Staged is not null)
        {
            return UpdateAction.Install;
        }

        return state.Latest?.WindowsBuild is not null ? UpdateAction.Update : UpdateAction.None;
    }

    /// <summary>
    /// Метка у номера версии в боковой колонке настроек: единственное место, где о новой версии
    /// видно, не открывая страницу. Называет и установленную, и новую версию.
    /// </summary>
    private static (string Text, bool Accent) SidebarOf(UpdateState state, string current)
    {
        if (state.Download is { } download)
        {
            return (Loc.Format("S.Updates.SidebarNewer", current, download.Version), true);
        }

        if ((state.Installing ?? state.Staged) is { } staged)
        {
            return (Loc.Format("S.Updates.SidebarStaged", current, staged.Version), true);
        }

        return state.Latest is { } latest
            ? (Loc.Format("S.Updates.SidebarNewer", current, latest.Release), true)
            : ("v" + current, false);
    }
}
