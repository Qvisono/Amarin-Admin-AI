using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Заводские модели 1.34.0: новые профили, разовая замена у старых, то же семейство у любого
/// провайдера и переезд слотов от провайдера без ключа.
/// </summary>
public sealed class FactoryModelsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-factory-" + Guid.NewGuid().ToString("N"));

    public FactoryModelsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Файл 1.33.0 с заводскими моделями — как его писала прежняя версия.</summary>
    private const string Shipped133 = """
        {
          "chatModelId": "",
          "liteModelId": "openai-gpt-56-luna",
          "liteReasoning": { "disableThinking": false },
          "heavyModelId": "grok-4-6",
          "heavyReasoning": { "disableThinking": false, "reasoningEffort": "medium" },
          "routerModelId": "openai-gpt-56-luna",
          "routerReasoning": { "disableThinking": true },
          "titleModelId": "openai-gpt-56-luna",
          "titleReasoning": { "disableThinking": true },
          "agentFastModelId": "deepseek-v4-flash-0731-fast",
          "agentFastReasoning": { "disableThinking": true },
          "agentLiteModelId": "openai-gpt-56-luna",
          "agentLiteReasoning": { "disableThinking": false },
          "agentHeavyModelId": "grok-4-6",
          "agentHeavyReasoning": { "disableThinking": false, "reasoningEffort": "high" },
          "summaryModelId": "deepseek-v4-flash-0731-fast",
          "summaryReasoning": { "disableThinking": true }
        }
        """;

    private AppSettings LoadFile(string json)
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), json);
        return new AppSettingsStore(_root).Load();
    }

    [Fact]
    public void A_new_profile_gets_the_new_models_and_auto()
    {
        var settings = AppSettings.CreateDefault();

        // 1.37.0-beta.8: модели OpenRouter — он приоритетнее.
        Assert.Equal(VeniceModelCatalog.AutoId, settings.ChatModelId);
        Assert.Equal(("openrouter:minimax/minimax-m3.1-flash:nitro", "medium"), (settings.FastModelId, settings.FastReasoning.ReasoningEffort));
        Assert.Equal(("openrouter:anthropic/claude-haiku-5.5", "medium"), (settings.LiteModelId, settings.LiteReasoning.ReasoningEffort));
        Assert.Equal(("openrouter:anthropic/claude-sonnet-5.5", "medium"), (settings.HeavyModelId, settings.HeavyReasoning.ReasoningEffort));
        Assert.Equal(("openrouter:typesafe/jev-1.14", true), (settings.RouterModelId, settings.RouterReasoning.DisableThinking));
        Assert.Equal(("openrouter:typesafe/jev-1.14", true), (settings.SynGuardModelId, settings.SynGuardReasoning.DisableThinking));
        Assert.Equal(("openrouter:anthropic/claude-haiku-5.5", true), (settings.FoxModelId, settings.FoxReasoning.DisableThinking));
        Assert.Equal(("openrouter:minimax/minimax-m3.1-flash:nitro", "medium"), (settings.AgentFastModelId, settings.AgentFastReasoning.ReasoningEffort));
        Assert.False(settings.AgentFastReasoning.DisableThinking);
        Assert.Equal(("openrouter:anthropic/claude-haiku-5.5", "medium"), (settings.AgentLiteModelId, settings.AgentLiteReasoning.ReasoningEffort));
        Assert.Equal(("openrouter:anthropic/claude-sonnet-5.5", "high"), (settings.AgentHeavyModelId, settings.AgentHeavyReasoning.ReasoningEffort));
        Assert.Equal(("openrouter:deepseek/deepseek-v4-flash-0731", true), (settings.TitleModelId, settings.TitleReasoning.DisableThinking));
        Assert.Equal(("openrouter:anthropic/claude-haiku-5.5", "low"), (settings.SummaryModelId, settings.SummaryReasoning.ReasoningEffort));
        Assert.Equal(("openrouter:x-ai/grok-4.3", "low"), (settings.BypassModelId, settings.BypassReasoning.ReasoningEffort));
        Assert.Null(settings.WebSearchProvider);
        Assert.Null(settings.VoiceModel);
        Assert.Equal((VeniceModelCatalog.AutoId, (string?)null, 0), (settings.SpeechModel, settings.SpeechVoice, settings.SpeechRate));
        Assert.Equal(AppSettings.CurrentFactoryModels, settings.FactoryModelsRevision);
    }

    /// <summary>
    /// Обход (1.36.0) — слот новый: старый файл без поля получает заводскую модель, а всё, что
    /// человек выбрал в остальных слотах, остаётся — ревизия заводских моделей ради него не растёт.
    /// </summary>
    [Fact]
    public void An_old_file_gets_the_bypass_slot_and_keeps_its_choices()
    {
        var old = AppSettings.CreateDefault();
        old.HeavyModelId = "openrouter:some/model";
        var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(old, AppJson.Options))!.AsObject();
        Assert.True(json.Remove("bypassModelId"));
        Assert.True(json.Remove("bypassReasoning"));

        var settings = LoadFile(json.ToJsonString());

        Assert.Equal(AppSettings.FactoryBypassModelId, settings.BypassModelId);
        Assert.NotNull(settings.BypassReasoning);
        Assert.Equal("openrouter:some/model", settings.HeavyModelId);
    }

    /// <summary>Заводские модели 1.33.0 меняются на новые вместе с размышлением — один раз.</summary>
    [Fact]
    public void The_old_factory_models_are_replaced_once()
    {
        var settings = LoadFile(Shipped133);

        Assert.Equal(AppSettings.CreateDefault().LiteModelId, settings.LiteModelId);
        Assert.Equal("medium", settings.LiteReasoning.ReasoningEffort);
        Assert.Equal(AppSettings.ShippedHeavyModelId, settings.HeavyModelId);
        Assert.Equal("medium", settings.HeavyReasoning.ReasoningEffort);
        Assert.Equal(AppSettings.FactoryDecisionModelId, settings.RouterModelId);
        Assert.True(settings.RouterReasoning.DisableThinking);
        Assert.Equal(AppSettings.ShippedFastModelId, settings.AgentFastModelId);
        Assert.Equal("medium", settings.AgentFastReasoning.ReasoningEffort);
        Assert.Equal(AppSettings.ShippedHeavyModelId, settings.AgentHeavyModelId);
        Assert.Equal("high", settings.AgentHeavyReasoning.ReasoningEffort);
        Assert.Equal(AppSettings.ShippedLiteModelId, settings.SummaryModelId);
        Assert.Equal("low", settings.SummaryReasoning.ReasoningEffort);
        Assert.Equal(AppSettings.ShippedFastModelId, settings.FastModelId);

        // Beta переводит и прежний пустой слот чата в «Авто».
        Assert.Equal("auto", settings.ChatModelId);
        Assert.Equal(AppSettings.CurrentFactoryModels, settings.FactoryModelsRevision);
    }

    /// <summary>Beta заменяет и ручные модели, и ручные уровни размышления.</summary>
    [Fact]
    public void A_chosen_model_or_level_is_reset_once_for_beta()
    {
        var json = Shipped133
            .Replace("\"heavyModelId\": \"grok-4-6\"", "\"heavyModelId\": \"kimi-k2-7-code\"", StringComparison.Ordinal)
            .Replace("\"reasoningEffort\": \"high\"", "\"reasoningEffort\": \"max\"", StringComparison.Ordinal);

        var settings = LoadFile(json);

        Assert.Equal(AppSettings.ShippedHeavyModelId, settings.HeavyModelId);
        Assert.Equal("medium", settings.HeavyReasoning.ReasoningEffort);

        // В этой миграции ручное размышление тоже заменяется.
        Assert.Equal(AppSettings.ShippedHeavyModelId, settings.AgentHeavyModelId);
        Assert.Equal("high", settings.AgentHeavyReasoning.ReasoningEffort);
    }

    /// <summary>Вернувший себе прежнюю модель после замены её не теряет на следующем запуске.</summary>
    [Fact]
    public void A_model_chosen_back_after_the_replacement_survives_restarts()
    {
        var store = new AppSettingsStore(_root);
        var settings = LoadFile(Shipped133);
        settings.HeavyModelId = "grok-4-6";
        store.Save(settings);

        Assert.Equal("grok-4-6", new AppSettingsStore(_root).Load().HeavyModelId);
    }

    /// <summary>После смены провайдера слот использует его ключ по умолчанию.</summary>
    [Fact]
    public void Old_slot_bindings_are_cleared()
    {
        var json = Shipped133.Replace("\"chatModelId\": \"\",", "\"chatModelId\": \"\", \"modelKeys\": { \"Heavy\": \"k2\" },", StringComparison.Ordinal);

        var settings = LoadFile(json);

        Assert.Null(ModelSlots.ReadKey(settings, ModelSlot.Heavy));
    }

    /// <summary>
    /// Ревизию без поля в файле узнаёт сама десериализация: файл без отметки — нулевой и проходит
    /// замену один раз, а объект, собранный в коде, — нынешний. В beta.1 ради этого файл
    /// настроек разбирался на каждом запуске второй раз.
    /// </summary>
    [Fact]
    public void A_file_without_the_mark_is_revision_zero_and_settings_built_in_code_are_current()
    {
        Assert.Equal(0, System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}", AppJson.Options)!.FactoryModelsRevision);
        Assert.Equal(AppSettings.CurrentFactoryModels, new AppSettings().FactoryModelsRevision);
        Assert.Equal(AppSettings.CurrentFactoryModels, AppSettings.CreateDefault().FactoryModelsRevision);

        new AppSettingsStore(_root).Save(new AppSettings { RouterModelId = "my-router" });
        Assert.Equal("my-router", new AppSettingsStore(_root).Load().RouterModelId);
    }

    private static List<VeniceModelInfo> Catalog(params string[] ids) =>
        [.. ids.Select(id => new VeniceModelInfo { Id = id })];

    /// <remarks>
    /// Ревизия 2 — профиль, уже прошедший замену beta.1: beta.2 меняет его модели ещё раз. Ревизия 3 —
    /// прошедший и её: ревизия 4 (1.37.0-beta.8) меняет модели и голос, но SynGuard и поиск не трогает.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Beta_resets_every_slot_in_each_profile_once_and_preserves_other_settings(int revision)
    {
        foreach (var profile in new[] { "first", "second" })
        {
            var root = Path.Combine(_root, profile);
            var store = new AppSettingsStore(root);
            var settings = AppSettings.CreateDefault();
            settings.FactoryModelsRevision = revision;
            settings.MainPrompt = "keep my instructions";
            settings.ApprovalMode = ApprovalMode.ReadOnly;
            settings.WebSearchProvider = LlmProvider.OpenRouter;
            settings.WebSearchKeyId = "old-search-key";
            settings.SynGuardEnabled = false;
            settings.VoiceModel = "openai/whisper-large-v3";
            settings.SpeechModel = "tts-kokoro";
            settings.SpeechVoice = "af_sky";
            settings.SpeechRate = 3;
            foreach (var (slot, _, write) in ModelSlots.All)
            {
                write(settings, "custom-model");
                ModelSlots.WriteKey(settings, slot, "old-key");
            }
            store.Save(settings);

            var loaded = new AppSettingsStore(root).Load();
            var defaults = AppSettings.CreateDefault();
            foreach (var (slot, read, _) in ModelSlots.All)
            {
                Assert.Equal(read(defaults), read(loaded));
                Assert.Null(ModelSlots.ReadKey(loaded, slot));
            }
            Assert.Equal("keep my instructions", loaded.MainPrompt);
            Assert.Equal(ApprovalMode.ReadOnly, loaded.ApprovalMode);
            Assert.Equal(revision < 3 ? null : LlmProvider.OpenRouter, loaded.WebSearchProvider);
            Assert.Equal(revision < 3 ? null : "old-search-key", loaded.WebSearchKeyId);
            Assert.Equal(revision < 3, loaded.SynGuardEnabled);
            Assert.Null(loaded.VoiceModel);
            Assert.Equal((VeniceModelCatalog.AutoId, (string?)null, 0), (loaded.SpeechModel, loaded.SpeechVoice, loaded.SpeechRate));
            Assert.Equal("medium", loaded.FastReasoning.ReasoningEffort);
            Assert.True(loaded.SynGuardReasoning.DisableThinking);
            Assert.Equal(AppSettings.CurrentFactoryModels, loaded.FactoryModelsRevision);

            loaded.RouterModelId = "my-new-router";
            loaded.RouterReasoning = new ReasoningSettings { ReasoningEffort = "medium" };
            loaded.SynGuardReasoning = new ReasoningSettings { DisableThinking = true };
            store.Save(loaded);
            var restarted = new AppSettingsStore(root).Load();
            Assert.Equal("my-new-router", restarted.RouterModelId);
            Assert.Equal("medium", restarted.RouterReasoning.ReasoningEffort);
            Assert.True(restarted.SynGuardReasoning.DisableThinking);
        }
    }

    [Theory]
    [InlineData("claude-haiku-5-5", "anthropic:claude-haiku-5-5")]
    [InlineData("claude-sonnet-5-5", "anthropic:claude-sonnet-5-5")]
    public void Anthropic_has_the_family_even_before_its_catalog(string family, string expected) =>
        Assert.Equal(expected, ModelFamily.Find(family, LlmProvider.Anthropic, catalog: null));

    [Fact]
    public void OpenRouter_spells_the_family_with_dots_and_a_vendor()
    {
        Assert.Equal("openrouter:anthropic/claude-haiku-5.5", ModelFamily.Find("claude-haiku-5-5", LlmProvider.OpenRouter, null));
        Assert.Equal("openrouter:x-ai/grok-4.7", ModelFamily.Find("grok-4-7", LlmProvider.OpenRouter, null));
    }

    /// <summary>У своего сервера и прямых провайдеров семейство находится по каталогу.</summary>
    [Fact]
    public void A_server_catalog_finds_the_family_by_its_spelling()
    {
        var server = LlmProvider.Custom("a1b2c3");
        var catalog = Catalog("custom-a1b2c3:gpt-6-sol", "custom-a1b2c3:Claude-Haiku-5.5-20260901", "custom-a1b2c3:grok-4.7");

        Assert.Equal("custom-a1b2c3:Claude-Haiku-5.5-20260901", ModelFamily.Find("claude-haiku-5-5", server, catalog));
        Assert.Equal("custom-a1b2c3:grok-4.7", ModelFamily.Find("grok-4-7", server, catalog));
        Assert.Null(ModelFamily.Find("claude-sonnet-5-5", server, catalog));
    }

    /// <summary>У DeepSeek напрямую Flash называется без снимка.</summary>
    [Fact]
    public void DeepSeek_direct_names_flash_without_a_snapshot() =>
        Assert.Equal(
            "deepseek:deepseek-flash",
            ModelFamily.Find("deepseek-v4-flash-0731", LlmProvider.DeepSeek, Catalog("deepseek:deepseek-pro", "deepseek:deepseek-flash")));

    /// <summary>Заводская модель слота находится у провайдера, а не первая попавшаяся.</summary>
    [Fact]
    public void Slot_defaults_take_the_same_family_elsewhere()
    {
        var anthropic = Catalog("anthropic:claude-opus-5-5", "anthropic:claude-sonnet-5-5", "anthropic:claude-haiku-5-5");

        Assert.Equal("anthropic:claude-haiku-5-5", ModelSlotDefaults.Resolve(LlmProvider.Anthropic, ModelSlot.Lite, anthropic));
        Assert.Equal("anthropic:claude-sonnet-5-5", ModelSlotDefaults.Resolve(LlmProvider.Anthropic, ModelSlot.Heavy, anthropic));
        Assert.Equal("anthropic:claude-haiku-5-5", ModelSlotDefaults.Resolve(LlmProvider.Anthropic, ModelSlot.Summary, anthropic));
    }

    /// <summary>
    /// Человек только с ключом Anthropic: слоты, стоящие у Venice без ключа, переезжают к Anthropic
    /// той же моделью, когда доехал его каталог.
    /// </summary>
    [Fact]
    public void Slots_of_a_keyless_provider_move_to_the_chosen_key()
    {
        var settings = AppSettings.CreateDefault();
        var anthropic = Catalog("anthropic:claude-opus-5-5", "anthropic:claude-sonnet-5-5", "anthropic:claude-haiku-5-5");

        var changed = ModelSlotHealing.Heal(
            settings, LlmProvider.Anthropic, anthropic, LlmProvider.Anthropic,
            provider => provider == LlmProvider.Anthropic, _ => true);

        Assert.True(changed);
        Assert.Equal("anthropic:claude-haiku-5-5", settings.LiteModelId);
        Assert.Equal("anthropic:claude-sonnet-5-5", settings.HeavyModelId);
        Assert.Equal("anthropic:claude-haiku-5-5", settings.SummaryModelId);
        Assert.StartsWith("anthropic:", settings.AgentHeavyModelId, StringComparison.Ordinal);
        Assert.Equal(VeniceModelCatalog.AutoId, settings.ChatModelId);
    }

    /// <summary>
    /// Слот у провайдера, ключ к которому есть, не переезжает, даже если его каталог не дошёл:
    /// отказ по стране стёр бы выбор, который сработает, как только сеть пустит.
    /// </summary>
    [Fact]
    public void Slots_of_a_provider_with_a_key_stay()
    {
        var settings = AppSettings.CreateDefault();
        var anthropic = Catalog("anthropic:claude-haiku-5-5");

        var changed = ModelSlotHealing.Heal(
            settings, LlmProvider.Anthropic, anthropic, LlmProvider.Anthropic,
            _ => true, _ => true);

        Assert.False(changed);
        Assert.Equal(AppSettings.ShippedLiteModelId, settings.LiteModelId);
    }

    /// <summary>Переезд — только к провайдеру выбранного ключа, а не к тому, чей каталог доехал первым.</summary>
    [Fact]
    public void Slots_move_only_to_the_chosen_key_provider()
    {
        var settings = AppSettings.CreateDefault();
        foreach (var (_, _, write) in ModelSlots.Service)
        {
            write(settings, "claude-haiku-5-5");
        }

        var openRouter = Catalog("openrouter:anthropic/claude-haiku-5.5", AppSettings.FactoryDecisionModelId, AppSettings.FactoryFoxModelId);

        var changed = ModelSlotHealing.Heal(
            settings, LlmProvider.OpenRouter, openRouter, LlmProvider.Anthropic,
            provider => provider != LlmProvider.Venice, _ => true);

        Assert.False(changed);
        Assert.Equal("claude-haiku-5-5", settings.LiteModelId);
    }

    /// <summary>
    /// 1.37.0-beta.8: у человека только с ключом Venice заводские модели OpenRouter переезжают к Venice:
    /// та же модель семейством, а у быстрой и обхода своя модель Venice.
    /// </summary>
    [Fact]
    public void Openrouter_defaults_move_to_venice_for_a_venice_only_key()
    {
        var settings = AppSettings.CreateDefault();
        var venice = Catalog(
            "claude-haiku-5-5", "claude-sonnet-5-5", "deepseek-v4-flash-0731", "minimax-m3-1-flash",
            "grok-4-3", "abliteration-abliterated-model-large-flash");

        var changed = ModelSlotHealing.Heal(
            settings, LlmProvider.Venice, venice, LlmProvider.Venice,
            provider => provider == LlmProvider.Venice, _ => true);

        Assert.True(changed);
        Assert.Equal("minimax-m3-1-flash", settings.FastModelId);
        Assert.Equal("minimax-m3-1-flash", settings.AgentFastModelId);
        Assert.Equal("claude-haiku-5-5", settings.LiteModelId);
        Assert.Equal("claude-sonnet-5-5", settings.HeavyModelId);
        Assert.Equal("claude-sonnet-5-5", settings.AgentHeavyModelId);
        Assert.Equal("deepseek-v4-flash-0731", settings.TitleModelId);
        Assert.Equal("claude-haiku-5-5", settings.FoxModelId);
        Assert.Equal("abliteration-abliterated-model-large-flash", settings.BypassModelId);

        // Выбранная руками модель OpenRouter едет по семейству, а не к заводской обхода.
        var chosen = AppSettings.CreateDefault();
        chosen.HeavyModelId = "openrouter:x-ai/grok-4.3";
        ModelSlotHealing.Heal(chosen, LlmProvider.Venice, venice, LlmProvider.Venice, provider => provider == LlmProvider.Venice, _ => true);
        Assert.Equal("grok-4-3", chosen.HeavyModelId);
    }
}
