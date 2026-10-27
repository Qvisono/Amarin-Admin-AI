using System.Text.Json.Serialization;

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
    Almond,

    // Значения ниже 1.35 и раньше не знают: в settings.json они пишутся числом (AppThemeJsonConverter).
    // Перечисление только дописывается в конец — число в файле и есть тема.
    Onyx,
    Carbon,
    GlueGray
}

public sealed class AppSettings : IJsonOnDeserializing
{
    public bool AutoScroll { get; set; } = true;

    /// <summary>Colour scheme. <see cref="AppTheme.System"/> follows the Windows app theme.</summary>
    [JsonConverter(typeof(AppThemeJsonConverter))]
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>
    /// Язык интерфейса: <c>ru</c>, <c>en</c> или код языка, переведённого моделью. Настройка
    /// профиля, как и тема; сами файлы переводов общие для всех профилей. Неизвестный код
    /// приводится к русскому при загрузке.
    /// </summary>
    public string LanguageCode { get; set; } = "ru";

    /// <summary>Uniform UI zoom, percent. Allowed: 80, 90, 100, 110, 115, 120, 125, 150, 175, 200, 225, 250.</summary>
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
    /// Семейства заводских моделей — идентификаторами Venice (1.34.0). У других провайдеров то же
    /// семейство находит <see cref="ModelFamily"/>, поэтому имена здесь и есть ключи семейств.
    /// </summary>
    /// <remarks>
    /// С 1.37.0 сами слоты получают модели OpenRouter (<see cref="ShippedLiteModelId"/> и соседи), а
    /// эти константы остаются запасом там, где в коде нужна модель Venice: <c>LastResort</c>, цепочки
    /// замены Venice, модель из appsettings.json. Подставь сюда OpenRouter — и ключ Venice получил бы
    /// в запас модель чужого провайдера.
    /// </remarks>
    public const string FactoryLiteModelId = "claude-haiku-5-5";

    public const string FactoryHeavyModelId = "claude-sonnet-5-5";

    public const string FactoryAgentHeavyModelId = FactoryHeavyModelId;

    public const string FactoryDecisionModelId = "openrouter:typesafe/jev-1.14";

    /// <summary>
    /// Заводские модели слотов (1.37.0, ревизия 4) — у OpenRouter: так решил человек, OpenRouter
    /// приоритетнее. С ключом OpenRouter модель остаётся у него, а человеку без этого ключа лечение
    /// слотов переносит её к провайдеру его ключа (<see cref="ModelSlotDefaults"/>).
    /// </summary>
    public const string ShippedLiteModelId = "openrouter:anthropic/claude-haiku-5.5";

    public const string ShippedHeavyModelId = "openrouter:anthropic/claude-sonnet-5.5";

    /// <summary>
    /// Быстрая модель чата и агента: MiniMax Flash у самого быстрого провайдера OpenRouter (nitro).
    /// У Venice — та же модель без nitro (<see cref="ModelSlotDefaults"/>).
    /// </summary>
    public const string ShippedFastModelId = "openrouter:minimax/minimax-m3.1-flash:nitro";

    public const string ShippedTitleModelId = "openrouter:deepseek/deepseek-v4-flash-0731";

    /// <summary>Заводская модель Розы (1.37.0): Claude Haiku без размышления.</summary>
    public const string FactoryFoxModelId = ShippedLiteModelId;

    /// <summary>
    /// Заводская модель обхода (1.37.0): Grok 4.3 у OpenRouter; у Venice — своя модель обхода
    /// (<see cref="ModelSlotDefaults"/>).
    /// </summary>
    public const string FactoryBypassModelId = "openrouter:x-ai/grok-4.3";

    /// <summary>Заводская озвучка «Авто» (1.37.0): ElevenLabs v4 у OpenRouter.</summary>
    public const string FactorySpeechModelId = "openrouter:elevenlabs/eleven-v4";

    /// <summary>Голос заводской озвучки, когда человек голос не выбирал.</summary>
    public const string FactorySpeechVoice = "roger";

