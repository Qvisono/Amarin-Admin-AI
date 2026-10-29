using System.Collections.Concurrent;
using System.Text.Json;

namespace Amarin.Core;

public sealed class AppSettingsStore
{
    /// <summary>Сколько ждать тишины, прежде чем записать отложенное (<see cref="SaveDeferred"/>).</summary>
    public static readonly TimeSpan DeferDelay = TimeSpan.FromMilliseconds(400);

    private readonly string _file;

    private readonly Lock _gate = new();

    /// <summary>Текст файла и его отметка времени на момент чтения.</summary>
    /// <remarks>
    /// <see cref="Load"/> зовут отовсюду — движок чата, хост агентов, очередь подтверждений,
    /// генераторы заголовка и сводки, — и каждый вызов открывал файл заново. Кэш по отметке
    /// времени безопасен: файл правит только эта программа, а она у пользователя одна.
    /// Разбор при этом остаётся на каждый вызов, и объект по-прежнему возвращается свой —
    /// вызывающие его правят, и общий на всех сломал бы страницу настроек.
    /// </remarks>
    private string? _cachedText;
    private DateTime _cachedStamp;

    public AppSettingsStore(string? rootDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(rootDirectory)
            ? AppPaths.Root
            : rootDirectory;
        _file = Path.Combine(root, "settings.json");
    }

    public string FilePath => _file;

    /// <summary>Есть ли файл — или запись, которая вот-вот его создаст.</summary>
    public bool Exists => Writer.For(_file).Peek() is not null || File.Exists(_file);

    public AppSettings Load()
    {
        if (Writer.For(_file).Peek() is null && !File.Exists(_file))
        {
            return AppSettings.CreateDefault();
        }

        try
        {
            var text = ReadCached();
            if (text is null)
            {
                return AppSettings.CreateDefault();
            }

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
            settings.TitleReasoning ??= new ReasoningSettings();
            settings.AgentFastReasoning ??= new ReasoningSettings();
            settings.AgentLiteReasoning ??= new ReasoningSettings();
            settings.AgentHeavyReasoning ??= new ReasoningSettings();
            settings.SynGuardReasoning ??= new ReasoningSettings();
            settings.SummaryReasoning ??= new ReasoningSettings();
            changed |= MigrateSummarySlot(settings);
            settings.FoxReasoning ??= AppSettings.FactoryFoxReasoning();
            settings.BypassReasoning ??= new ReasoningSettings();
            settings.FastReasoning ??= new ReasoningSettings();
            settings.Fox ??= new FoxSettings();
            changed |= settings.Fox.Normalize();

            // Последней: она переписывает все слоты и размышление разом, и прежние разовые
            // поправки размышления маршрутизатора и защиты (1.19.4, 1.21.1) ей больше не нужны.
            changed |= MigrateFactoryModels(settings);
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

    /// <summary>
    /// Текст файла, который разбирается, или <c>null</c> (файла нет, он не открылся или
    /// повреждён вместе с копией).
    /// </summary>
    /// <remarks>
    /// Повреждённый файл <see cref="GuardedJsonFile"/> откладывает в сторону и поднимает
    /// запасную копию: иначе заводские значения, прочитанные вместо него, первым же сохранением
    /// затёрли бы файл человека.
    /// </remarks>
    private string? ReadCached()
    {
        // Отложенное, ещё не записанное, — правда новее файла: иначе только что включённое
        // «Только чтение» прошло бы мимо шлюза, пока ползунок оформления ждёт своей записи.
        if (Writer.For(_file).Peek() is { } pending)
        {
            return pending;
        }

        var stamp = File.GetLastWriteTimeUtc(_file);
        lock (_gate)
        {
            if (_cachedText is not null && _cachedStamp == stamp)
            {
                return _cachedText;
            }
        }

        GuardedJsonFile.Read(_file, IsReadable, out var text);
        if (text is null)
        {
            return null;
        }

        lock (_gate)
        {
            _cachedText = text;
            _cachedStamp = File.Exists(_file) ? File.GetLastWriteTimeUtc(_file) : stamp;
        }

        return text;
    }

    internal static bool IsReadable(string text) =>
        JsonSerializer.Deserialize<AppSettings>(text, AppJson.Options) is not null;

    /// <summary>Прежние заводские тексты характера. Совпавшие в настройках стираются — человек напишет свой.</summary>
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
    /// Переносит сводкам чата то, чем они пользовались до появления своего слота.
    /// </summary>
    /// <remarks>
    /// До 1.23.0 сводки молча брали модель и ключ быстрого агента. Оставить новый слот пустым
    /// значило бы увести их на подобранную по умолчанию модель — человек этого не просил и
    /// заметил бы только по счёту. Переносим один раз: слот заполнен — значит выбор уже сделан.
    /// </remarks>
    internal static bool MigrateSummarySlot(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SummaryModelId))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(settings.AgentFastModelId))
        {
            return false;
        }

