using System.Text.Json.Serialization;
using Amarin.Tools;

namespace Amarin.Core;

public enum AssistantStatus
{
    Streaming,
    Complete,
    Cancelled,
    Error
}

public enum ToolCallStatus
{
    Pending,
    Running,
    Done,
    Failed
}

public enum AgentRunStatus
{
    Running,
    Complete,
    Cancelled,
    Failed
}

public sealed class ChatSession
{
    /// <summary>
    /// Замок на перестановку списков вне хода — варианты ответа меняют местами хвосты
    /// <see cref="Messages"/> и <see cref="ApiMessages"/>, а сериализация в фоне не должна
    /// застать их посередине. В файл не пишется: свойство не публичное.
    /// </summary>
    internal Lock Gate { get; } = new();

    public string Id { get; set; } = "";

    public string Title { get; set; } = "Новый чат";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string SelectedModelId { get; set; } = "";

    /// <summary>
    /// Ключ, которым платит эта переписка. Пусто — ключ по умолчанию для провайдера модели.
    /// </summary>
    /// <remarks>
    /// Рядом с моделью и по той же причине: выбор человека принадлежит переписке, а не
    /// программе. Два чата могут идти на разных ключах, и переключение в одном не должно
    /// перекладывать деньги другого. Пустое значение в старых файлах читается как «по
    /// умолчанию» — переписки прежних версий открываются без миграции.
    /// </remarks>
    public string? SelectedKeyId { get; set; }

    /// <summary>True — чат не просит модель размышлять. По умолчанию — как раньше.</summary>
    public bool DisableThinking { get; set; } = true;

    public string? ReasoningEffort { get; set; }

    [JsonIgnore]
    public ReasoningChoice Reasoning => new(DisableThinking, ReasoningEffort);

    public List<ChatDisplayMessage> Messages { get; set; } = [];

    public List<ChatMessage> ApiMessages { get; set; } = [];

    /// <summary>Чат записан прогоном задачи по расписанию — её идентификатор; null — обычный чат.</summary>
    public string? ScheduleJobId { get; set; }

    /// <summary>Удалённая машина, на которой исполняются команды этого чата (C10); null — этот ПК.</summary>
    public string? TargetMachineId { get; set; }

    /// <summary>Свой промпт и набор инструкций этого чата (D11); null — общие.</summary>
    public ChatProfile? Profile { get; set; }

    /// <summary>Чат в режиме «только чтение» (команда /readonly, D9): его ходы ничего не меняют.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Сжатие контекста (D10): сколько первых сообщений истории модели заменены сводкой
    /// <see cref="CompactSummary"/>. Ноль — не сжимали. Сами сообщения остаются на месте и на
    /// экране — меняется только то, что уходит модели (<see cref="ContextCompaction"/>).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int CompactedThrough { get; set; }

    /// <summary>Сводка сжатой части переписки; null — не сжимали.</summary>
    public string? CompactSummary { get; set; }

    /// <summary>
    /// Отпечаток сжатой части истории. Правка, удаление или другой вариант ответа в этой части
    /// меняют отпечаток — и сводка перестаёт применяться: лучше отправить всё, чем пересказ
    /// разговора, которого на экране уже нет.
    /// </summary>
    public string? CompactFingerprint { get; set; }

    /// <summary>Последнее сообщение ленты, которое сводка покрывает, — под ним стоит отметка.</summary>
    public string? CompactedAfterMessageId { get; set; }

    /// <summary>
    /// Контекст последнего запроса по счёту провайдера. Хранится вместе с
    /// <see cref="LastPromptTokensApiIndex"/>, чтобы кольцо прибавляло прикидку за дописанное после
    /// него, а не показывало число, устаревшее с первым же набранным словом. Ноль — у чатов,
    /// сохранённых до появления поля, и у чатов без единого ответа.
    /// </summary>
    public int LastPromptTokens { get; set; }

    /// <summary>Length of <see cref="ApiMessages"/> when <see cref="LastPromptTokens"/> was measured.</summary>
    public int LastPromptTokensApiIndex { get; set; }