    /// <summary>
    /// Ревизия заводских моделей. Старый файл её не знает (0), и разовая замена
    /// (<see cref="AppSettingsStore.MigrateFactoryModels"/>) срабатывает у него один раз. Номер
    /// растёт с каждой новой заменой: 2 — 1.35.0-beta.1 (Claude, Jev 1.13), 3 — beta.2 (Jev 1.14,
    /// Aion 3.5 Mini у Розы), 4 — 1.37.0-beta.8 (модели OpenRouter, быстрая модель чата, голос);
    /// профиль, прошедший прежнюю, проходит и новую.
    /// </summary>
    public const int CurrentFactoryModels = 4;

    /// <summary>
    /// Какие заводские модели уже стоят у этого профиля. Отметка нужна, потому что заводская
    /// модель 1.33 неотличима от выбранной вручную той же: без отметки человек, сам вернувший
    /// себе прежнюю модель, получал бы подмену на каждом запуске.
    /// </summary>
    /// <remarks>
    /// Новый объект — нынешней ревизии, а прочитанный файл без этого поля — нулевой
    /// (<see cref="IJsonOnDeserializing.OnDeserializing"/>): так файл до ревизий проходит замену один раз, а
    /// настройки, собранные в коде, не сбрасываются ею при следующем чтении.
    /// </remarks>
    public int FactoryModelsRevision { get; set; } = CurrentFactoryModels;

    /// <summary>
    /// Модель чата. <c>auto</c> — маршрутизатор выбирает между быстрым, лёгким и тяжёлым слотом.
    /// </summary>
    /// <remarks>
    /// Пустая строка — прежний сентинел «модель Venice из appsettings.json». Новые профили с
    /// 1.34.0 получают «Авто»: идентификатор Venice у человека с ключом другого провайдера не
    /// работал, а «Авто» работает у любого — его слоты подбираются по семейству.
    /// </remarks>
    public string ChatModelId { get; set; } = "";

    public ReasoningSettings ChatReasoning { get; set; } = new();

    /// <summary>
    /// Быстрая модель «Авто» (1.37.0): ответ, которому не нужно думать. Слот новый: старый файл без
    /// поля получает начальное значение свойства.
    /// </summary>
    public string FastModelId { get; set; } = ShippedFastModelId;

