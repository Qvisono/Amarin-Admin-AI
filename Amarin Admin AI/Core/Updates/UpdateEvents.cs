namespace Amarin.Core;

/// <summary>Что случилось с обновлениями: нажатие, итог сетевого шага, такт часов, выход.</summary>
public abstract record UpdateEvent
{
    private UpdateEvent()
    {
    }

    /// <summary>Проверить: кнопкой «Проверить» (<paramref name="Manual"/>) или изнутри (выход, смена галки).</summary>
    public sealed record CheckRequested(bool Manual) : UpdateEvent;

    /// <summary>Такт часов: не пора ли автопроверке в сеть.</summary>
    public sealed record Heartbeat : UpdateEvent;

    /// <summary>Проверка с этим номером кончилась.</summary>
    public sealed record CheckFinished(int Generation, UpdateCheckResult Result) : UpdateEvent;

    /// <summary>Человек ответил «Применить» в вопросе об обновлении до этой версии.</summary>
    /// <param name="AllowUnverified">Сборка без суммы, и человек отдельно согласился её поставить.</param>
    public sealed record UpdateConfirmed(ReleaseInfo Release, UpdatePlan Plan, bool AllowUnverified) : UpdateEvent;

    /// <summary>Спланировать установку не вышло (нет сборки, папка недоступна).</summary>
    public sealed record PlanFailed(ReleaseVersion Version, string Error) : UpdateEvent;

    /// <summary>Отменить загрузку.</summary>
    public sealed record CancelRequested : UpdateEvent;

    /// <summary>Фоновая загрузка спланирована: куда ляжет файл и встанет ли без прав.</summary>
    public sealed record DownloadPlanned(int Generation, UpdatePlan Plan) : UpdateEvent;

    /// <summary>Доля скачанного у загрузки с этим номером.</summary>
    public sealed record DownloadProgress(int Generation, double Share) : UpdateEvent;

    /// <summary>Загрузка с этим номером кончилась: файл сверен или причина отказа.</summary>
    public sealed record DownloadFinished(int Generation, UpdateStepResult Result, string? File, UpdatePlan? Plan) : UpdateEvent;

    /// <summary>Загрузку с этим номером оборвала отмена.</summary>
    public sealed record DownloadCancelled(int Generation) : UpdateEvent;

    /// <summary>Подмена файла кончилась.</summary>
    public sealed record SwapFinished(StagedUpdate Staged, UpdateStepResult Result) : UpdateEvent;

    /// <summary>Файл подменён, а новую версию запустить не вышло.</summary>
    public sealed record RestartFailed(string Error) : UpdateEvent;

    /// <summary>Галка «Автообновление» поменялась (настройка уже записана).</summary>
    public sealed record AutoUpdateChanged(bool On) : UpdateEvent;

    /// <summary>Бета-канал включили или выключили (настройка уже записана).</summary>
    public sealed record ChannelChanged : UpdateEvent;

    /// <summary>Пошёл возврат к прошлой версии.</summary>
    public sealed record RollbackStarted : UpdateEvent;

    /// <summary>Прошлая версия встала на место программы: дальше только перезапуск.</summary>
    public sealed record RollbackDone : UpdateEvent;

    /// <summary>Возврат к прошлой версии не удался.</summary>
    public sealed record RollbackFailed(string Error) : UpdateEvent;

    /// <summary>
    /// Версия найдена, а загрузка не шла или сорвалась — попробовать ещё раз (выход доводит
    /// обновление уже без окна).
    /// </summary>
    public sealed record RetryDownload : UpdateEvent;

    /// <summary>Показать разовую строку.</summary>
    public sealed record NoticeShown(UpdateNoticeKind Kind, string? Detail = null) : UpdateEvent;

    /// <summary>Страницу открыли заново: ответы на прошлые нажатия (<see cref="UpdateNotice.Transient"/>) больше не к месту.</summary>
    public sealed record NoticesDismissed : UpdateEvent;

    /// <summary>Начат выход из программы.</summary>
    public sealed record ExitStarted : UpdateEvent;

    /// <summary>Выход отменён: программу запустили снова, и окно вернулось.</summary>
    public sealed record ExitCancelled : UpdateEvent;

    /// <summary>Скачанная сборка уходит на место программы при выходе (или выключении Windows).</summary>
    public sealed record SwapOnExitStarted : UpdateEvent;
}

/// <summary>Что сделать снаружи после перехода: сеть, диск, процессы. Исполняет <see cref="UpdateController"/>.</summary>
public abstract record UpdateEffect
{
    private UpdateEffect()
    {
    }

    /// <summary>Пойти в GitHub; итог — <see cref="UpdateEvent.CheckFinished"/> с этим номером.</summary>
    public sealed record StartCheck(int Generation) : UpdateEffect;

    /// <summary>Скачать выпуск; итог — <see cref="UpdateEvent.DownloadFinished"/> с этим номером.</summary>
    /// <param name="Plan">Готовый план (из вопроса человеку); null — спланировать самому.</param>
    public sealed record StartDownload(int Generation, ReleaseInfo Release, UpdatePlan? Plan, bool AllowUnverified) : UpdateEffect;

    /// <summary>Оборвать загрузку с этим номером.</summary>
    public sealed record CancelDownload(int Generation) : UpdateEffect;

    /// <summary>Сохранить чат и подменить файл; итог — <see cref="UpdateEvent.SwapFinished"/>.</summary>
    public sealed record Install(StagedUpdate Staged) : UpdateEffect;

    /// <summary>Запустить подменённый exe и закрыться.</summary>
    public sealed record Restart(string ExePath) : UpdateEffect;

    /// <summary>Запомнить в настройках время проверки — для строки «Последняя проверка».</summary>
    public sealed record RememberCheck : UpdateEffect;
}

/// <summary>Новое состояние и что сделать снаружи.</summary>
public sealed record UpdateTransition(UpdateState State, IReadOnlyList<UpdateEffect> Effects)
{
    public static UpdateTransition Stay(UpdateState state) => new(state, []);
}

/// <summary>
/// Всё, что переходу нужно знать о мире, кроме самого состояния: версия, настройки, часы, ходы.
/// </summary>
/// <param name="Current">Установленная версия.</param>
/// <param name="AutoUpdate">Галка «Автообновление».</param>
/// <param name="Declined">Версия, от которой человек откатился (её автообновление не ставит).</param>
/// <param name="TurnsRunning">Идут ответы — перезапуск их оборвал бы.</param>
/// <param name="NowUtc">Который час.</param>
public readonly record struct UpdateContext(
    ReleaseVersion Current,
    bool AutoUpdate,
    ReleaseVersion? Declined,
    bool TurnsRunning,
    DateTime NowUtc);
