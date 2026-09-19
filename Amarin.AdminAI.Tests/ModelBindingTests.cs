using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Привязка слота: модель и ключ, которым за неё платят.
/// </summary>
/// <remarks>
/// До версии 1.23.0 ключ был один на программу, и он же задавал провайдера всем девяти слотам.
/// Здесь проверяется обратное: слот помнит свой ключ, а исчезнувший ключ не ломает слот,
/// а возвращает его к выбору по умолчанию.
/// </remarks>
public sealed class ModelSlotKeyTests
{
    [Fact]
    public void A_slot_remembers_its_key()
    {
        var settings = AppSettings.CreateDefault();
        ModelSlots.WriteBinding(
            settings,
            ModelSlot.Title,
            new ModelBinding("openrouter:deepseek/deepseek-chat", "key-a"));

        var binding = ModelSlots.Binding(settings, ModelSlot.Title);
        Assert.Equal("openrouter:deepseek/deepseek-chat", binding.ModelId);
        Assert.Equal("key-a", binding.KeyId);
        Assert.Equal(LlmProvider.OpenRouter, binding.Provider);
    }

    /// <summary>
    /// Слоты независимы: в этом весь смысл правки — заголовки у одного провайдера, разговор
    /// у другого.
    /// </summary>
    [Fact]
    public void Slots_do_not_share_a_provider()
    {
        var settings = AppSettings.CreateDefault();
        ModelSlots.WriteBinding(settings, ModelSlot.Title, new ModelBinding("openrouter:x/y", "or"));
        ModelSlots.WriteBinding(settings, ModelSlot.Chat, new ModelBinding("grok-4-6", "ven"));

        Assert.Equal(LlmProvider.OpenRouter, ModelSlots.Binding(settings, ModelSlot.Title).Provider);
        Assert.Equal(LlmProvider.Venice, ModelSlots.Binding(settings, ModelSlot.Chat).Provider);
    }

    /// <summary>
    /// «Ключа нет» и «ключ по умолчанию» — одно состояние. Два способа его записать однажды
    /// сравнили бы между собой и ошиблись.
    /// </summary>
    [Fact]
    public void Clearing_a_key_leaves_no_empty_record()
    {
        var settings = AppSettings.CreateDefault();
        ModelSlots.WriteKey(settings, ModelSlot.Lite, "key-a");
        ModelSlots.WriteKey(settings, ModelSlot.Lite, "");

        Assert.Null(ModelSlots.ReadKey(settings, ModelSlot.Lite));
        Assert.Null(settings.ModelKeys);
    }

    /// <summary>Настройки прежних версий открываются без миграции: ключей в них просто нет.</summary>
    [Fact]
    public void Settings_without_keys_resolve_to_the_default()
    {
        var settings = AppSettings.CreateDefault();
        Assert.Null(settings.ModelKeys);
        Assert.All(
            ModelSlots.All,
            slot => Assert.Null(ModelSlots.ReadKey(settings, slot.Slot)));
    }
}

/// <summary>Разрешение ключа под модель: назначенный, иначе по умолчанию для провайдера.</summary>
public sealed class ApiKeyProviderBindingTests
{
    private static ApiKeyProvider Build()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            [
                new KeyHandle("v1", LlmProvider.Venice, "ven-1"),
                new KeyHandle("v2", LlmProvider.Venice, "ven-2"),
                new KeyHandle("o1", LlmProvider.OpenRouter, "or-1")
            ]);
        return keys;
    }

    /// <summary>
    /// Модель OpenRouter уходит с ключом OpenRouter, пока выбран ключ Venice. Именно этого
    /// и нельзя было сделать до 1.23.0.
    /// </summary>
    [Fact]
    public void A_foreign_provider_model_gets_its_own_key()
    {
        var credential = Build().CredentialFor("openrouter:openai/gpt-5", null);
        Assert.Equal(LlmProvider.OpenRouter, credential.Provider);
        Assert.Equal("or-1", credential.Secret);
    }

    [Fact]
    public void A_named_key_wins_over_the_default()
    {
        var credential = Build().CredentialFor("grok-4-6", "v2");
        Assert.Equal("ven-2", credential.Secret);
    }

    /// <summary>
    /// Ключ чужого провайдера не принимается даже по имени: это ровно та ошибка, от которой
    /// защищает <see cref="ApiCredential"/>, — секрет одного сервера, отправленный другому.
    /// </summary>
    [Fact]
    public void A_key_of_another_provider_is_refused()
    {
        var credential = Build().CredentialFor("grok-4-6", "o1");
        Assert.Equal(LlmProvider.Venice, credential.Provider);
        Assert.Equal("ven-1", credential.Secret);
    }

    /// <summary>Удалённый ключ не ломает слот, а возвращает его к выбору по умолчанию.</summary>
    [Fact]
    public void A_vanished_key_falls_back_to_the_default()
    {
        var credential = Build().CredentialFor("grok-4-6", "deleted");
        Assert.Equal("ven-1", credential.Secret);
    }

    /// <summary>
    /// У провайдера без ключа учётные данные пустые: отказ понятной строкой выдаёт клиент,
    /// который один знает, что человек пытался сделать.
    /// </summary>
    [Fact]
    public void A_provider_without_a_key_yields_nothing()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            [new KeyHandle("v1", LlmProvider.Venice, "ven-1")]);

        Assert.True(keys.CredentialFor("openrouter:openai/gpt-5", null).IsEmpty);
        Assert.False(keys.HasKeyFor(LlmProvider.OpenRouter));
        Assert.True(keys.HasKeyFor(LlmProvider.Venice));
    }

    /// <summary>Список ключей меняется тем же присваиванием, что и выбранный, — одним чтением.</summary>
    [Fact]
    public void The_key_list_changes_with_the_selected_key()
    {
        var keys = Build();
        var seen = 0;
        keys.Changed += _ => seen++;

        keys.Use(
            new ApiCredential(LlmProvider.OpenRouter, "or-1"),
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            [new KeyHandle("o1", LlmProvider.OpenRouter, "or-1")]);

        Assert.Equal(1, seen);
        Assert.Equal(LlmProvider.OpenRouter, keys.CurrentProvider);
        Assert.Single(keys.Keys);
        Assert.True(keys.CredentialFor("grok-4-6", "v2").IsEmpty);
    }

    /// <summary>Пустая смена не будит подписчиков: на них висят остаток и список моделей.</summary>
    [Fact]
    public void An_unchanged_set_stays_quiet()
    {
        var keys = Build();
        var seen = 0;
        keys.Changed += _ => seen++;

        keys.Use(
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            new ApiCredential(LlmProvider.Venice, "ven-1"),
            [
                new KeyHandle("v1", LlmProvider.Venice, "ven-1"),
                new KeyHandle("v2", LlmProvider.Venice, "ven-2"),
                new KeyHandle("o1", LlmProvider.OpenRouter, "or-1")
            ]);

        Assert.Equal(0, seen);
    }
}
