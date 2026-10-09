namespace Amarin.Core;

/// <summary>Режим доступа модели к системе.</summary>
/// <remarks>
/// Пишется в <c>settings.json</c> именем (<see cref="AppJson"/>), поэтому новые режимы старым
/// файлам не мешают: там лежит «Normal» или «AlwaysApprove», и оба по-прежнему значат то же.
/// </remarks>
public enum ApprovalMode
{
    /// <summary>Спрашивать про опасное: запись в реестр, службы, скрипты, перезапись файлов.</summary>
    Normal,

    /// <summary>Подтверждать всё автоматически, кроме вопросов SynGuard и белого списка загрузок.</summary>
    AlwaysApprove,

    /// <summary>Только чтение: любая запись отклоняется, модель узнаёт об этом из отказа.</summary>
    ReadOnly,

    /// <summary>Спрашивать про любую запись, даже ту, что в обычном режиме идёт молча.</summary>
    AskAll
}

/// <summary>
/// Порядок и длина даты там, где программа её показывает.
/// </summary>
/// <remarks>
/// Порядок членов свободен: <see cref="AppJson"/> пишет перечисления именами, а не номерами,
/// поэтому в <c>settings.json</c> значение ищется по имени. Дописывать новые можно куда угодно,
/// а переименовывать существующие нельзя — старый файл перестанет читаться.
/// </remarks>
public enum DateFormat
{
    DayMonthShort,
    MonthDayShort,
    DayMonthFull,
    MonthDayFull
}

/// <summary>
/// Скругление углов главного окна. Его рисует Windows 11, и выбор у неё ровно из трёх видов:
/// своего радиуса система не принимает.
/// </summary>
/// <remarks>
/// Пишется в <c>settings.json</c> именем, как и прочие перечисления: переименовывать нельзя.
/// </remarks>
public enum WindowCorners
{
    /// <summary>Маленькое скругление — как было до 1.27.0, пока окно числилось «инструментом».</summary>
    Small,

    /// <summary>Обычное скругление окон Windows 11.</summary>
    Round,

    /// <summary>Прямые углы.</summary>
    Square
}

/// <summary>
/// Готовая палитра. Первые три — исходные и сохраняют имена в settings.json, остальные — готовые
/// цветовые темы. Что за каждой стоит — в <see cref="ThemeCatalog"/>.
/// </summary>
/// <remarks>
/// Порядок здесь свободный: <see cref="AppJson"/> пишет перечисления строками camelCase, и тема в
/// <c>settings.json</c> находится по имени, а не по номеру. Вид сетки тем в настройках задаёт
/// <see cref="ThemeCatalog.Presets"/>, а не этот список.
/// </remarks>
public enum AppTheme
{
    System,
    Light,
    Dark,
    Obsidian,
    Midnight,
    Nord,
    Cobalt,
    Slate,
    Amethyst,
    Rose,
    Crimson,
    Ember,
    Emerald,
    Frost,
    Sakura,
    Mint,
    Sepia,
    Silver,
    Graphite,
    Paper,
    Ocean,
    Forest,
    Plum,
    Sand,
    Steel,
    Terminal,
    Rust,
    Neon,
    Ochre,
    Quartz,
    Ink,
    Contrast,
    Matte,
    MatteLight,
    EdgeBlue,
    EdgeLime,
    EdgeAmber,
    EdgeMagenta,
    Garnet,
    Ruby,
    Coral,
    Cherry,
    Glacier,
    Iceberg,
    Zircon,
    Aurora,
    Cirrus,
    Opal,
    Alpine,
    Tundra,
    Cream,
    Linen,
    Vellum,
    Almond
}

public sealed class AppSettings
{
    public bool AutoScroll { get; set; } = true;

    /// <summary>Colour scheme. <see cref="AppTheme.System"/> follows the Windows app theme.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>
    /// Язык интерфейса: <c>ru</c>, <c>en</c> или код языка, переведённого моделью. Настройка
    /// профиля, как и тема; сами файлы переводов общие для всех профилей. Неизвестный код
    /// приводится к русскому при загрузке.
    /// </summary>
    public string LanguageCode { get; set; } = "ru";

    /// <summary>Uniform UI zoom, percent. Allowed: 80, 90, 100, 110, 125, 150, 175, 200, 225, 250.</summary>
    public int UiScalePercent { get; set; } = 100;

