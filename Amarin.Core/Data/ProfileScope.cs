namespace Amarin.Core;

/// <summary>
/// Данные одного профиля — настройки, чаты, ключи и всё, что лежит в его папке, — и переезд на
/// другой профиль на месте.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 — поля и <c>UseProfile</c> «сумки служб» окна (<c>AppServices</c>), и проверить
/// переезд можно было только с окном. Окно по-прежнему читает всё это через свою сумку, но
/// правда лежит здесь, а сумка только пересылает.
/// </para>
/// <para>
/// Часть объектов при смене профиля подменяется (хранилища настроек, чатов, ключей, заготовки
/// промптов), часть переводится на другую папку на месте — их ссылку уже держат движок, агент,
/// инструменты и копии <see cref="AgentOptions"/>: библиотека инструкций, журнал трат, журнал
/// аудита, рецепты, расписание, «Состояние ПК», машины, раскладка и индекс чатов, черновики и MCP.
/// </para>
/// </remarks>
internal sealed class ProfileScope
{
    private RecipeLibrary? _recipes;
    private ScheduleBook? _schedule;
    private HealthCache? _health;
    private MachineBook? _machines;
    private ChatTextIndex? _textIndex;
    private ChatOrganizer? _organizer;
    private DraftStore? _drafts;

    /// <param name="dataRoot">Папка профиля: корень данных у профиля по умолчанию, <c>profiles/&lt;id&gt;</c> у остальных.</param>
    /// <param name="settingsStore">Хранилище настроек этой папки — то же, что уже прочитал запуск.</param>
    /// <param name="settings">Прочитанные настройки.</param>
    /// <param name="keys">Держатель выбранного ключа — общий на программу и на все копии настроек.</param>
    /// <param name="ledger">Журнал трат — его ссылка роздана копиям настроек, поэтому он переводится, а не подменяется.</param>
    /// <param name="instructions">Инструкции пользователя — ссылку держат движок чата и инструмент.</param>
    /// <param name="environmentKey">Ключ из <c>VENICE_API_KEY</c> — общий для всех профилей.</param>
    /// <param name="openRouterEnvironmentKey">То же для <c>OPENROUTER_API_KEY</c>.</param>
    /// <param name="audit">Журнал аудита; null — не ведётся (тесты).</param>
    /// <param name="mcp">Серверы MCP; null — не заводились (тесты).</param>
    /// <param name="secrets">Что вырезать из отчётов о сбое; без него — свой, ни с кем не общий.</param>
    public ProfileScope(
        string dataRoot,
        AppSettingsStore settingsStore,
        AppSettings settings,
        ApiKeyProvider keys,
        SpendLedger ledger,
        InstructionLibrary instructions,
        string environmentKey = "",
        string openRouterEnvironmentKey = "",
        AuditLog? audit = null,
        McpHost? mcp = null,
        SecretRegistry? secrets = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(instructions);

        DataRoot = dataRoot;
        SettingsStore = settingsStore;
        Settings = settings;
        Keys = keys;
        Ledger = ledger;
        Instructions = instructions;
        EnvironmentKey = environmentKey ?? "";
        OpenRouterEnvironmentKey = openRouterEnvironmentKey ?? "";
        Audit = audit;
        Mcp = mcp;
        Secrets = secrets ?? new SecretRegistry();

        // Шифрование — через настройки профиля, а не через значение: после смены профиля это
        // уже другой объект настроек.
        ChatStore = new ChatStore(dataRoot) { Encrypt = () => Settings.EncryptChats };
        Prompts = new PromptLibrary(dataRoot);
        KeyStore = new ApiKeyStore(dataRoot, EnvironmentKey, OpenRouterEnvironmentKey);
    }

    /// <summary>Папка профиля, с которой работают хранилища.</summary>
    public string DataRoot { get; private set; }

    public AppSettingsStore SettingsStore { get; private set; }

    public AppSettings Settings { get; private set; }

    public ChatStore ChatStore { get; private set; }

    /// <summary>Заготовки основного промпта. Тоже на профиль — как и сам основной промпт.</summary>
    public PromptLibrary Prompts { get; private set; }

