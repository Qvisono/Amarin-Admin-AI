using Amarin.Core;
using Amarin.Tools;

namespace Amarin.UI;

internal sealed class AppServices : IDisposable
{
    public required AgentOptions Options { get; init; }

    // Settable, not init-only: switching profiles re-roots both stores in place.
    public required AppSettingsStore SettingsStore { get; set; }

    public required AppSettings Settings { get; set; }

    public required ChatStore ChatStore { get; set; }

    /// <summary>Заготовки основного промпта. Тоже на профиль — как и сам основной промпт.</summary>
    public required PromptLibrary Prompts { get; set; }

    /// <summary>
    /// Инструкции пользователя. Init-only, как и журнал трат: ссылку на библиотеку держат движок
    /// чата и инструмент <c>read_instruction</c>, поэтому смена профиля переводит её на другую
    /// папку, а не подменяет объект.
    /// </summary>
    public required InstructionLibrary Instructions { get; init; }

    /// <summary>Ключи Venice активного профиля.</summary>
    public required ApiKeyStore KeyStore { get; set; }

    /// <summary>
    /// Собственный журнал трат. Init-only: ссылка на него роздана всем копиям
    /// <see cref="AgentOptions"/>, подменять надо корень внутри, а не сам объект.
    /// </summary>
    public required SpendLedger Ledger { get; init; }

    /// <summary>
    /// Лимиты трат — тот же объект, что роздан копиям настроек. По умолчанию свой: тесты
    /// собирают службы руками и лимитов не касаются.
    /// </summary>
    internal SpendGuard SpendGuard { get; init; } = new(() => null, new SpendLedger(Path.GetTempPath()));

    /// <summary>
    /// Журнал аудита — тот же, что роздан копиям настроек. Null — не ведётся (тесты).
    /// </summary>
    internal AuditLog? Audit => Options.Audit;

    /// <summary>
    /// Ключ, которым платят прямо сейчас. Общий на программу и на все копии
    /// <see cref="AgentOptions"/>, поэтому init-only: подменять надо содержимое, а не сам объект.
    /// </summary>
    public required ApiKeyProvider Keys { get; init; }

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

    /// <summary>
    /// Рецепты профиля (C7). По умолчанию — рядом с настройками: тесты собирают службы руками
    /// во временной папке, и библиотека попадает туда же, а не в данные человека.
    /// </summary>
    internal RecipeLibrary Recipes
    {
        get => _recipes ??= new RecipeLibrary(Path.GetDirectoryName(SettingsStore.FilePath)!);
        init => _recipes = value;
    }

    private RecipeLibrary? _recipes;

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

    /// <summary>Запуск агента в обход чата — для задач по расписанию. Null — расписание не работает.</summary>
    internal IAgentHost? AgentHost { get; init; }

    /// <summary>Задачи по расписанию (C3). По умолчанию — рядом с настройками, как и рецепты.</summary>
    internal ScheduleBook Schedule
    {
        get => _schedule ??= new ScheduleBook(Path.GetDirectoryName(SettingsStore.FilePath)!);
        init => _schedule = value;
    }

    private ScheduleBook? _schedule;

    /// <summary>Последний снимок «Состояния ПК» (C4).</summary>
    internal HealthCache Health
    {
        get => _health ??= new HealthCache(Path.GetDirectoryName(SettingsStore.FilePath)!);
        init => _health = value;
    }

    private HealthCache? _health;

    /// <summary>Удалённые машины профиля (C10).</summary>
    internal MachineBook Machines
    {
        get => _machines ??= new MachineBook(Path.GetDirectoryName(SettingsStore.FilePath)!);
        init => _machines = value;
    }

    private MachineBook? _machines;

    /// <summary>Текст всех чатов для поиска «по тексту» (D1). Переезжает с профилем вместе с хранилищем.</summary>
    internal ChatTextIndex TextIndex
    {
        get
        {
            if (_textIndex is null)
            {
                _textIndex = new ChatTextIndex(Path.GetDirectoryName(SettingsStore.FilePath)!, () => Settings.EncryptChats);
                WireTextIndex(ChatStore);
            }

            return _textIndex;
        }
    }

    private ChatTextIndex? _textIndex;

    private void WireTextIndex(ChatStore store)
    {
        store.Saved += session => _textIndex?.Update(session);
        store.Deleted += id => _textIndex?.Remove(id);
    }

    /// <summary>
    /// Папки, теги и архив списка чатов (D5). Удалённый чат вычищается из раскладки событием
    /// хранилища — какой бы путь его ни удалил.
    /// </summary>
    internal ChatOrganizer Organizer
    {
        get
        {
            if (_organizer is null)
            {
                _organizer = new ChatOrganizer(Path.GetDirectoryName(SettingsStore.FilePath)!);
                WireOrganizer(ChatStore);
            }

            return _organizer;
        }
    }