    /// <summary>
    /// Пересказ переписки в одну-две фразы: по нему ищут чат, когда помнят, о чём он был,
    /// а не как назывался. Пишется моделью «быстрая» после каждого ответа.
    /// </summary>
    /// <remarks>
    /// Источник истины — здесь; в <see cref="ChatIndexEntry.Summary"/> лежит копия, чтобы поиск
    /// по всем чатам не читал каждый файл. У переписок, сохранённых до появления поля, пусто:
    /// сводка соберётся при следующем ответе.
    /// </remarks>
    public string? Summary { get; set; }

    /// <summary>
    /// Сводку пора собрать заново по показанной ветке, а не дописывать: сменился вариант ответа.
    /// </summary>
    /// <remarks>
    /// Сводка дописывается по одному обмену за раз, и после перегенерации или переключения она
    /// пересказывала бы уже спрятанный ответ. Пересобирается она при следующем ответе, а не
    /// в миг переключения: иначе каждое листание вариантов стоило бы запроса к модели.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool SummaryStale { get; set; }

    /// <summary>Во что обошёлся придуманный заголовок этой переписки.</summary>
    /// <remarks>
    /// Живёт на чате, а не только на сообщении: заголовок считается в отрыве от хода, и его
    /// цена может стать известна раньше, чем появился первый ответ, — тогда она ждёт здесь.
    /// Остаётся и после того, как её записали в сообщение: пересозданный первый ответ подберёт
    /// её заново.
    /// </remarks>
    public VeniceCost? TitleCost { get; set; }
}

public sealed class ChatDisplayMessage
{
    public string Role { get; set; } = "user";

