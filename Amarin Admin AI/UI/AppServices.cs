using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Службы, которые получает окно. Собирает их корень композиции (<c>AppComposition</c>), тесты —
/// <c>UiServices</c>.
/// </summary>
/// <remarks>
/// Данные профиля — настройки, чаты, ключи и всё, что лежит в его папке, — живут в
/// <see cref="ProfileScope"/>, а здесь только пересылаются: окно читает их по-прежнему
/// (<c>_services.ChatStore</c>), а переезд на другой профиль проверяется без окна.
/// </remarks>
internal sealed class AppServices : IDisposable
{
    public required AgentOptions Options { get; init; }

    /// <summary>Данные открытого профиля и переезд на другой.</summary>
    public required ProfileScope Profile { get; init; }

    public AppSettingsStore SettingsStore => Profile.SettingsStore;

    public AppSettings Settings => Profile.Settings;

    public ChatStore ChatStore => Profile.ChatStore;

    /// <summary>Заготовки основного промпта. Тоже на профиль — как и сам основной промпт.</summary>
    public PromptLibrary Prompts => Profile.Prompts;

    /// <summary>
    /// Инструкции пользователя. Ссылку на библиотеку держат движок чата и инструмент
    /// <c>read_instruction</c>, поэтому смена профиля переводит её на другую папку, а не
    /// подменяет объект.
    /// </summary>
    public InstructionLibrary Instructions => Profile.Instructions;

    /// <summary>Ключи активного профиля.</summary>
    public ApiKeyStore KeyStore => Profile.KeyStore;

    /// <summary>
    /// Собственный журнал трат. Ссылка на него роздана всем копиям <see cref="AgentOptions"/>,
    /// переводить надо корень внутри, а не сам объект.
    /// </summary>
    public SpendLedger Ledger => Profile.Ledger;

    /// <summary>
    /// Лимиты трат — тот же объект, что роздан копиям настроек. По умолчанию свой: тесты
    /// собирают службы руками и лимитов не касаются.
    /// </summary>
    internal SpendGuard SpendGuard { get; init; } = new(() => null, new SpendLedger(Path.GetTempPath()));

    /// <summary>
    /// Журнал аудита — тот же, что роздан копиям настроек. Null — не ведётся (тесты).
    /// </summary>
    internal AuditLog? Audit => Profile.Audit;

    /// <summary>
    /// Ключ, которым платят прямо сейчас. Общий на программу и на все копии
    /// <see cref="AgentOptions"/>: подменять надо содержимое, а не сам объект.
    /// </summary>
    public ApiKeyProvider Keys => Profile.Keys;

    /// <summary>
    /// Остатки всех ключей. Одна книга на программу: её наполняют все клиенты, а складывает
    /// сумму плашка в композере.
    /// </summary>
    public required BalanceBook Balances { get; init; }

    public required ProfileStore Profiles { get; init; }

    public required ProfileRegistry ProfileRegistry { get; set; }

    public required HttpClient Http { get; init; }

    public required HttpClient DownloadHttp { get; init; }

    public required VeniceClient Venice { get; init; }

    public required VeniceModelListCache Models { get; init; }

    public required ChatEngine Chat { get; init; }

    public required ChatTitleGenerator Titles { get; init; }

    public required ChatSummaryGenerator Summaries { get; init; }

    public required ConfirmationQueue Confirmations { get; init; }

    /// <summary>
    /// Планы агентов на одобрение. Не required и с умолчанием: тесты собирают службы руками, а
    /// без очереди план просто не показывается — <see cref="AgentHost"/> её получает отдельно.
    /// </summary>
    internal PlanReviewQueue PlanReviews { get; init; } = new();

    /// <summary>Рецепты профиля (C7).</summary>
    internal RecipeLibrary Recipes => Profile.Recipes;

    /// <summary>
    /// Запуск рецептов. Без собранного в Program — отказ любой записи: исполнять инструменты
    /// агента мимо настоящего набора тестам незачем.
    /// </summary>
    internal RecipeRunner RecipeRunner
    {
        get => _recipeRunner ??= new RecipeRunner(() => new ToolRegistry([]), Confirmations, () => Settings, () => Audit);
        init => _recipeRunner = value;
    }