    private ChatOrganizer? _organizer;

    private void WireOrganizer(ChatStore store) => store.Deleted += id =>
    {
        _organizer?.Forget(id);
        _drafts?.Delete(id);
    };

    /// <summary>Шаблоны новых чатов (D11).</summary>
    internal ChatTemplateBook Templates => _templates ??= new ChatTemplateBook(Path.GetDirectoryName(SettingsStore.FilePath)!);

    private ChatTemplateBook? _templates;

    /// <summary>Черновики чатов (D12). Удалённый чат уносит и свой черновик — тем же событием.</summary>
    internal DraftStore Drafts
    {
        get
        {
            if (_drafts is null)
            {
                _drafts = new DraftStore(Path.GetDirectoryName(SettingsStore.FilePath)!, () => Settings.EncryptChats);
                // Удаление чата слушает раскладка (WireOrganizer) — она и заводится, если ещё нет.
                _ = Organizer;
            }

            return _drafts;
        }
    }

    private DraftStore? _drafts;

    /// <summary>Разбор фактов в отчёте о работе (D7): тем же клиентом и ключами, что и сводка.</summary>
    internal WorkReportWriter WorkReports => _workReports ??= new WorkReportWriter(Http, Options, () => Settings);

    private WorkReportWriter? _workReports;

    /// <summary>Серверы MCP (C11). Null — не заводились (тесты): инструментов MCP нет.</summary>
    internal McpHost? Mcp { get; init; }

    public string? StartupPrompt { get; init; }

    /// <summary>Запуск был с <c>--send</c>: <see cref="StartupPrompt"/> отправляется сразу.</summary>
    public bool StartupSend { get; init; }

    /// <summary>Этот запуск стёр данные профиля по просьбе прежнего; null — не стирал.</summary>
    public WipeResult? StartupWipe { get; init; }

    /// <summary>Чат из <c>--open-chat</c>: открыть его при запуске.</summary>
    public string? StartupChatId { get; init; }

    /// <summary>Ключ из VENICE_API_KEY — он общий для всех профилей и не меняется на ходу.</summary>
    public required string EnvironmentKey { get; init; }

    /// <summary>
    /// То же для OPENROUTER_API_KEY. Без <c>required</c>: пустая строка — обычное положение
    /// дел, эту переменную заводят единицы.
    /// </summary>
    public string OpenRouterEnvironmentKey { get; init; } = "";

    public void ReloadSettings() => Settings = SettingsStore.Load();

    /// <summary>
    /// Разовый перенос цен, уже записанных в переписках, в журнал трат.
    /// </summary>
    /// <remarks>
    /// График показывает, сколько ушло с ключа, а не сколько лежит на диске: удалённая переписка
    /// денег не возвращает. Живой учёт от чатов и не зависит — он пишет в <c>usage/</c> в момент
    /// списания, — а вот этот перенос читает их файлы, и раньше его делала только страница
    /// «Key &amp; Info» по первому заходу. Пока туда не зашли, цены старых ответов лежали лишь
    /// в самих переписках, и удалённый до первого захода чат уносил свои деньги с графика
    /// навсегда. Поэтому перенос делается на запуске, не дожидаясь, что человек откроет страницу.
    /// <para>
    /// Ходит по всем файлам чатов, поэтому зовётся из фонового потока. Отметка в журнале
    /// закрывает эту дверь навсегда — обход случается ровно один раз за жизнь профиля.
    /// </para>
    /// </remarks>
    public void BackfillSpendLedger()
    {
        // Отметка профиля, а не ключа: переписки общие, а какой ключ за них платил, в них не
        // записано. Пока отметка стояла у ключа, каждый заведённый позже ключ забирал себе всю
        // чужую историю, и графики двух ключей совпадали до цента.
        if (Settings.SpendBackfilledAt is not null)
        {
            return;
        }

        try
        {
            // Переписки отдаются ленивой последовательностью, а не списком: их бывают сотни,
            // и файл чата бывает в мегабайты — собрать их все в память разом дороже, чем
            // прочитать диск второй раз в единственном за всю жизнь профиля проходе.
            Ledger.RepairDuplicateBackfills(ReadSavedSessions());
            Ledger.Backfill(KeyStore.ActiveSecret(), ReadSavedSessions());

            var stamp = DateTime.Now;
            Settings.SpendBackfilledAt = stamp;

            // Через Update, а не Save: зовут это из фонового потока, и записать сюда свою копию
            // настроек целиком значило бы затереть то, что человек в это же время менял в окне.
            SettingsStore.Update(settings => settings.SpendBackfilledAt = stamp);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // Перенос — удобство, а не обязанность: не вышло сейчас, попробуем при заходе
            // на страницу трат.
        }
    }

