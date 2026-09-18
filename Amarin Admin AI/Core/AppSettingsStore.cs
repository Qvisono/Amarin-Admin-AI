using System.Text.Json;

namespace Amarin.Core;

public sealed class AppSettingsStore
{
    private readonly string _file;

    public AppSettingsStore(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppPaths.Root
            : rootDirectory;
        _file = Path.Combine(root, "settings.json");
    }

    public string FilePath => _file;

    public bool Exists => File.Exists(_file);

    public AppSettings Load()
    {
        if (!File.Exists(_file))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            var text = File.ReadAllText(_file);
            var settings = JsonSerializer.Deserialize<AppSettings>(text, AppJson.Options)
                           ?? AppSettings.CreateDefault();
            var changed = MigrateLegacyChatPrompts(settings);
            changed |= SeedDownloadAllowedDomains(settings);
            settings.Appearance ??= new AppearanceSettings();
            changed |= settings.Appearance.Normalize();
            settings.ChatReasoning ??= new ReasoningSettings();
            settings.LiteReasoning ??= new ReasoningSettings();
            settings.HeavyReasoning ??= new ReasoningSettings();
            settings.RouterReasoning ??= new ReasoningSettings();
            changed |= MigrateRouterReasoning(settings);
            settings.TitleReasoning ??= new ReasoningSettings();
            settings.AgentFastReasoning ??= new ReasoningSettings();
            settings.AgentLiteReasoning ??= new ReasoningSettings();
            settings.AgentHeavyReasoning ??= new ReasoningSettings();
            settings.SynGuardReasoning ??= new ReasoningSettings();
            if (changed)
            {
                Save(settings);
            }

            return settings;
        }
        catch
        {
            return AppSettings.CreateDefault();
        }
    }

    /// <summary>Former shipped personality texts. Matching AppData is cleared so the user writes their own.</summary>
    internal static readonly string[] LegacyPersonalityPrompts =
    [
        "Ты - Amarin, ассистент системного администратора.",
        """
        Ты - Amarin, живой собеседник. Можно болтать, шутить, объяснять простыми словами и держать тёплый тон - как умный друг, а не как программа и не как сухой отчёт.
        Ты хорошо разбираешься в Windows и системном администрировании: это твоя экспертиза, а не повод звучать холодно или «по инструкции».
        Отвечай по-русски, если пользователь не просит иначе. Обычный разговор, шутки, вопросы ни о чём и мнения - это нормально, на них не нужно звать инструменты.
        """,
        """
        Ты - Amarin, живой собеседник. Можно болтать, шутить, объяснять простыми словами и держать тёплый тон - как умный друг, а не как программа и не как сухой отчёт.
        Ты хорошо разбираешься в Windows и системном администрировании: это твоя экспертиза, а не повод звучать холодно или «по инструкции».
        Отвечай по-русски, если пользователь не просит иначе. Обычный разговор, шутки, вопросы ни о чём и мнения - это нормально, на них не нужно звать инструменты.
        Никогда не говори «я не могу», если задачу можно отдать ИИ-агенту на этом ПК. Всё, чего не умеешь сам (открыть сайт или URL в браузере, страницу с биографией, файлы, диск, службы, скриншоты, скачивание) - умеет агент. Зови init_agent.
        """,
        """
        Ты - Amarin, живой собеседник. Можно болтать, шутить, объяснять простыми словами и держать тёплый тон - как умный друг, а не как программа и не как сухой отчёт.
        Ты хорошо разбираешься в Windows и системном администрировании: это твоя экспертиза, а не повод звучать холодно или «по инструкции».
        Отвечай по-русски, если пользователь не просит иначе. Обычный разговор, шутки, вопросы ни о чём и мнения - это нормально, на них не нужно звать инструменты.
        Не отказывайся от задачи, которую можно отдать ИИ-агенту на этом ПК - любой формулировкой отказа.
        Всё, чего не умеешь сам (открыть сайт или URL в браузере, страницу с биографией, файлы, диск, службы, скриншоты, скачивание) - умеет агент. Зови init_agent, не предлагай пользователю сделать это вручную.
        """,
        """
        Ты - Amarin, живой собеседник. Можно болтать, шутить, объяснять простыми словами и держать тёплый тон - как умный друг, а не как программа и не как сухой отчёт.
        Ты хорошо разбираешься в Windows и системном администрировании: это твоя экспертиза, а не повод звучать холодно или «по инструкции».
        Отвечай по-русски, если пользователь не просит иначе. Обычный разговор, шутки, вопросы ни о чём и мнения - это нормально, на них не нужно звать инструменты.
        Не отказывайся от задачи, которую можно отдать ИИ-агенту на этом ПК - любой формулировкой отказа.
        Всё, чего не умеешь сам (открыть сайт или URL в браузере, страницу с биографией, файлы, диск, службы, скриншоты, скачивание) - умеет агент. Зови init_agent, не предлагай пользователю сделать это вручную.
        Не используй эмодзи и цветные пиктограммы. Вместо них - обычный текст: скобки и текстовые эмоции вроде :) :( :D ;) ^_^.
        """
    ];

    /// <summary>
    /// До 1.19.4 маршрутизатор по умолчанию думал на «medium». Сохранённый файл держит это
    /// значение и после смены умолчания, поэтому прежнее умолчание переписывается один раз.
    /// </summary>
    /// <remarks>
    /// Отличающее условие — ровно та пара, что отгружалась: размышление включено и сила
    /// «medium». Всё остальное человек выставил руками в настройках, и трогать это нельзя.
    /// Откуда взялось значение, в settings.json не записано, так что осознанный выбор «думает,
    /// medium» неотличим от нетронутого слота и один раз потеряется — это принятый размен
    /// против флага-маркера, который остался бы в настройках навсегда ради одного релиза.
    /// Вызывается после заживления null: пустой слот и так означает «размышление выключено»,
    /// и считать его требующим миграции значило бы переписывать файл при каждом запуске.
    /// </remarks>
    internal static bool MigrateRouterReasoning(AppSettings settings)
    {
        var slot = settings.RouterReasoning;
        if (slot is null || slot.DisableThinking ||
            !string.Equals(slot.ReasoningEffort?.Trim(), "medium", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        slot.DisableThinking = true;
        slot.ReasoningEffort = null;
        return true;
    }

    internal static bool MigrateLegacyChatPrompts(AppSettings settings)
    {
        var changed = false;
        if (LegacyPersonalityPrompts.Any(legacy => SamePrompt(settings.MainPrompt, legacy)))
        {
            settings.MainPrompt = "";
            changed = true;
        }

        if (SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPrompt) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV2) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV3) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV4) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV5) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV6) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV7) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV8) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV9) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV10) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV11) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV12) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV13) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV14) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV15) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV16) ||
            SamePrompt(settings.TechAiPrompt, ChatEngine.LegacyDefaultTechPromptV17))
        {
            settings.TechAiPrompt = "";
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Fills the download allowlist on settings files written before it existed.
    /// Only <c>null</c> is seeded — an empty list means the user cleared it on purpose.
    /// </summary>
    internal static bool SeedDownloadAllowedDomains(AppSettings settings)
    {
        if (settings.DownloadAllowedDomains is not null)
        {
            return false;
        }

        settings.DownloadAllowedDomains = [.. new DownloadOptions().AllowedDomains];
        return true;
    }

    private static bool SamePrompt(string? left, string? right) =>
        NormalizePrompt(left) == NormalizePrompt(right);

    private static string NormalizePrompt(string? value) =>
        (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var json = JsonSerializer.Serialize(settings, AppJson.Options) + Environment.NewLine;
        AppDataFile.WriteAtomic(_file, json);
    }

    public void Update(Action<AppSettings> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        var settings = Load();
        mutator(settings);
        Save(settings);
    }
}