        settings.SummaryModelId = settings.AgentFastModelId;
        ModelSlots.WriteKey(settings, ModelSlot.Summary, ModelSlots.ReadKey(settings, ModelSlot.AgentFast));
        return true;
    }

    /// <summary>
    /// Разовая замена всех моделей и уровней размышления на заводские нынешней ревизии
    /// (<see cref="AppSettings.CurrentFactoryModels"/>).
    /// </summary>
    /// <remarks>
    /// Меняется и выбранное руками — так решил человек к выпуску 1.35.0 (маршрутизатор и защита
    /// переехали на модели Decisions) и снова к 1.37.0-beta.8 (модели OpenRouter у всех). Ровно
    /// один раз на профиль за ревизию, поэтому выбор, сделанный после замены, остаётся. Ключи не
    /// трогаются, снимаются только их привязки к слотам: заводская модель могла уехать к другому
    /// провайдеру.
    /// <para>
    /// SynGuard и поиск «Авто» ставили ревизии 2 и 3; ревизия 4 их не трогает — об этом её не
    /// просили, и выключенный человеком SynGuard не должен включаться сам. Профиль, не прошедший
    /// ревизию 3, получает их, как получил бы ею.
    /// </para>
    /// <para>
    /// Ревизия 5 (1.37.1) ставит модели и размышление профиля человека, а с ними — так он решил —
    /// поиск через OpenRouter и разговор с агентом, сохраняемый в чате. SynGuard не трогает.
    /// </para>
    /// </remarks>
    internal static bool MigrateFactoryModels(AppSettings settings)
    {
        if (settings.FactoryModelsRevision >= AppSettings.CurrentFactoryModels)
        {
            return false;
        }

        var defaults = AppSettings.CreateDefault();
        foreach (var (_, read, write) in ModelSlots.All)
        {
            write(settings, read(defaults));
        }

        settings.FastReasoning = defaults.FastReasoning;
        settings.ChatReasoning = defaults.ChatReasoning;
        settings.LiteReasoning = defaults.LiteReasoning;
        settings.HeavyReasoning = defaults.HeavyReasoning;
        settings.RouterReasoning = defaults.RouterReasoning;
        settings.TitleReasoning = defaults.TitleReasoning;
        settings.AgentFastReasoning = defaults.AgentFastReasoning;
        settings.AgentLiteReasoning = defaults.AgentLiteReasoning;
        settings.AgentHeavyReasoning = defaults.AgentHeavyReasoning;
        settings.SynGuardReasoning = defaults.SynGuardReasoning;
        settings.SummaryReasoning = defaults.SummaryReasoning;
        settings.FoxReasoning = defaults.FoxReasoning;
        settings.BypassReasoning = defaults.BypassReasoning;
        settings.ModelKeys = null;
        if (settings.FactoryModelsRevision < 3)
        {
            settings.SynGuardEnabled = true;
        }

        settings.WebSearchProvider = defaults.WebSearchProvider;
        settings.WebSearchKeyId = null;
        settings.AgentMemory = defaults.AgentMemory;

        // Голос (1.37.0-beta.8): распознавание и озвучка — «Авто», они берут OpenRouter, когда к
        // нему есть ключ (VoiceAvailability).
        settings.VoiceModel = defaults.VoiceModel;
        settings.SpeechModel = defaults.SpeechModel;
        settings.SpeechVoice = defaults.SpeechVoice;
        settings.SpeechRate = defaults.SpeechRate;
        settings.FactoryModelsRevision = AppSettings.CurrentFactoryModels;
        return true;
    }

    internal static bool MigrateLegacyChatPrompts(AppSettings settings)
    {
        var changed = false;

        // Пустой промпт не совпадает ни с одним отгружавшимся, а сравнение каждого из них стоит
        // двух копий текста. Load зовут постоянно, и у большинства оба слота давно пусты.
        if (!string.IsNullOrWhiteSpace(settings.MainPrompt) &&
            LegacyPersonalityPrompts.Any(legacy => SamePrompt(settings.MainPrompt, legacy)))
        {
            settings.MainPrompt = "";
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(settings.TechAiPrompt) &&
            LegacyTechPrompts.All.Any(legacy => SamePrompt(settings.TechAiPrompt, legacy)))
        {
            settings.TechAiPrompt = "";
            changed = true;
        }

        // Нетронутая копия прежнего промпта агента: без сброса у человека навсегда остались бы
        // убранная «Yandex policy» и «отвечать только по-русски» (см. LegacyAgentPrompts).
        if (!string.IsNullOrWhiteSpace(settings.TechAgentPrompt) &&
            LegacyAgentPrompts.All.Any(legacy => SamePrompt(settings.TechAgentPrompt, legacy)))
        {
            settings.TechAgentPrompt = "";
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Заполняет белый список загрузок в настройках, записанных до его появления. Засевается только
    /// <c>null</c>: пустой список значит, что человек очистил его сам.
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
        var json = Serialize(settings);

        // Через писателя пути: он отменяет отложенную запись и не даёт ей, запоздав, лечь
        // поверх этой.
        Writer.For(_file).WriteNow(json);

        lock (_gate)
        {
            _cachedText = json;
            _cachedStamp = File.GetLastWriteTimeUtc(_file);
        }
    }

    /// <summary>
    /// Сохранить чуть позже: сериализация — сейчас, на вызывающем потоке, запись — в фоне после
    /// <see cref="DeferDelay"/> тишины. Для того, что меняется непрерывно: ползунки оформления
    /// писали файл (и его копию) на каждый тик прямо на потоке окна.
    /// </summary>
    /// <remarks>
    /// Писатель один на путь и общий для всех экземпляров хранилища, поэтому <see cref="Load"/>
    /// и <see cref="Update"/> любого из них видят ещё не записанное, а <see cref="Save"/> отменяет
    /// его и пишет своё. Всё, что читает <c>settings.json</c> мимо хранилища (архив данных,
    /// резервная копия), сперва зовёт <see cref="FlushAll"/>; выход процесса — тоже.
    /// </remarks>
    public void SaveDeferred(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Writer.For(_file).Defer(Serialize(settings));
    }

    /// <summary>Записать отложенное этого файла сейчас.</summary>
    public void Flush() => Writer.For(_file).Flush();

    /// <summary>Записать всё отложенное — перед выходом и перед чтением файлов мимо хранилища.</summary>
    public static void FlushAll() => Writer.FlushAll();

    private static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, AppJson.Options) + Environment.NewLine;

    public void Update(Action<AppSettings> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        var settings = Load();
        mutator(settings);
        Save(settings);
    }

    /// <summary>Единственный писатель одного <c>settings.json</c> в процессе.</summary>
    /// <remarks>
    /// Все записи пути — и немедленные, и отложенные — идут под его замком: иначе отложенная,
    /// сработав посреди немедленной, легла бы поверх более новой.
    /// </remarks>
    private sealed class Writer
    {
        private static readonly ConcurrentDictionary<string, Writer> All = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _path;
        private readonly Lock _gate = new();
        private readonly Timer _timer;
        private string? _pending;

        static Writer()
        {
            // Последний тик ползунка не должен пропасть оттого, что программу закрыли раньше
            // паузы. Окно сбрасывает и само, а это — на любой иной выход.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAll();
        }

        private Writer(string path)
        {
            _path = path;
            _timer = new Timer(_ => FlushQuietly(), null, Timeout.Infinite, Timeout.Infinite);
        }

        public static Writer For(string path) => All.GetOrAdd(Path.GetFullPath(path), full => new Writer(full));

        public static void FlushAll()
        {
            foreach (var writer in All.Values)
            {
                writer.FlushQuietly();
            }
        }

        public string? Peek()
        {
            lock (_gate)
            {
                return _pending;
            }
        }

        public void Defer(string text)
        {
            lock (_gate)
            {
                _pending = text;
                _timer.Change(DeferDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void WriteNow(string text)
        {
            lock (_gate)
            {
                _pending = null;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                GuardedJsonFile.Write(_path, text);
            }
        }

        public void Flush()
        {
            lock (_gate)
            {
                if (_pending is not { } text)
                {
                    return;
                }

                // Папку профиля могли удалить, пока запись ждала паузы: воскрешать её ради
                // отложенного тика ползунка нельзя.
                if (Path.GetDirectoryName(_path) is { } folder && !Directory.Exists(folder))
                {
                    _pending = null;
                    return;
                }

                GuardedJsonFile.Write(_path, text);
                _pending = null;
            }
        }

        /// <summary>Из таймера и при выходе: исключение здесь уронило бы процесс.</summary>
        private void FlushQuietly()
        {
            try
            {
                Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Не записалось — осталось отложенным: следующий тик, сброс или выход попробуют снова.
                CrashLog.Write($"settings: deferred write failed: {ex.Message}");
            }
        }
    }
}