    public ReasoningSettings FastReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "medium"
    };

    public string LiteModelId { get; set; } = ShippedLiteModelId;

    public ReasoningSettings LiteReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "medium"
    };

    public string HeavyModelId { get; set; } = ShippedHeavyModelId;

    public ReasoningSettings HeavyReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "medium"
    };

    public string RouterModelId { get; set; } = FactoryDecisionModelId;

    /// <summary>
    /// Маршрутизатор отвечает одним словом, и размышление перед ним только оплачивается.
    /// Хуже того: на списке мелких подзадач рассуждающая модель уговаривала себя на «heavy» —
    /// пунктов же много, — и человек платил флагману за сложение.
    /// </summary>
    public ReasoningSettings RouterReasoning { get; set; } = new()
    {
        DisableThinking = true
    };

    public string TitleModelId { get; set; } = ShippedTitleModelId;

    public ReasoningSettings TitleReasoning { get; set; } = new() { DisableThinking = true };

    /// <summary>
    /// Быстрая дешёвая модель Venice — запас заголовков, сводок и Розы, когда слот пуст.
    /// </summary>
    public const string DefaultFastModelId = "deepseek-v4-flash-0731";

    /// <summary>
    /// Быстрый уровень агента: та же быстрая модель, что у чата (1.37.0).
    /// </summary>
    public string AgentFastModelId { get; set; } = ShippedFastModelId;

    public ReasoningSettings AgentFastReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "medium"
    };

    public string AgentLiteModelId { get; set; } = ShippedLiteModelId;

    public ReasoningSettings AgentLiteReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "medium"
    };

    public string AgentHeavyModelId { get; set; } = ShippedHeavyModelId;

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
    /// Decisions возвращает оценку без отдельного текстового размышления.
    /// </summary>
    public ReasoningSettings SynGuardReasoning { get; set; } = new()
    {
        DisableThinking = true
    };

    /// <summary>
    /// Модель скрытых сводок переписки.
    /// </summary>
    /// <remarks>
    /// До 1.23.0 сводки молча брали слот быстрого агента, и отдельно их было не настроить:
    /// поменяв модель агенту, человек менял её и сводкам, не зная об этом. При первом чтении
    /// старых настроек сюда переносится то, что стояло у быстрого агента, — поведение
    /// у обновившегося не меняется. Пустое значение здесь — признак старого файла, поэтому
    /// заводская модель сводки ставится не им, а <see cref="CreateDefault"/>.
    /// </remarks>
    public string SummaryModelId { get; set; } = "";

    public ReasoningSettings SummaryReasoning { get; set; } = new() { DisableThinking = true };

    /// <summary>Заводское размышление сводки: подумать немного, но не оплачивать долгое.</summary>
    internal static ReasoningSettings FactorySummaryReasoning() => new()
    {
        DisableThinking = false,
        ReasoningEffort = "low"
    };

    /// <summary>
    /// Модель лисёнка Розы (1.35.0): реплики на переписку, разговор с ним и дневник.
    /// </summary>
    /// <remarks>
    /// Заводская — <see cref="FactoryFoxModelId"/>: Роза отвечает одной-двумя фразами, а зовут
    /// его часто. В отличие от сводки пустое значение здесь ничего не переносит — слот новый, и
    /// старый файл просто получает начальное значение свойства.
    /// </remarks>
    public string FoxModelId { get; set; } = FactoryFoxModelId;

    /// <summary>
    /// Размышление Розы — выключено (1.37.0): реплика в пару слов размышления не окупает, а
    /// заводская модель (Haiku) умеет не думать вовсе.
    /// </summary>
    public ReasoningSettings FoxReasoning { get; set; } = FactoryFoxReasoning();

    internal static ReasoningSettings FactoryFoxReasoning() => new() { DisableThinking = true };

    /// <summary>
    /// Модель обхода (1.36.0): последняя ступень после отказов — ответа чата (<c>i_refuse</c>) и
    /// агента (<c>agent_refused</c>).
    /// </summary>
    /// <remarks>
    /// Слот новый, как у Розы: старый файл без поля получает начальное значение свойства, и
    /// ревизия заводских моделей ради него не поднимается — она сбросила бы выбор человека во всех
    /// остальных слотах.
    /// </remarks>
    public string BypassModelId { get; set; } = FactoryBypassModelId;

    public ReasoningSettings BypassReasoning { get; set; } = new()
    {
        DisableThinking = false,
        ReasoningEffort = "low"
    };

    /// <summary>
    /// Может ли агент управлять компьютером мышью и клавиатурой (1.36.0). Заводская — нет.
    /// </summary>
    /// <remarks>
    /// Выключено, пока человек не включит сам: снимки экрана и текст окон уходят провайдеру
    /// модели, и это его решение. Из чужого архива не приезжает (<c>DataBundleImporter</c>).
    /// </remarks>
    public bool ComputerControl { get; set; }

    /// <summary>
    /// Встроенный браузер агента на WebView2 (1.37.0). Заводская — нет.
    /// </summary>
    /// <remarks>
    /// По образцу <see cref="ComputerControl"/>: текст страниц и снимки уходят провайдеру модели,
    /// а нажимает агент на настоящих сайтах человека. Из чужого архива не приезжает ни в какой
    /// профиль (<c>DataBundleImporter</c>).
    /// </remarks>
    public bool AgentBrowser { get; set; }

    /// <summary>
    /// OpenRouter: только провайдеры, которые не хранят запросы и не учат на них модели (1.37.0).
    /// </summary>
    /// <remarks>
    /// Уходит в каждый запрос к OpenRouter полем <c>provider</c> (<c>zdr</c> и
    /// <c>data_collection: deny</c>). Настройка защиты: в существующий профиль из чужого архива
    /// не приезжает.
    /// </remarks>
    public bool OpenRouterNoRetention { get; set; }

    /// <summary>
    /// Копия журнала аудита в журнал «Приложение» Windows (1.37.0). Заводская — да.
    /// </summary>
    /// <remarks>
    /// Администратор видит действия ИИ своими средствами (<c>AuditEvents</c>), без аргументов.
    /// Выключаемая, потому что журнал «Приложение» читают все пользователи компьютера; политика
    /// организации может держать её включённой. Настройка защиты: в существующий профиль из
    /// чужого архива не приезжает.
    /// </remarks>
    public bool AuditToWindowsLog { get; set; } = true;

    /// <summary>
    /// Загрузка с любого сайта: белый список загрузок не проверяется (1.37.0). Заводская — нет.
    /// </summary>
    /// <remarks>
    /// Сам список остаётся в <see cref="DownloadAllowedDomains"/> и снова действует, как только
    /// переключатель выключат. Из чужого архива не приезжает ни в какой профиль.
    /// </remarks>
    public bool DownloadAnySite { get; set; }

    /// <summary>
    /// Временный чат живёт до закрытия программы (1.37.0). Выключено — исчезает, как только
    /// человек уходит из него в другой чат.
    /// </summary>
    public bool TemporaryChatsUntilClose { get; set; } = true;

    /// <summary>Сколько живёт разговор чата с агентом (1.37.0).</summary>
    /// <remarks>
    /// До 1.37.0 разговор кончался вместе с ответом, в котором агента запустили; теперь это
    /// <see cref="AgentMemory.Answer"/>. Заводское — пока открыта программа: так чат может
    /// написать агенту прошлого ответа, а на диск история агентов не ложится.
    /// </remarks>
    public AgentMemory AgentMemory { get; set; } = AgentMemory.Session;

    /// <summary>Лисёнок Роза (1.35.0): показывать ли, размер, шалости, что он читает.</summary>
    public FoxSettings Fox { get; set; } = new();

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

    /// <summary>
    /// Модель озвучки провайдера (1.37.0), с приставкой, как у моделей чата: Venice — голая,
    /// OpenRouter — <c>openrouter:…</c>. Пусто — синтезатор Windows на этом ПК. <c>auto</c> —
    /// <see cref="FactorySpeechModelId"/>, если к OpenRouter есть ключ, иначе синтезатор Windows.
    /// </summary>
    /// <remarks>
    /// «Авто» значением, а не пустой строкой: пустая уже значила «Windows», и прежние файлы
    /// читаются как раньше. Новые профили и ревизия 4 заводских моделей ставят «Авто».
    /// </remarks>
    public string? SpeechModel { get; set; }

    /// <summary>
    /// Голос чтения вслух (1.37.0): голос Windows или голос модели <see cref="SpeechModel"/>. Пусто —
    /// у Windows голос языка ответа, у модели её первый голос; голос, которого нет, — как пустой.
    /// </summary>
    public string? SpeechVoice { get; set; }

    /// <summary>Скорость чтения вслух: шкала Windows от −10 до 10, ноль — обычная.</summary>
    public int SpeechRate { get; set; }

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

    /// <summary>
    /// До чтения полей: если в файле нет <see cref="FactoryModelsRevision"/>, остаётся 0. Иначе
    /// пришлось бы разбирать файл второй раз, чтобы узнать, было ли поле.
    /// </summary>
    void IJsonOnDeserializing.OnDeserializing() => FactoryModelsRevision = 0;

    /// <summary>Настройки нового профиля.</summary>
    /// <remarks>
    /// Модель чата и сводки ставятся здесь, а не начальными значениями свойств: пустые значения
    /// у них — признак файла прежней версии, по которому срабатывают переносы при чтении.
    /// </remarks>
    public static AppSettings CreateDefault() => new()
    {
        DownloadAllowedDomains = [.. new DownloadOptions().AllowedDomains],
        ChatModelId = VeniceModelCatalog.AutoId,
        SummaryModelId = ShippedLiteModelId,
        SummaryReasoning = FactorySummaryReasoning(),
        SpeechModel = VeniceModelCatalog.AutoId
    };
}