    private RecipeRunner? _recipeRunner;

    /// <summary>
    /// Инструменты агента и файловые — те, которыми исполняются рецепты, команды отложенных задач
    /// и проверки их условий. Без собранного в Program — пустой набор: тестам окна исполнять
    /// настоящие команды незачем.
    /// </summary>
    internal Func<ToolRegistry> RunTools { get; init; } = () => new ToolRegistry([]);

    /// <summary>Запуск агента в обход чата — для задач по расписанию. Null — расписание не работает.</summary>
    internal IAgentHost? AgentHost { get; init; }

    /// <summary>Задачи по расписанию (C3).</summary>
    internal ScheduleBook Schedule => Profile.Schedule;

    /// <inheritdoc cref="ProfileScope.Deferred"/>
    internal DeferredBook Deferred => Profile.Deferred;

    /// <summary>Последний снимок «Состояния ПК» (C4).</summary>
    internal HealthCache Health => Profile.Health;

    /// <summary>Удалённые машины профиля (C10).</summary>
    internal MachineBook Machines => Profile.Machines;

    /// <summary>Текст всех чатов для поиска «по тексту» (D1). Переезжает с профилем вместе с хранилищем.</summary>
    internal ChatTextIndex TextIndex => Profile.TextIndex;

    /// <summary>Папки, теги и архив списка чатов (D5).</summary>
    internal ChatOrganizer Organizer => Profile.Organizer;

    /// <summary>Черновики чатов (D12).</summary>
    internal DraftStore Drafts => Profile.Drafts;

    /// <summary>Разбор фактов в отчёте о работе (D7): тем же клиентом и ключами, что и сводка.</summary>
    internal WorkReportWriter WorkReports => _workReports ??= new WorkReportWriter(Http, Options, () => Settings);

    private WorkReportWriter? _workReports;

    /// <summary>Серверы MCP (C11). Null — не заводились (тесты): инструментов MCP нет.</summary>
    internal McpHost? Mcp => Profile.Mcp;

    public string? StartupPrompt { get; init; }

    /// <summary>Запуск был с <c>--send</c>: <see cref="StartupPrompt"/> отправляется сразу.</summary>
    public bool StartupSend { get; init; }

    /// <summary>Этот запуск стёр данные профиля по просьбе прежнего; null — не стирал.</summary>
    public WipeResult? StartupWipe { get; init; }

    /// <summary>Чат из <c>--open-chat</c>: открыть его при запуске.</summary>
    public string? StartupChatId { get; init; }

    /// <summary>Действие запуска (G6): новый чат, состояние ПК, трей, путь из Проводника.</summary>
    internal StartupAction StartupAction { get; init; }

    /// <summary>Путь из «Спросить Amarin» в Проводнике.</summary>
    public string? StartupAskPath { get; init; }

    /// <summary>Ключ из VENICE_API_KEY — он общий для всех профилей и не меняется на ходу.</summary>
    public string EnvironmentKey => Profile.EnvironmentKey;

    /// <summary>То же для OPENROUTER_API_KEY.</summary>
    public string OpenRouterEnvironmentKey => Profile.OpenRouterEnvironmentKey;

    /// <inheritdoc cref="ProfileScope.ReloadSettings"/>
    public void ReloadSettings() => Profile.ReloadSettings();

    /// <inheritdoc cref="ProfileScope.BackfillSpendLedger"/>
    public void BackfillSpendLedger() => Profile.BackfillSpendLedger();

    /// <inheritdoc cref="ProfileScope.ApplyActiveKey"/>
    public void ApplyActiveKey() => Profile.ApplyActiveKey();

    /// <inheritdoc cref="ProfileScope.UseProfile"/>
    public void UseProfile(string dataRoot)
    {
        Profile.UseProfile(dataRoot);
        SpendGuard.ForgetWarnings();
    }

    public void Dispose()
    {
        Mcp?.StopAll();
        Http.Dispose();
        DownloadHttp.Dispose();
    }
}