    public string Id { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    public string Text { get; set; } = "";

    /// <summary>
    /// Картинки, приложенные к сообщению. В памяти — base64, на диске <see cref="ChatStore"/>
    /// выносит их в файлы вложений; экспорт и «Поделиться» несут их внутри чата. У ответов
    /// модели пусто.
    /// </summary>
    public List<ImageAttachment> Images { get; set; } = [];

    /// <summary>
    /// Документы, прикреплённые к сообщению: PDF, таблицы, исходники. Хранятся так же, как
    /// картинки, — base64 внутри чата. У старых переписок поля в JSON нет, и список выходит
    /// пустым: чат сериализуется рефлексией, миграция не нужна.
    /// </summary>
    public List<FileAttachment> Files { get; set; } = [];

    /// <summary>
    /// Фрагменты прежних ответов, на которые человек отвечает этим сообщением.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Text"/>, а не вписаны в него: см. <see cref="MessageQuote"/>. У
    /// переписок, сохранённых раньше, поля в JSON нет, и список выходит пустым — чат
    /// сериализуется рефлексией, миграция не нужна.
    /// </remarks>
    public List<MessageQuote> Quotes { get; set; } = [];

    public string? RequestedModelId { get; set; }

    public string? ResolvedModelId { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Сколько модель думала до первого слова ответа. Показывается рядом с длительностью, когда
    /// это заметно; ноль у моделей, которые пишут сразу.
    /// </summary>
    public TimeSpan ThinkingDuration { get; set; }

    public VeniceCost? Cost { get; set; }

    /// <summary>
    /// Цена разговора с самой моделью — без инструментов и вложенных агентов. <see cref="Cost"/> —
    /// это она плюс цены вызовов инструментов и агентов: ровно та разбивка, что под курсором на цене.
    /// </summary>
    public VeniceCost? ModelCost { get; set; }

    /// <summary>
    /// Во что обошёлся сам выбор модели, когда ход шёл на «Авто». По наличию этого поля строка
    /// «Маршрутизатор» и появляется в разбивке — отдельного признака «ход маршрутизировался» нет.
    /// </summary>
    /// <remarks>
    /// Маршрутизатор платит тем же клиентом, что и разговор, и до этого поля его деньги молча
    /// сидели внутри строки «Модель»: «Авто» выглядела дороже, чем она есть. У переписок,
    /// сохранённых раньше, поле пустое — чат сериализуется рефлексией, миграция не нужна.
    /// </remarks>
    public VeniceCost? RouterCost { get; set; }

    /// <summary>
    /// Цена придуманного заголовка чата, записанная на это сообщение. Заполняется только у
    /// первого ответа: заголовок сочиняется один раз, по первому вопросу.
    /// </summary>
    /// <remarks>
    /// В отличие от маршрутизатора эта трата в счёт хода не входит — у генератора свой клиент,
    /// поэтому её прибавляют, а не вычитают.
    /// </remarks>
    public VeniceCost? TitleCost { get; set; }

    /// <summary>
    /// Цена сводки, дописанной после этого ответа.
    /// </summary>
    /// <remarks>
    /// Сводка считается уже после того, как счёт сообщения закрыт, поэтому отдельного места
    /// ожидания, как у заголовка, ей не нужно: цена всегда попадает ровно на тот ответ, который
    /// её и вызвал. Клиент у неё свой, ход этих денег не видит — значит их прибавляют.
    /// </remarks>
    public VeniceCost? SummaryCost { get; set; }

    /// <summary>
    /// Во что обошлась проверка SynGuard за весь ход: по одному запросу на раунд инструментов
    /// каждого агента, сложенные вместе.
    /// </summary>
    /// <remarks>
    /// Как и заголовок, защитник платит своим клиентом, и ход его денег не видит — значит их
    /// прибавляют к итогу, а не вычитают. Одно поле на сообщение, а не строка на каждую
    /// проверку: человеку важно, сколько стоила защита, а не сколько раз она срабатывала.
    /// </remarks>
    public VeniceCost? GuardCost { get; set; }

    /// <summary>
    /// Цена сжатия контекста (D10), приписанная ответу, после которого сжимали. Как у сводки —
    /// своим клиентом, поэтому прибавляется к итогу, а в разбивке стоит своей строкой.
    /// </summary>
    public VeniceCost? CompactCost { get; set; }

    public AssistantStatus Status { get; set; }

    public List<ToolRound> ToolRounds { get; set; } = [];

    /// <summary>
    /// Другие варианты продолжения переписки с этого места — по порядку, без показанного.
    /// </summary>
    /// <remarks>
    /// Держит их только «якорь» — первое сообщение показанного варианта: ответ при
    /// перегенерации, вопрос при правке. Сам показанный вариант лежит в плоских
    /// <see cref="ChatSession.Messages"/> и <see cref="ChatSession.ApiMessages"/>, поэтому
    /// сборка истории, отрисовка, сводка и заголовок работают с ним как прежде, а старые файлы
    /// читаются без миграции: у них этого поля просто нет. Подробности — в
    /// <see cref="ChatBranches"/>.
    /// </remarks>
    public List<ChatBranch>? Variants { get; set; }

    /// <summary>Место показанного варианта среди всех: 0 — первый.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int VariantIndex { get; set; }

    /// <summary>Сколько всего вариантов продолжения с этого места, вместе с показанным.</summary>
    [JsonIgnore]
    public int VariantCount => (Variants?.Count ?? 0) + 1;
}

/// <summary>Спрятанный вариант продолжения: сообщения ленты и история модели с этого места.</summary>
public sealed class ChatBranch
{
    public List<ChatDisplayMessage> Messages { get; set; } = [];

    public List<ChatMessage> ApiMessages { get; set; } = [];

    /// <summary>Замер контекста, если он был сделан на этом варианте.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LastPromptTokens { get; set; }

    /// <summary>Где был замер — от начала этого варианта в истории модели.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int LastPromptTokensApiOffset { get; set; }
}

public sealed class ToolRound
{
    public string InfoLine { get; set; } = "";

    /// <summary>
    /// Что модель сказала перед этим раундом инструментов — её собственная реплика, а не служебная
    /// строка движка.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="InfoLine"/>: её движок перезаписывает «инструменты завершены» в конце
    /// раунда, и реплика там свой раунд не пережила бы. Пусто у чатов, сохранённых до появления поля.
    /// </remarks>
    public string ModelNote { get; set; } = "";

    /// <summary>
    /// Что случилось с сообщением, дописанным человеком, пока шёл этот раунд: замечено, агент
    /// остановлен, агент пересажен на другую модель.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="InfoLine"/> по той же причине, что и <see cref="ModelNote"/>:
    /// движок переписывает InfoLine в конце раунда, и запись об услышанной просьбе исчезла бы
    /// вместе с ней. Пусто у чатов, сохранённых до появления поля.
    /// </remarks>
    public string FollowUpNote { get; set; } = "";

    /// <summary>
    /// Чем кончилась проверка SynGuard этого раунда. <c>null</c> — проверки не было (защита
    /// выключена или в раунде одно чтение), и у переписок прежних версий.
    /// </summary>
    /// <remarks>
    /// Перечисление, а не готовая строка, в отличие от <see cref="FollowUpNote"/>: пометку
    /// рисует интерфейс на текущем языке, и после смены языка она не остаётся на прежнем.
    /// </remarks>
    public SynGuardOutcome? GuardOutcome { get; set; }

    public List<ToolCallRecord> Calls { get; set; } = [];
}

public sealed class ToolCallRecord
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string ArgumentsJson { get; set; } = "";

    public string ResultPreview { get; set; } = "";

    /// <summary>
    /// Полный вывод инструмента (с потолком) — его журнал открывает по щелчку на строке. У старых
    /// вызовов пусто, и журнал берёт краткий итог.
    /// </summary>
    public string ResultText { get; set; } = "";

    public bool Success { get; set; }

    public ToolCallStatus Status { get; set; }

    /// <summary>
    /// Когда инструмент на самом деле начал работу. У старых вызовов — значение по умолчанию, и
    /// журнал берёт время сообщения, а не сортирует все старые действия в нулевой год.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>How long it ran. Zero when unknown, same as above.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Картинки от инструмента — нарисованная, снимок экрана. Хранятся так же, как вложения
    /// человека: base64 в памяти, файлы вложений на диске.
    /// </summary>
    public List<ImageAttachment> Images { get; set; } = [];

    /// <summary>
    /// Файлы, которые этот вызов положил на диск. Из них собирается полоса карточек под ответом.
    /// </summary>
    /// <remarks>
    /// Здесь только путь и размер: содержимое файла в переписку не уезжает — см.
    /// <see cref="Tools.SavedFile"/>. У переписок, сохранённых раньше, список пуст; чат
    /// сериализуется рефлексией, миграция не нужна.
    /// </remarks>
    public List<Tools.SavedFile> SavedFiles { get; set; } = [];

    /// <summary>
    /// Инструкция пользователя, которую открыл этот вызов. По ней под ответом стоит отметка
    /// «по инструкции». У прочих вызовов и у переписок прежних версий — null.
    /// </summary>
    public Tools.InstructionRef? Instruction { get; set; }

    /// <summary>
    /// Отложенная задача, которую поставил этот вызов. По ней под ответом стоит полоска «⏰ задача»
    /// с её нынешним состоянием. У прочих вызовов и у переписок прежних версий — null.
    /// </summary>
    public DeferredRef? Deferred { get; set; }

    /// <summary>
    /// Сколько этот вызов добавил к счёту хода. Только для показа: сумма уже в итоге сообщения, и
    /// повторное сложение посчитало бы дважды. Есть у платных инструментов (рисование, чтение
    /// страниц), у остальных null.
    /// </summary>
    public VeniceCost? Cost { get; set; }

    /// <summary>
    /// Вывод инструмента длиннее того, что уходит модели (<see cref="ChatToolPreview.FormatForApi"/>):
    /// модель видела его обрезанным (D10). У вызовов прежних версий — false: этого не знали.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool TruncatedForModel { get; set; }

    public AgentRunRecord? NestedAgent { get; set; }
}

public sealed class AgentRunRecord
{
    public int SlotIndex { get; set; }

