namespace Amarin.Core;

/// <summary>Что делает отложенная задача, когда приходит её срок.</summary>
public enum DeferredKind
{
    /// <summary>Показывает напоминание: карточку с мелодией, которая ждёт «Готово».</summary>
    Reminder,

    /// <summary>Выполняет одобренную человеком при создании команду PowerShell.</summary>
    Command,

    /// <summary>Поручает задачу агенту в том чате, где её поставили.</summary>
    Agent,

    /// <summary>Открывает программы, которые были открыты при прошлом выключении.</summary>
    RestorePrograms
}

/// <summary>Когда задача срабатывает впервые.</summary>
public enum DeferredTriggerKind
{
    /// <summary>В момент <see cref="DeferredTask.NextDueUtc"/>.</summary>
    At,

    /// <summary>Когда время работы Windows дорастёт до <see cref="DeferredTrigger.UptimeHours"/>.</summary>
    Uptime,

    /// <summary>При следующем включении компьютера (входе в Windows).</summary>
    NextBoot,

    /// <summary>При следующем запуске программы.</summary>
    NextAppStart,

    /// <summary>Когда проверочный скрипт впервые ответит «да».</summary>
    Condition
}

/// <summary>Повторяется ли задача после срабатывания.</summary>
public enum DeferredRepeatKind
{
    Once,

    /// <summary>Каждые <see cref="DeferredRepeat.EveryMinutes"/> минут.</summary>
    Every,

    /// <summary>Каждый день в <see cref="DeferredRepeat.Time"/>.</summary>
    Daily,

    /// <summary>По дням недели <see cref="DeferredRepeat.Days"/> в <see cref="DeferredRepeat.Time"/>.</summary>
    Weekly,

    /// <summary>При каждом запуске программы.</summary>
    EveryAppStart,

    /// <summary>При каждом включении компьютера (и каждый раз, когда условие снова сбылось).</summary>
    EveryBoot
}

/// <summary>Где задача сейчас.</summary>
public enum DeferredStatus
{
    /// <summary>Ждёт срока.</summary>
    Pending,

    /// <summary>Выполняется: отметка ставится до запуска, чтобы обрыв не прошёл молча.</summary>
    Running,

    Done,
    Failed,
    Cancelled,

    /// <summary>Выполнение оборвалось (программу закрыли, ПК выключили): ждёт решения человека.</summary>
    Interrupted,

    /// <summary>Повторяющаяся задача три раза подряд не удалась и остановлена.</summary>
    Paused
}

/// <summary>Условие первого срабатывания.</summary>
public sealed class DeferredTrigger
{
    public DeferredTriggerKind Kind { get; set; }

    public double? UptimeHours { get; set; }

    /// <summary>Скрипт PowerShell только на чтение, который печатает True или False.</summary>
    public string? Condition { get; set; }

    public int CheckEveryMinutes { get; set; } = 5;
}

/// <summary>Правило повтора.</summary>
public sealed class DeferredRepeat
{
    public DeferredRepeatKind Kind { get; set; } = DeferredRepeatKind.Once;

    public int EveryMinutes { get; set; }

    /// <summary>Время суток «ЧЧ:мм» по местным часам — для ежедневных и еженедельных.</summary>
    public string? Time { get; set; }

    public List<DayOfWeek> Days { get; set; } = [];
}

/// <summary>Отложенная задача: что сделать, когда, сколько раз — и чем всё кончилось.</summary>
public sealed class DeferredTask
{
    public string Id { get; set; } = "";

    /// <summary>Чат, где задачу поставили: туда приходит отчёт агента или команды.</summary>
    public string? ChatId { get; set; }

    /// <summary>Чат, куда пришёл отчёт, если исходного уже не было.</summary>
    public string? ResultChatId { get; set; }

    public string Title { get; set; } = "";

    public DeferredKind Kind { get; set; }

    /// <summary>Текст напоминания или задание агенту.</summary>
    public string Text { get; set; } = "";

    /// <summary>Скрипт команды — ровно тот, что одобрил человек.</summary>
    public string? Command { get; set; }

    /// <summary>
    /// Печать (HMAC под ключом DPAPI) на то, что выполнится без человека: команда и возврат программ.
    /// Задача из чужого архива или поправленная руками печати не сходится и не выполняется.
    /// </summary>
    public string? Seal { get; set; }

    public DeferredTrigger Trigger { get; set; } = new();

    public DeferredRepeat Repeat { get; set; } = new();

    public DateTime CreatedUtc { get; set; }

    /// <summary>Ближайший срок для задач по времени; null — задача ждёт события.</summary>
    public DateTime? NextDueUtc { get; set; }

    public DeferredStatus Status { get; set; }

    public DateTime? LastFiredUtc { get; set; }

    public DateTime? LastAttemptUtc { get; set; }

    public DateTime? LastProbeUtc { get; set; }

    public int Occurrences { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary>Напоминание показано, но «Готово» ещё не нажали: при запуске программы оно встанет снова.</summary>
    public DateTime? AwaitingAckSinceUtc { get; set; }

    /// <summary>Сколько сроков повторяющегося напоминания пришло, пока висела неподтверждённая карточка.</summary>
    public int MissedWhileUnacked { get; set; }

    public DateTime? SnoozedUntilUtc { get; set; }

    /// <summary>Каким условие было на прошлой проверке: срабатывает переход «нет» → «да».</summary>
    public bool ConditionWasTrue { get; set; }

    /// <summary>Загрузка Windows, в которой задача по времени работы уже сработала.</summary>
    public DateTime? BootAtFireUtc { get; set; }

    /// <summary>Чат был в режиме «только чтение», когда задачу ставили: она так и выполнится.</summary>
    public bool ReadOnly { get; set; }

    public decimal? MaxCostUsd { get; set; }

    public decimal CostUsd { get; set; }

    public string? Output { get; set; }

    public string? Error { get; set; }

    /// <summary>Задача ещё жива: ждёт, идёт или ждёт решения.</summary>
    public bool IsActive => Status is DeferredStatus.Pending or DeferredStatus.Running or DeferredStatus.Interrupted;
}

/// <summary>Отметка «здесь поставлена отложенная задача» — полоска под ответом в ленте.</summary>
public sealed record DeferredRef(string Id, string Title, DeferredKind Kind);

/// <summary>Сообщение в чате пришло от отложенной задачи, а не от человека.</summary>
/// <param name="LateBy">Насколько срок опоздал (компьютер был выключен); null — вовремя.</param>
public sealed record DeferredMark(string TaskId, string Title, DateTime DueUtc, TimeSpan? LateBy = null);