    /// <summary>
    /// Как показывать дату: в подсказке над временем ответа, в журнале и в отчёте об импорте.
    /// </summary>
    public DateFormat DateFormat { get; set; } = DateFormat.DayMonthShort;

    /// <summary>
    /// Углы главного окна. Отдельно от <see cref="Appearance"/>: форма окна не должна зависеть
    /// от главного выключателя оформления. Заводское — маленькое скругление, которое люди
    /// видели до 1.27.0.
    /// </summary>
    public WindowCorners WindowCorners { get; set; } = WindowCorners.Small;

    /// <summary>
    /// Фон, стекло и раскладка поверх <see cref="Theme"/>. Значения по умолчанию повторяют вид
    /// темы как есть, так что ничего не меняется, пока человек это не включит.
    /// </summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>
    /// Уровни агента, у которых сначала план (lite, fast, heavy). Нет поля — только heavy: у
    /// тяжёлого агента самые длинные цепочки изменений, и их стоит видеть заранее.
    /// </summary>
    public List<string>? PlanFirstTiers { get; set; }

    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Normal;

    /// <summary>
    /// Инструменты, которые человек выключил. Модели они не показываются вовсе, а вызов по имени
    /// всё равно отклоняется. Null и пустой список — выключенных нет (так читаются старые файлы).
    /// </summary>
    public List<string>? DisabledTools { get; set; }

    /// <summary>
    /// Хранить переписки, их опись и новые строки журнала аудита зашифрованными DPAPI на
    /// текущего пользователя Windows (<see cref="AtRestCipher"/>). Выключено по умолчанию:
    /// так читаются и пишутся файлы прежних версий.
    /// </summary>
    public bool EncryptChats { get; set; }

    /// <summary>
    /// Через сколько минут без ввода в окне закрывать его экраном блокировки; 0 — никогда.
    /// Действует только у профиля с паролем: без пароля снимать блокировку нечем.
    /// </summary>
    public int AutoLockMinutes { get; set; }

    /// <summary>Показывать карточку в правом нижнем углу, когда ход кончился, а окно не в фокусе.</summary>
    public bool NotifyOnResponseComplete { get; set; } = true;

    /// <summary>Короткий системный звук с карточкой. Без карточки не учитывается.</summary>
    public bool NotifySound { get; set; } = true;

    /// <summary>Мелодия напоминаний и итогов отложенных задач (1.33.0).</summary>
    public bool DeferredSound { get; set; } = true;

    /// <summary>
    /// Кнопки «поделиться» и «экспорт» под сообщениями. Обе отдают всю переписку без шифрования,
    /// поэтому их можно выключить совсем.
    /// </summary>
    public bool ChatSharingEnabled { get; set; } = true;

    /// <summary>
    /// Домены, с которых <c>download_file</c> может скачивать, вместе с поддоменами. <c>null</c> —
    /// «ещё не засеяно»: при первом чтении хранилище заполнит список заводским. Пустой список —
    /// осознанный выбор, он запрещает любые загрузки.
    /// </summary>
    public List<string>? DownloadAllowedDomains { get; set; }

    /// <summary>
    /// Когда в журнал трат перенесли цены из уже сохранённых переписок.
    /// </summary>
    /// <remarks>
    /// Отметка профиля, а не ключа. Раньше она стояла у ключа — и каждый заведённый после
    /// неё ключ переносил себе ту же историю чатов заново: график по новому ключу повторял
    /// траты старого рубль в рубль. Переписки принадлежат профилю, а какой ключ за них платил,
    /// в них не записано, поэтому перенос имеет смысл ровно один раз на профиль.
    /// </remarks>
    public DateTime? SpendBackfilledAt { get; set; }

    /// <summary>
    /// Каким режимом показан график трат: по всем ключам или по одному выбранному.
    /// </summary>
    /// <remarks>
    /// Платит теперь не один ключ: у каждого слота модели свой, и сумма по одному ключу больше
    /// не отвечает на вопрос «сколько программа стоит». Поэтому режимов два, и выбранный
    /// переживает перезапуск — человек смотрит на график не один раз.
    /// </remarks>
    public bool SpendScopeAllKeys { get; set; } = true;

    /// <summary>Лимиты трат (E1). Заводское — всё выключено.</summary>
    public SpendLimits SpendLimits { get; set; } = new();

    /// <summary>Пороги остатка (E4). Заводские — прежние константы плашки: $1 и $0.25.</summary>
    public BalanceThresholds BalanceThresholds { get; set; } = new();

