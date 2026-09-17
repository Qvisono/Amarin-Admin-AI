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
    public string Id { get; set; } = "";

    public string Title { get; set; } = "Новый чат";

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string SelectedModelId { get; set; } = "";

    /// <summary>When true, the chat does not ask the model to think. Default preserves old behaviour.</summary>
    public bool DisableThinking { get; set; } = true;

    public string? ReasoningEffort { get; set; }

    [JsonIgnore]
    public ReasoningChoice Reasoning => new(DisableThinking, ReasoningEffort);

    public List<ChatDisplayMessage> Messages { get; set; } = [];

    public List<ChatMessage> ApiMessages { get; set; } = [];

    /// <summary>
    /// Context the model read on the last request, as Venice counted it. Kept with
    /// <see cref="LastPromptTokensApiIndex"/> so the gauge can add an estimate for whatever was
    /// appended since instead of showing a number that went stale the moment the user typed.
    /// Zero on chats saved before the field existed, and on chats that never got an answer.
    /// </summary>
    public int LastPromptTokens { get; set; }

    /// <summary>Length of <see cref="ApiMessages"/> when <see cref="LastPromptTokens"/> was measured.</summary>
    public int LastPromptTokensApiIndex { get; set; }

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
    /// Images the user attached to this message, stored inline as base64 so they survive a
    /// restart and travel with an exported or shared chat. Empty for assistant messages.
    /// </summary>
    public List<ImageAttachment> Images { get; set; } = [];

    /// <summary>
    /// Документы, прикреплённые к сообщению: PDF, таблицы, исходники. Хранятся так же, как
    /// картинки, — base64 внутри чата. У старых переписок поля в JSON нет, и список выходит
    /// пустым: чат сериализуется рефлексией, миграция не нужна.
    /// </summary>
    public List<FileAttachment> Files { get; set; } = [];

    public string? RequestedModelId { get; set; }

    public string? ResolvedModelId { get; set; }

    public TimeSpan Duration { get; set; }

    /// <summary>
    /// How long the model spent before the first word of the answer. Shown beside the duration
    /// when it is worth mentioning; zero for models that start writing immediately.
    /// </summary>
    public TimeSpan ThinkingDuration { get; set; }

    public VeniceCost? Cost { get; set; }

    /// <summary>
    /// What the conversation with the model itself cost, with tools and nested agents taken
    /// out. <see cref="Cost"/> is the sum of this, every tool call's own price and every nested
    /// agent's — which is exactly the breakdown shown when hovering the price.
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
    /// Во что обошлась проверка SynGuard за весь ход: по одному запросу на раунд инструментов
    /// каждого агента, сложенные вместе.
    /// </summary>
    /// <remarks>
    /// Как и заголовок, защитник платит своим клиентом, и ход его денег не видит — значит их
    /// прибавляют к итогу, а не вычитают. Одно поле на сообщение, а не строка на каждую
    /// проверку: человеку важно, сколько стоила защита, а не сколько раз она срабатывала.
    /// </remarks>
    public VeniceCost? GuardCost { get; set; }

    public AssistantStatus Status { get; set; }

    public List<ToolRound> ToolRounds { get; set; } = [];
}

public sealed class ToolRound
{
    public string InfoLine { get; set; } = "";

    /// <summary>
    /// What the model said before running this round of tools - its own remark, not the engine's
    /// status line.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="InfoLine"/> because the engine overwrites that one with
    /// "инструменты завершены" as soon as the round ends, and a remark stored there would not
    /// survive its own round. Empty on chats saved before the field existed.
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

    public List<ToolCallRecord> Calls { get; set; } = [];
}

public sealed class ToolCallRecord
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string ArgumentsJson { get; set; } = "";

    public string ResultPreview { get; set; } = "";

    /// <summary>
    /// The tool's output in full, capped — what the journal opens when a row is clicked.
    /// Empty on calls recorded before the field existed; the journal falls back to the summary.
    /// </summary>
    public string ResultText { get; set; } = "";

    public bool Success { get; set; }

    public ToolCallStatus Status { get; set; }

    /// <summary>
    /// When the tool actually started running. Default on calls recorded before the field
    /// existed — the journal falls back to the owning message's timestamp there rather than
    /// sorting every old action to the year zero.
    /// </summary>
    public DateTime StartedAt { get; set; }

    /// <summary>How long it ran. Zero when unknown, same as above.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Pictures the tool produced — a generated image, a screenshot. Stored inline as base64 so
    /// they survive a restart and travel with an exported chat, exactly like user attachments.
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
    /// What this one call added to the turn's bill. Display only — it is already inside the
    /// message total, so summing it again would double-count. Set for the tools that actually
    /// cost money (drawing a picture, scraping a page); null everywhere else.
    /// </summary>
    public VeniceCost? Cost { get; set; }

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
    /// Kept at the top of the sidebar, out of the by-date groups. Lives on the index rather
    /// than the session so pinning never counts as an edit to the conversation itself.
    /// </summary>
    public bool IsPinned { get; set; }
}
