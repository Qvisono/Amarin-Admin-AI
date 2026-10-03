namespace Amarin.Core;

/// <summary>Состояние обновлений одним словом — то, что показывает пилюля в плашке.</summary>
public enum UpdatePhase
{
    /// <summary>Новее нет (или проверка ещё не ходила).</summary>
    UpToDate,

    /// <summary>Идёт проверка, и сказать больше нечего — или проверку попросили кнопкой.</summary>
    Checking,

    /// <summary>Найдена версия новее установленной.</summary>
    Found,

    /// <summary>Идёт загрузка — фоновая или по кнопке (<see cref="UpdateDownload.Origin"/>).</summary>
    Downloading,

    /// <summary>Скачано и сверено, ждёт установки.</summary>
    Downloaded,

    /// <summary>Файл программы подменяется.</summary>
    Installing,

    /// <summary>Проверка, загрузка, подмена или откат не удались.</summary>
    Failed
}

/// <summary>Откуда загрузка обновления.</summary>
public enum UpdateDownloadOrigin
{
    /// <summary>Сама, по автообновлению: файл ляжет рядом и встанет при закрытии программы.</summary>
    Background,

    /// <summary>По кнопке «Обновить»: по окончании — подмена и перезапуск.</summary>
    User
}

/// <summary>Скачанная и сверенная сборка, которая ждёт установки.</summary>
/// <param name="AllowUnverified">Сборка без контрольной суммы — человек дважды согласился её поставить.</param>
public sealed record StagedUpdate(UpdatePlan Plan, string File, ReleaseVersion Version, bool AllowUnverified = false);

/// <summary>Идущая загрузка.</summary>
/// <param name="InstallRequested">
/// По окончании поставить и перезапустить. Фоновая загрузка получает это, когда человек жмёт
/// «Обновить» посреди неё: он присоединяется к уже идущей, а не отменяет её.
/// </param>
/// <param name="Generation">
/// Номер загрузки. Отчёт о доле и итог приходят очередью и могут опоздать: итог отменённой или
/// заменённой загрузки узнаётся по чужому номеру и отбрасывается.
/// </param>
/// <param name="Plan">Куда и как ставить; у фоновой загрузки появляется, когда её спланируют.</param>
public sealed record UpdateDownload(
    ReleaseInfo Release,
    UpdateDownloadOrigin Origin,
    bool InstallRequested,
    bool AllowUnverified,
    double Share,
    int Generation,
    UpdatePlan? Plan = null)
{
    public ReleaseVersion Version => Release.Release;

    /// <summary>Загрузку ждёт человек: по кнопке или присоединившись к фоновой.</summary>
    public bool Wanted => Origin == UpdateDownloadOrigin.User || InstallRequested;
}

/// <summary>Не удалась загрузка, подмена или откат. Версия — та, что ставили, если она была.</summary>
public sealed record UpdateFailure(ReleaseVersion? Version, string Error);

/// <summary>Разовая строка поверх обычного статуса плашки.</summary>
public enum UpdateNoticeKind
{
    /// <summary>Загрузку отменили.</summary>
    DownloadCancelled,

    /// <summary>Идут ответы — перезапуск их оборвал бы.</summary>
    WaitForTurns,

    /// <summary>Загрузка по кнопке кончилась посреди ответа: сборка ждёт кнопки или закрытия.</summary>
    ReadyAfterTurns,

    /// <summary>Страницу релиза не открыть.</summary>
    BrowserFailed,

    /// <summary>Идёт возврат к прошлой версии.</summary>
    RollingBack
}

/// <summary>Разовая строка: что случилось и подробность (текст ошибки), если есть.</summary>
public sealed record UpdateNotice(UpdateNoticeKind Kind, string? Detail = null)
{
    /// <summary>
    /// Ответ на последнее нажатие, а не факт: открытие страницы его снимает. Так было и до
    /// автомата — плашка, нарисованная заново, забывала «Загрузка отменена».
    /// </summary>
    public bool Transient => Kind is UpdateNoticeKind.DownloadCancelled or UpdateNoticeKind.WaitForTurns or UpdateNoticeKind.BrowserFailed;
}