    public string ModelId { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public AgentRunStatus Status { get; set; }

    public List<ToolRound> ToolRounds { get; set; } = [];

    public string ReportText { get; set; } = "";

    public VeniceCost? Cost { get; set; }

    /// <summary>План, который агент предлагал (C1), — чтобы лента показала его и после хода.</summary>
    public AgentPlan? Plan { get; set; }

    /// <summary>Что решил человек о последнем плане; null — ещё не решил.</summary>
    public PlanVerdict? PlanVerdict { get; set; }
}

public sealed class ChatIndex
{
    public List<ChatIndexEntry> Items { get; set; } = [];
}

public sealed class ChatIndexEntry
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Стоит наверху боковой панели, вне групп по датам. Хранится в описи, а не в сессии:
    /// закрепление не считается правкой самой переписки.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Копия <see cref="ChatSession.Summary"/>. Лежит в индексе, потому что поиск по смыслу
    /// и вкладка сводок в журнале читают их все разом — по файлу на чат это была бы сотня
    /// чтений с диска на каждое нажатие.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>Когда чат начат — для сортировки «по созданию». У описи прежних версий пусто.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Во что обошёлся чат целиком, со всеми вариантами (<see cref="ChatCost.Total"/>).</summary>
    public decimal TotalCost { get; set; }
}