    /// <summary>Ключи профиля. Подменяется при смене: у профиля свои ключи.</summary>
    public ApiKeyStore KeyStore { get; private set; }

    public InstructionLibrary Instructions { get; }

    public SpendLedger Ledger { get; }

    public ApiKeyProvider Keys { get; }

    public AuditLog? Audit { get; }

    public McpHost? Mcp { get; }

    public SecretRegistry Secrets { get; }

    public string EnvironmentKey { get; }

    public string OpenRouterEnvironmentKey { get; }

    /// <summary>Рецепты профиля (C7).</summary>
    public RecipeLibrary Recipes
    {
        get => _recipes ??= new RecipeLibrary(DataRoot);
        init => _recipes = value;
    }

    /// <summary>Задачи по расписанию (C3).</summary>
    public ScheduleBook Schedule
    {
        get => _schedule ??= new ScheduleBook(DataRoot);
        init => _schedule = value;
    }

    /// <summary>Последний снимок «Состояния ПК» (C4).</summary>
    public HealthCache Health
    {
        get => _health ??= new HealthCache(DataRoot);
        init => _health = value;
    }

    /// <summary>Удалённые машины профиля (C10).</summary>
    public MachineBook Machines
    {
        get => _machines ??= new MachineBook(DataRoot);
        init => _machines = value;
    }

    /// <summary>
    /// Текст всех чатов для поиска «по тексту» (D1). Заводится по первому обращению: пока поиском
    /// не пользовались, каждая запись чата не обновляла бы ещё и индекс.
    /// </summary>
    public ChatTextIndex TextIndex
    {
        get
        {
            if (_textIndex is null)
            {
                _textIndex = new ChatTextIndex(DataRoot, () => Settings.EncryptChats);
                WireTextIndex(ChatStore);
            }

            return _textIndex;
        }
    }

    /// <summary>
    /// Папки, теги и архив списка чатов (D5). Удалённый чат вычищается из раскладки событием
    /// хранилища — какой бы путь его ни удалил.
    /// </summary>
    public ChatOrganizer Organizer
    {
        get
        {
            if (_organizer is null)
            {
                _organizer = new ChatOrganizer(DataRoot);
                WireOrganizer(ChatStore);
            }

            return _organizer;
        }
    }

    /// <summary>Черновики чатов (D12). Удалённый чат уносит и свой черновик — тем же событием.</summary>
    public DraftStore Drafts
    {
        get
        {
            if (_drafts is null)
            {
                _drafts = new DraftStore(DataRoot, () => Settings.EncryptChats);

                // Удаление чата слушает раскладка (WireOrganizer) — она и заводится, если ещё нет.
                _ = Organizer;
            }

            return _drafts;
        }
    }

    /// <summary>Перечитать настройки с диска — после импорта данных.</summary>
    public void ReloadSettings() => Settings = SettingsStore.Load();

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
        Secrets.Use(KeyStore.AllSecrets());
    }

    /// <summary>
    /// Переводит хранилища в папку другого профиля. Движок читает настройки через функцию, и
    /// пересобирать больше ничего не нужно, — но текущий чат вызывающий обязан сохранить раньше.
    /// </summary>
    public void UseProfile(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);

        // Прежнее хранилище пишет в фоне, а через мгновение на него уже никто не сошлётся:
        // всё, что оно не успело положить на диск, пропало бы вместе с ним.
        ChatStore.Flush();

        Directory.CreateDirectory(Path.Combine(dataRoot, "chats"));
        DataRoot = dataRoot;
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

    private void WireTextIndex(ChatStore store)
    {
        store.Saved += session => _textIndex?.Update(session);
        store.Deleted += id => _textIndex?.Remove(id);
        store.DeletedMany += ids =>
        {
            foreach (var id in ids)
            {
                _textIndex?.Remove(id);
            }
        };
    }

    private void WireOrganizer(ChatStore store)
    {
        store.Deleted += id =>
        {
            _organizer?.Forget(id);
            _drafts?.Forget(id);
        };

        // Раскладка — одной записью на пачку: каждая правка переписывает organize.json целиком.
        store.DeletedMany += ids =>
        {
            _organizer?.Forget(ids);
            foreach (var id in ids)
            {
                _drafts?.Forget(id);
            }
        };
    }
}