    /// <summary>Автоматические резервные копии (F1). Заводское — выключены.</summary>
    public BackupSettings Backup { get; set; } = new();

    /// <summary>Хранение старых чатов (F3). Заводское — ничего не трогать.</summary>
    public ChatRetention Retention { get; set; } = new();

    /// <summary>Интеграция с Windows (G): трей, уведомления, сочетания, автозапуск, Проводник.</summary>
    public WindowsIntegrationSettings Windows { get; set; } = new();

    /// <summary>
    /// Ключ, назначенный слоту: имя слота — идентификатор ключа из <c>keys.json</c>.
    /// </summary>
    /// <remarks>
    /// Отдельным словарём, а не девятью полями рядом с моделями: слотов девять, и девять
    /// парных полей пришлось бы заводить, читать и переносить по одному. Слот, которого здесь
    /// нет, берёт ключ по умолчанию для провайдера своей модели — поэтому
    /// <c>settings.json</c> прежних версий читается без миграции, а удаление ключа не ломает
    /// слот, а лишь возвращает его к выбору по умолчанию.
    /// </remarks>
    public Dictionary<string, string>? ModelKeys { get; set; }

    /// <summary>
    /// Выбранные модели других провайдеров: имя провайдера — имя слота — идентификатор модели.
    /// </summary>
    /// <remarks>
    /// Наследство версий, где активный ключ задавал провайдера всем девяти слотам разом и при
    /// его смене выбор приходилось прятать сюда. С 1.23.0 у каждого слота свой провайдер,
    /// прятать нечего, и поле больше не читается и не пишется. Оставлено, чтобы
    /// <c>settings.json</c> прежних версий открывался и сохранялся без потерь.
    /// </remarks>
    public Dictionary<string, Dictionary<string, string>>? ModelSlotsByProvider { get; set; }

    /// <summary>
    /// Empty means use Venice:Model from the shipped appsettings.json.
    /// </summary>
    /// <remarks>
    /// Пустая строка — сентинел только для Venice: в appsettings.json лежит его идентификатор
    /// модели. У остальных провайдеров слот заполняется явно, см. <see cref="ModelSlotDefaults"/>.
    /// </remarks>
    public string ChatModelId { get; set; } = "";

    public ReasoningSettings ChatReasoning { get; set; } = new();

    public string LiteModelId { get; set; } = "openai-gpt-56-luna";

    public ReasoningSettings LiteReasoning { get; set; } = new() { DisableThinking = false };

    public string HeavyModelId { get; set; } = "grok-4-6";