    private IEnumerable<ChatSession> ReadSavedSessions()
    {
        foreach (var entry in ChatStore.List())
        {
            if (ChatStore.TryLoad(entry.Id) is { } session)
            {
                yield return session;
            }
        }
    }

    /// <summary>
    /// Переносит выбор человека из хранилища в держатель ключа и заодно обновляет список
    /// секретов, которые вырезаются из отчёта об аварии.
    /// </summary>
    /// <remarks>
    /// Единственная точка, через которую проходят все три способа сменить выбранный ключ:
    /// выбор кружком на странице, добавление (новый ключ сразу становится выбранным) и
    /// удаление (выбранным становится следующий годный). Вместе с выбранным сюда же едет
    /// и весь список: из него слоты моделей достают назначенные им ключи, и список обязан
    /// смениться тем же присваиванием — иначе слот успел бы найти уже удалённый ключ.
    /// <para>
    /// Моделей эта смена больше не касается. До версии 1.23.0 активный ключ задавал провайдера
    /// всем девяти слотам разом, и здесь же выбор прятался в тайник до возвращения прежнего
    /// ключа. Теперь провайдер живёт в самом идентификаторе модели, у каждого слота свой,
    /// и менять при смене ключа нечего.
    /// </para>
    /// </remarks>
    public void ApplyActiveKey()
    {
        // Вместе с выбранным — ключ Venice: рисование картинок и чтение страниц умеет только он,
        // и при выбранном ключе OpenRouter взять его больше неоткуда.
        Keys.Use(KeyStore.ActiveCredential(), KeyStore.VeniceCredential(), KeyStore.Handles());
        CrashHandler.Secrets = KeyStore.AllSecrets();
    }

    /// <summary>
    /// Points the settings and chat stores at another profile's directory. The engine keeps
    /// reading settings through the same <c>Func&lt;AppSettings&gt;</c>, so nothing else has
    /// to be rebuilt — but the caller must persist the current chat first.
    /// </summary>
    public void UseProfile(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        // Прежнее хранилище пишет в фоне, а через мгновение на него уже никто не сошлётся:
        // всё, что оно не успело положить на диск, пропало бы вместе с ним.
        ChatStore.Flush();

        Directory.CreateDirectory(Path.Combine(dataRoot, "chats"));
        SettingsStore = new AppSettingsStore(dataRoot);
        // Перешифровка прежней папки теряет смысл: хранилище её больше не ведёт, а галочка
        // шифрования теперь читается из настроек другого профиля.
        ChatStore.StopReformat();
        ChatStore = new ChatStore(dataRoot) { Encrypt = () => Settings.EncryptChats };
        if (_textIndex is not null)
        {
            _textIndex.UseRoot(dataRoot, () => Settings.EncryptChats);
            WireTextIndex(ChatStore);
        }

        _drafts?.UseRoot(dataRoot, () => Settings.EncryptChats);
        _templates?.UseRoot(dataRoot);
        if (_organizer is not null)
        {
            _organizer.UseRoot(dataRoot);
            WireOrganizer(ChatStore);
        }

        Prompts = new PromptLibrary(dataRoot);
        Instructions.UseRoot(dataRoot);
        Recipes.UseRoot(dataRoot);
        Schedule.UseRoot(dataRoot);
        Health.UseRoot(dataRoot);
        Machines.UseRoot(dataRoot);
        Mcp?.UseRoot(dataRoot);
        Mcp?.Refresh();
        Settings = SettingsStore.Load();

        // Ключи у профиля свои, поэтому вместе с настройками переезжает и хранилище: иначе
        // человек, сменивший профиль, продолжал бы платить чужим ключом.
        KeyStore = new ApiKeyStore(dataRoot, EnvironmentKey, OpenRouterEnvironmentKey);
        KeyStore.Load();
        Ledger.UseRoot(dataRoot);
        Audit?.UseRoot(dataRoot);
        ApplyActiveKey();

        // Прерванная перешифровка или файлы, разложенные импортом, — привести к настройке
        // этого профиля. В фоне и по первым байтам: обычно делать нечего.
        Detached.Run(ChatStore.EnsureFormat(), "chat_reformat");
    }

    public void Dispose()
    {
        Mcp?.StopAll();
        Http.Dispose();
        DownloadHttp.Dispose();
    }
}
