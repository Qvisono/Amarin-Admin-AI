using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Хранилище ключей, когда провайдеров стало два.
/// </summary>
/// <remarks>
/// Цена ошибки здесь — ключ, отправленный не тому серверу: секрет уезжает на посторонний
/// адрес, а человек видит лишь «ключ не принят».
/// </remarks>
public sealed class ProviderKeyStoreTests : IDisposable
{
    private const string VeniceKey = "vk-venice-0123456789";
    private const string OpenRouterKey = "sk-or-v1-9876543210";
    private const string VeniceFromEnvironment = "vk-env-42424242";
    private const string OpenRouterFromEnvironment = "sk-or-env-42424242";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-provkeys-" + Guid.NewGuid().ToString("N"));

    public ProviderKeyStoreTests() => Directory.CreateDirectory(_root);

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

    private string File_ => Path.Combine(_root, "keys.json");

    private ApiKeyStore Store(string venice = "", string openRouter = "")
    {
        var store = new ApiKeyStore(_root, venice, openRouter);
        store.Load();
        return store;
    }

    /// <summary>
    /// Файл, написанный версией до 1.23.0, поля провайдера не знает. Прочитаться он обязан как
    /// список ключей Venice — каким и был: иначе у обновившегося человека ключи в один миг
    /// начали бы предъявляться чужому серверу.
    /// </summary>
    [Fact]
    public void A_file_written_before_providers_existed_reads_as_venice()
    {
        Store().Add("Рабочий", VeniceKey);

        var text = System.IO.File.ReadAllText(File_);
        var stripped = string.Join("\n", text
            .Split('\n')
            .Where(line => !line.Contains("\"provider\"", StringComparison.OrdinalIgnoreCase)));
        System.IO.File.WriteAllText(File_, stripped);

        var entry = Assert.Single(Store().List());
        Assert.Equal(LlmProvider.Venice, entry.Provider);
        Assert.Equal(VeniceKey, Store().ActiveSecret());
        Assert.Equal(LlmProvider.Venice, Store().ActiveProvider());
    }

