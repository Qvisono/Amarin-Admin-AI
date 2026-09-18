namespace Amarin.Core;

public enum ApprovalMode
{
    Normal,
    AlwaysApprove
}

/// <summary>
/// Palette preset. The first three are the originals and keep their names in settings.json;
/// the rest are the shipped colour presets. See <see cref="ThemeCatalog"/> for what each maps to.
/// </summary>
/// <remarks>
/// The order here is free: <see cref="AppJson"/> serialises enums as camelCase strings, so a theme
/// written into <c>settings.json</c> is found by name and not by position. What the grid in the
/// settings looks like is decided by <see cref="ThemeCatalog.Presets"/>, not by this list.
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
    Cherry
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
    /// Backdrop, glass and layout customisation on top of <see cref="Theme"/>.
    /// Defaults reproduce the plain preset look, so this is inert until the user turns it on.
    /// </summary>
    public AppearanceSettings Appearance { get; set; } = new();

    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Normal;

    /// <summary>Show the bottom-right toast when a turn finishes and the window is not focused.</summary>
    public bool NotifyOnResponseComplete { get; set; } = true;

    /// <summary>Play a short system sound with that toast. Ignored when the toast is off.</summary>
    public bool NotifySound { get; set; } = true;

    /// <summary>
    /// Show the "поделиться" and "экспорт" buttons under messages. Both produce unencrypted
    /// payloads that carry the whole conversation, so the feature can be switched off entirely.
    /// </summary>
    public bool ChatSharingEnabled { get; set; } = true;

    /// <summary>
    /// Hosts <c>download_file</c> may download from, subdomains included.
    /// <c>null</c> means "not seeded yet" — the store fills it from the shipped defaults on first load.
    /// An empty list is a deliberate choice and blocks every download.
    /// </summary>
    public List<string>? DownloadAllowedDomains { get; set; }

    /// <summary>Empty means use Venice:Model from the shipped appsettings.json.</summary>
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
    /// The cheap tier, for work that does not need a flagship or that the user asked to hurry.
    /// Thinking is off by default: the whole point of this slot is the answer arriving sooner,
    /// and a reasoning pass would spend exactly what it saves.
    /// </summary>
    public string AgentFastModelId { get; set; } = "deepseek-v4-flash-0731-fast";

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
    /// Защитник отвечает одним словом на вызов, и размышление перед этим только оплачивается —
    /// та же причина, по которой оно выключено у маршрутизатора.
    /// </summary>
    public ReasoningSettings SynGuardReasoning { get; set; } = new()
    {
        DisableThinking = true
    };

    /// <summary>Optional personality. Empty means the chat companion uses only the tech prompt.</summary>
    public string MainPrompt { get; set; } = "";

    public string TechAiPrompt { get; set; } = "";

    public string TechAgentPrompt { get; set; } = "";

    public SessionMode SessionMode { get; set; } = SessionMode.Continuous;

    /// <summary>
    /// Спрашивать GitHub о новой версии при запуске, не чаще раза в
    /// <see cref="UpdateChecker.AutoCheckInterval"/>. Проверка только узнаёт номер последней
    /// версии; скачивание и установку начинает человек кнопкой и подтверждает отдельным окном.
    /// </summary>
    public bool AutoCheckUpdates { get; set; } = true;

    /// <summary>Когда автопроверка последний раз ходила в сеть. UTC; <c>null</c> — ещё ни разу.</summary>
    public DateTime? LastUpdateCheckUtc { get; set; }

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