/// <summary>
/// Всё, что программа знает об обновлениях в этом запуске. Неизменяемо: каждое событие даёт
/// новое состояние (<see cref="UpdateMachine.Next"/>).
/// </summary>
/// <remarks>
/// Факты, а не одна стадия: проверка идёт поверх найденного, скачанная v1 лежит, пока качается
/// v2, ошибка загрузки не стирает найденную версию. Слово для пилюли — <see cref="Phase"/> —
/// выводится из фактов в одном месте, и порядок там тот же, что был у плашки до 1.30.0.
/// </remarks>
public sealed record UpdateState
{
    public static UpdateState Initial { get; } = new();

    /// <summary>Лучший найденный выпуск новее установленного; null — не нашли или не проверяли.</summary>
    public ReleaseInfo? Latest { get; init; }

    /// <summary>Идущая загрузка; null — ничего не качается.</summary>
    public UpdateDownload? Download { get; init; }

    /// <summary>Скачанная сборка, ждущая установки — кнопкой или закрытием программы.</summary>
    public StagedUpdate? Staged { get; init; }

    /// <summary>
    /// Сборка, которая встаёт на место программы. Остаётся и после удачной подмены, до
    /// перезапуска: плашка до последнего говорит «Устанавливаем версию…», а не «Не удалось».
    /// </summary>
    public StagedUpdate? Installing { get; init; }

    /// <summary>
    /// Файл программы уже подменён. Дальше только перезапуск, и выход не должен принять ещё
    /// работающую прежнюю версию за недоведённое обновление и качать его заново.
    /// </summary>
    public bool SwapDone { get; init; }

    /// <summary>
    /// Файл подменён, а запустить новую версию не вышло — почему. Факт, а не разовая строка:
    /// программа на диске уже другая, и до перезапуска плашке сказать больше нечего.
    /// </summary>
    public string? RestartError { get; init; }

    /// <summary>Последняя неудача загрузки, подмены или отката; null — не было или уже неважна.</summary>
    public UpdateFailure? Failure { get; init; }

    /// <summary>Разовая строка поверх статуса.</summary>
    public UpdateNotice? Notice { get; init; }

    /// <summary>Идёт проверка.</summary>
    public bool CheckRunning { get; init; }

    /// <summary>Проверку попросил человек — плашка говорит «Проверяем», что бы ни было найдено.</summary>
    public bool CheckManual { get; init; }

    /// <summary>Номер последней начатой проверки: итог прежней (до смены канала) не применяется.</summary>
    public int CheckGeneration { get; init; }

    /// <summary>
    /// Почему не удалась последняя проверка; null — удалась или ещё не шла. Без этого плашка после
    /// неудачной проверки говорила бы «Последняя версия» — ровно то, чего программа не знает.
    /// </summary>
    public string? LastCheckError { get; init; }

    /// <summary>Когда в этом запуске GitHub в последний раз ответил. В памяти, не в настройках.</summary>
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>Когда автопроверке можно идти в сеть снова.</summary>
    public DateTime NextAutoCheckUtc { get; init; }

    /// <summary>Номер последней начатой загрузки.</summary>
    public int DownloadGeneration { get; init; }

    /// <summary>Выход начат: такт больше не проверяет, загрузка по кнопке не ставит, а откладывает.</summary>
    public bool Exiting { get; init; }

    /// <summary>Слово для пилюли. Одно место на все случаи.</summary>
    /// <remarks>
    /// Порядок тот же, что у плашки 1.29.0: идёт подмена, затем загрузка, затем проверка по
    /// кнопке, затем неудача, скачанное, найденное, фоновая проверка, неудачная проверка.
    /// Неудача загрузки выше найденной версии: иначе следующая перерисовка показывала бы «Есть
    /// обновление» сразу после «Не удалось» — тот самый разнобой, который был до автомата.
    /// Подменённый файл — «Установка», пока не случится перезапуск: и после обновления, и после
    /// возврата к прошлой версии остаётся только он.
    /// </remarks>
    public UpdatePhase Phase => this switch
    {
        { RestartError: not null } => UpdatePhase.Failed,
        { Installing: not null } => UpdatePhase.Installing,
        { Download: not null } => UpdatePhase.Downloading,
        { CheckRunning: true, CheckManual: true } => UpdatePhase.Checking,
        { Failure: not null } => UpdatePhase.Failed,
        { SwapDone: true } => UpdatePhase.Installing,
        { Staged: not null } => UpdatePhase.Downloaded,
        { Latest: not null } => UpdatePhase.Found,
        { CheckRunning: true } => UpdatePhase.Checking,
        { LastCheckError: not null } => UpdatePhase.Failed,
        _ => UpdatePhase.UpToDate
    };
}