    [Fact]
    public void The_provider_survives_a_restart()
    {
        Store().Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        var entry = Assert.Single(Store().List());
        Assert.Equal(LlmProvider.OpenRouter, entry.Provider);
        Assert.Equal(new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey), Store().ActiveCredential());
    }

    /// <summary>Секрет по-прежнему не ложится на диск открытым текстом — провайдер ничего не меняет.</summary>
    [Fact]
    public void The_file_still_never_holds_the_key_in_the_clear()
    {
        Store().Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        var text = System.IO.File.ReadAllText(File_);
        Assert.DoesNotContain(OpenRouterKey, text, StringComparison.Ordinal);
        Assert.Contains("openRouter", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Переменная окружения своя у каждого провайдера, и строка в списке тоже.</summary>
    [Fact]
    public void Both_environment_keys_get_their_own_row()
    {
        var store = Store(VeniceFromEnvironment, OpenRouterFromEnvironment);
        var rows = store.List();

        Assert.Equal(2, rows.Count);

        // Убрать можно и такую строку, но это не удаление: переменная остаётся в Windows.
        Assert.All(rows, row => Assert.True(row.RemovalOnlyHides));
        Assert.Equal("VENICE_API_KEY", rows[0].Label);
        Assert.Equal("OPENROUTER_API_KEY", rows[1].Label);
        Assert.Equal(LlmProvider.Venice, rows[0].Provider);
        Assert.Equal(LlmProvider.OpenRouter, rows[1].Provider);
    }

    /// <summary>
    /// Идентификатор строки Venice из окружения менять нельзя: на него ссылается уже записанный
    /// у людей <c>activeKeyId</c>, и другое значение сбросило бы им выбор ключа.
    /// </summary>
    [Fact]
    public void The_venice_environment_row_keeps_its_old_identifier()
    {
        Assert.Equal("environment", ApiKeyStore.EnvironmentId);
        Assert.Equal("environment", ApiKeyStore.EnvironmentIdFor(LlmProvider.Venice));
        Assert.NotEqual(ApiKeyStore.EnvironmentId, ApiKeyStore.OpenRouterEnvironmentId);
    }

    [Fact]
    public void Choosing_the_openrouter_environment_row_switches_the_provider()
    {
        var store = Store(VeniceFromEnvironment, OpenRouterFromEnvironment);

        Assert.True(store.SetActive(ApiKeyStore.OpenRouterEnvironmentId));
        Assert.Equal(
            new ApiCredential(LlmProvider.OpenRouter, OpenRouterFromEnvironment),
            Store(VeniceFromEnvironment, OpenRouterFromEnvironment).ActiveCredential());
    }

    /// <summary>Строку окружения, которой нет, активной не сделать.</summary>
    [Fact]
    public void An_environment_row_that_does_not_exist_cannot_be_chosen() =>
        Assert.False(Store(VeniceFromEnvironment).SetActive(ApiKeyStore.OpenRouterEnvironmentId));

    /// <summary>
    /// Картинки и чтение страниц умеет только Venice, а активным может быть ключ OpenRouter —
    /// тогда ключ Venice берётся из сохранённых.
    /// </summary>
    [Fact]
    public void The_venice_key_is_found_even_when_another_provider_is_active()
    {
        var store = Store();
        store.Add("Венис", VeniceKey);
        store.Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        Assert.Equal(LlmProvider.OpenRouter, store.ActiveProvider());
        Assert.Equal(new ApiCredential(LlmProvider.Venice, VeniceKey), store.VeniceCredential());
    }

    /// <summary>Ключа Venice нет вовсе — пусто, а не чужой ключ: он уехал бы не на тот сервер.</summary>
    [Fact]
    public void Without_a_venice_key_there_is_nothing_to_draw_with()
    {
        var store = Store(openRouter: OpenRouterFromEnvironment);
        store.Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        Assert.True(store.VeniceCredential().IsEmpty);
        Assert.Equal(LlmProvider.Venice, store.VeniceCredential().Provider);
    }

    /// <summary>
    /// Блоб не расшифровался — работает ключ из окружения, но обязательно того же провайдера.
    /// Подставить соседний значило бы отправить секрет на сервер, которому он не предназначен.
    /// </summary>
    [Fact]
    public void An_unreadable_blob_falls_back_within_its_own_provider()
    {
        Store().Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        var damaged = System.IO.File.ReadAllText(File_)
            .Replace("\"protected\":", "\"protected\": \"bm90IGEgZHBhcGkgYmxvYg==\", \"ignored\":",
                     StringComparison.Ordinal);
        System.IO.File.WriteAllText(File_, damaged);

        var store = Store(VeniceFromEnvironment, OpenRouterFromEnvironment);
        Assert.Equal(
            new ApiCredential(LlmProvider.OpenRouter, OpenRouterFromEnvironment),
            store.ActiveCredential());
    }

    /// <summary>
    /// Имя файла с тратами считается из ключа и менять его нельзя: другая подпись означала бы,
    /// что у человека обнулилась история трат.
    /// </summary>
    [Fact]
    public void The_spend_file_name_is_unchanged() =>
        Assert.Equal(
            "994b0b2786f3b692",
            ApiKeyStore.Fingerprint("vk-first-key-0123456789"));

    /// <summary>Отчёт об аварии вырезает ключи обоих провайдеров, а не только активного.</summary>
    [Fact]
    public void Both_providers_keys_are_cut_from_a_crash_report()
    {
        var store = Store(VeniceFromEnvironment, OpenRouterFromEnvironment);
        store.Add("Венис", VeniceKey);
        store.Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        var secrets = store.AllSecrets();
        Assert.Contains(VeniceKey, secrets);
        Assert.Contains(OpenRouterKey, secrets);
        Assert.Contains(VeniceFromEnvironment, secrets);
        Assert.Contains(OpenRouterFromEnvironment, secrets);
    }

    /// <summary>
    /// Ключ и провайдер меняются одной операцией: между «поставили ключ» и «поставили
    /// провайдера» соседний поток успел бы отправить один другому.
    /// </summary>
    [Fact]
    public void The_holder_changes_both_at_once()
    {
        var keys = new ApiKeyProvider(VeniceKey);
        var seen = new List<ApiCredential>();
        keys.Changed += credential => seen.Add(credential);

        keys.Use(new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey));

        Assert.Equal(LlmProvider.OpenRouter, keys.CurrentProvider);
        Assert.Equal(OpenRouterKey, keys.Current);
        Assert.Equal(new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey), Assert.Single(seen));
    }

    /// <summary>Тот же ключ и тот же провайдер — не смена, и будить подписчиков незачем.</summary>
    [Fact]
    public void The_same_key_raises_nothing()
    {
        var keys = new ApiKeyProvider(VeniceKey);
        var raised = 0;
        keys.Changed += _ => raised++;

        keys.Use(new ApiCredential(LlmProvider.Venice, VeniceKey));

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Ключ Venice едет вместе с активным: держатель один на программу, и второму такому же
    /// пришлось бы ходить по тем же местам.
    /// </summary>
    [Fact]
    public void The_holder_also_carries_the_venice_key()
    {
        var keys = new ApiKeyProvider();
        keys.Use(
            new ApiCredential(LlmProvider.OpenRouter, OpenRouterKey),
            new ApiCredential(LlmProvider.Venice, VeniceKey));

        Assert.Equal(LlmProvider.OpenRouter, keys.CurrentProvider);
        Assert.Equal(new ApiCredential(LlmProvider.Venice, VeniceKey), keys.VeniceCredential);
    }

    /// <summary>
    /// Ключи по-прежнему не попадают в архив данных: человек его пересылает, и провайдер
    /// этого не меняет.
    /// </summary>
    [Fact]
    public void Keys_still_stay_out_of_the_data_bundle() =>
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("keys.json"));

    /// <summary>Провайдер записывается именем, а не числом: числа переживают перестановку членов.</summary>
    [Fact]
    public void The_provider_is_written_by_name()
    {
        Store().Add("Роутер", OpenRouterKey, LlmProvider.OpenRouter);

        using var document = JsonDocument.Parse(System.IO.File.ReadAllText(File_));
        var provider = document.RootElement
            .GetProperty("keys")[0]
            .GetProperty("provider");

        Assert.Equal(JsonValueKind.String, provider.ValueKind);
        Assert.Equal("openRouter", provider.GetString());
    }
}