    public ReasoningSettings HeavyReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "medium"
    };

    public string RouterModelId { get; set; } = "openai-gpt-56-luna";

    /// <summary>
    /// Маршрутизатор отвечает одним словом, и размышление перед ним только оплачивается.
    /// Хуже того: на списке мелких подзадач рассуждающая модель уговаривала себя на «heavy» —
    /// пунктов же много, — и человек платил флагману за сложение.
    /// </summary>
    public ReasoningSettings RouterReasoning { get; set; } = new()
    {
        DisableThinking = true
    };

    public string TitleModelId { get; set; } = "openai-gpt-56-luna";

    public ReasoningSettings TitleReasoning { get; set; } = new();

    /// <summary>
    /// Модель быстрого уровня по умолчанию. Ею же подписываются сводки чатов, когда слот пуст,
    /// — идентификатор один на оба места.
    /// </summary>
    public const string DefaultFastModelId = "deepseek-v4-flash-0731-fast";

    /// <summary>
    /// Дешёвый уровень — для работы, которой не нужен флагман, или когда человек просит быстрее.
    /// Размышление по умолчанию выключено: смысл слота — ответ раньше, а размышление съело бы
    /// ровно то, что он экономит.
    /// </summary>
    public string AgentFastModelId { get; set; } = DefaultFastModelId;

    public ReasoningSettings AgentFastReasoning { get; set; } = new() { DisableThinking = true };

    public string AgentLiteModelId { get; set; } = "openai-gpt-56-luna";

    public ReasoningSettings AgentLiteReasoning { get; set; } = new() { DisableThinking = false };

    public string AgentHeavyModelId { get; set; } = "grok-4-6";

    public ReasoningSettings AgentHeavyReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "high"
    };

    /// <summary>
    /// Проверять ли SynGuard то, что агент собирается запустить.
    /// </summary>
    /// <remarks>
    /// Включено с самого начала: в этом её смысл, а стоит она одного дешёвого запроса на раунд
    /// инструментов. Выключение — осознанный выбор человека, и переспрашивать о нём не нужно.
    /// </remarks>
    public bool SynGuardEnabled { get; set; } = true;

    /// <summary>Модель защитника. Пусто — <see cref="SynGuard.FallbackModelId"/>.</summary>
    public string SynGuardModelId { get; set; } = SynGuard.FallbackModelId;

    /// <summary>
    /// Защитник отвечает одним словом на вызов, но решает при этом, вредонос перед ним или
    /// работа: дешёвая модель без размышления судила по словам в команде — «планировщик»,
    /// «Hidden», «Bypass», — и останавливала то, о чём человек сам и просил. Отсюда «low»:
    /// подумать перед вердиктом, но не оплачивать долгое размышление на каждом раунде.
    /// </summary>
    public ReasoningSettings SynGuardReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "low"
    };

    /// <summary>
    /// Модель скрытых сводок переписки.
    /// </summary>
    /// <remarks>
    /// До 1.23.0 сводки молча брали слот быстрого агента, и отдельно их было не настроить:
    /// поменяв модель агенту, человек менял её и сводкам, не зная об этом. При первом чтении
    /// старых настроек сюда переносится то, что стояло у быстрого агента, — поведение
    /// у обновившегося не меняется.
    /// </remarks>
    public string SummaryModelId { get; set; } = "";

    public ReasoningSettings SummaryReasoning { get; set; } = new() { DisableThinking = true };

    /// <summary>
    /// Через кого искать в интернете. <c>null</c> — там же, где идёт разговор.
    /// </summary>
    /// <remarks>
    /// Настройка инструмента, а не модели: человек выбирает провайдера и ключ, а модель под них
    /// программа подбирает сама. Отдельной «ручки поиска» нет ни у Venice, ни у OpenRouter —
    /// поиск едет надстройкой на обычном запросе (<c>venice_parameters</c> и плагин
    /// соответственно), — но какая именно модель его везёт, человека не касается: он платит
    /// за интернет, а не за выбор модели.
    /// </remarks>
    public LlmProvider? WebSearchProvider { get; set; }

    /// <summary>Ключ, которым платится поиск. Пусто — ключ провайдера по умолчанию.</summary>
    public string? WebSearchKeyId { get; set; }

    /// <summary>
    /// Движок поиска у OpenRouter и его режим. Пусто — решает сам OpenRouter.
    /// </summary>
    /// <remarks>
    /// Две строки, а не одна составная: на провод они уходят двумя полями, и склеивать их
    /// ради настроек значило бы разбирать обратно перед каждым запросом. У Venice движок один,
    /// и эти поля к нему не относятся.
    /// </remarks>
    public string? WebSearchEngine { get; set; }

    public string? WebSearchEngineMode { get; set; }

    /// <summary>
    /// Переназначенные сочетания клавиш: имя действия из <see cref="HotkeyMap"/> — запись
    /// сочетания вроде <c>Ctrl+F</c>.
    /// </summary>
    /// <remarks>
    /// Словарём, а не полем на действие: действий будет больше, и на каждое заводить своё поле
    /// с миграцией незачем. Отсутствующее действие берёт заводское сочетание — поэтому
    /// <c>settings.json</c> прежних версий читается как есть, а сброс к заводскому просто
    /// убирает запись.
    /// </remarks>
    public Dictionary<string, string>? Hotkeys { get; set; }

    /// <summary>Порядок чатов в боковой панели (D5). Заводское — по последнему изменению, как было.</summary>
    public ChatSort ChatSort { get; set; } = ChatSort.Updated;

    /// <summary>
    /// Ширина боковой панели, которую человек выставил ручкой у края (D5). Null — заводские 184.
    /// Читается через <see cref="SidebarWidths.Clamp"/>: руками поправленный файл не должен
    /// ни спрятать список, ни отнять у ленты всё окно.
    /// </summary>
    public double? SidebarWidth { get; set; }

    /// <summary>Чем распознавать голос (D14). Заводское — «Авто»: на ПК, если есть распознаватель языка.</summary>
    public VoiceEngine VoiceEngine { get; set; } = VoiceEngine.Auto;

    /// <summary>
    /// Модель распознавания в облаке (D14): как у моделей чата, приставка задаёт провайдера
    /// (<c>openrouter:…</c>), без неё — Venice. Пусто — облако не настроено.
    /// </summary>
    public string? VoiceModel { get; set; }

    /// <summary>Язык речи (D14): код вроде «ru» или «en-US». Пусто — язык интерфейса.</summary>
    public string? VoiceLanguage { get; set; }

    /// <summary>Следовать режиму высокой контрастности Windows (I2). Заводское — да.</summary>
    public bool FollowHighContrast { get; set; } = true;

    /// <summary>Размер текста сообщений в ленте (I3). Null — заводские 13,5.</summary>
    public double? ChatFontSize { get; set; }

    /// <summary>Моноширинный шрифт кода (I3): consolas, cascadia, courier. Null — Consolas, как было.</summary>
    public string? CodeFont { get; set; }

    /// <summary>Номера строк у блоков кода в ленте (D13). Заводское — без них, как было.</summary>
    public bool CodeLineNumbers { get; set; }

    /// <summary>Выгрузка чата (D6) кладёт раунды инструментов: вызовы, аргументы и итоги.</summary>
    public bool ExportIncludeTools { get; set; }

    /// <summary>Выгрузка чата (D6) показывает цену каждого ответа и итог.</summary>
    public bool ExportIncludeCosts { get; set; }

    /// <summary>Боковая панель свёрнута в полоску — запоминается между запусками (D5).</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>Необязательный характер. Пусто — чат работает только на техпромпте.</summary>
    public string MainPrompt { get; set; } = "";

    public string TechAiPrompt { get; set; } = "";

    public string TechAgentPrompt { get; set; } = "";

    public SessionMode SessionMode { get; set; } = SessionMode.Continuous;

    /// <summary>
    /// Обновляться самостоятельно: спрашивать GitHub о новой версии при запуске и дальше раз в
    /// <see cref="UpdateSchedule.Interval"/>, скачивать найденную в фоне и ставить её, когда
    /// человек закрывает программу.
    /// </summary>
    /// <remarks>
    /// Снятая галка значит «программа не делает ничего сама»: ни проверок, ни загрузки, ни
    /// подмены файла — остаётся только кнопка на странице настроек. Имя свойства осталось
    /// прежним, хотя смысл шире проверки: переименование увело бы <c>settings.json</c> прежних
    /// версий к значению по умолчанию, а оно противоположно тому, что человек выбрал.
    /// </remarks>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Когда автопроверка последний раз ходила в сеть. UTC; <c>null</c> — ещё ни разу.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// Бета-канал: предлагать и предварительные выпуски. Заводское — нет: бета может сломаться,
    /// и получать её должен только тот, кто сам попросил.
    /// </summary>
    public bool BetaChannel { get; set; }

    /// <summary>
    /// Версия, от которой человек вернулся к прошлой. Её автообновление больше само не ставит —
    /// только кнопкой; более новую — как обычно.
    /// </summary>
    public string? DeclinedUpdate { get; set; }

    /// <summary>
    /// Последняя версия, чьи заметки «Что нового» человек уже видел. Пусто — первый запуск с
    /// этой настройкой: окно не показывается, а версия запоминается, иначе после установки
    /// с нуля человека встречали бы заметки к тому, что он и так только что скачал.
    /// </summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Открывать окно того же размера, каким его закрыли в прошлый раз.</summary>
    public bool RememberWindowSize { get; set; } = true;

    /// <summary>
    /// Размер окна в физических пикселях экрана; <c>0</c> — ещё не сохранён.
    /// </summary>
    /// <remarks>
    /// Пиксели, а не <c>Width</c> и <c>Height</c> окна. Масштаб интерфейса здесь сделан
    /// поддельным DPI: при 150 % окно шириной 1140 единиц занимает 1710 настоящих пикселей.
    /// Сохранив логическую величину, программа меняла бы размер окна от одной только смены
    /// масштаба или переезда на монитор с другим DPI.
    /// </remarks>
    public int WindowPixelWidth { get; set; }

    /// <inheritdoc cref="WindowPixelWidth"/>
    public int WindowPixelHeight { get; set; }

    /// <summary>Окно было закрыто развёрнутым.</summary>
    public bool WindowMaximized { get; set; }

    public static AppSettings CreateDefault() => new()
    {
        DownloadAllowedDomains = [.. new DownloadOptions().AllowedDomains]
    };
}
