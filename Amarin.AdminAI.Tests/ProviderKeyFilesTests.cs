using System.Text.Json;
using System.Text.Json.Serialization;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ключи новых провайдеров и свои серверы — в отдельном файле, чтобы откат на 1.33 их не терял.
/// </summary>
/// <remarks>
/// 1.33 читает провайдера ключа перечислением и, встретив незнакомое имя, сочла бы весь
/// <c>keys.json</c> повреждённым: ключи Venice и OpenRouter пропали бы вместе с новыми.
/// </remarks>
public sealed class ProviderKeyFilesTests : IDisposable
{
    private const string VeniceKey = "vk-venice-0123456789";
    private const string OpenAiKey = "sk-proj-0123456789abcdef";
    private const string StarKey = "sk-star-0123456789abcdef";
    private const string StarKey2 = "sk-star-fedcba9876543210";

    private static readonly ProviderEndpoint Star = new("ai.starimg.ru", "https://ai.starimg.ru/v1", OpenAiChat: true, AnthropicMessages: true);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-provfiles-" + Guid.NewGuid().ToString("N"));

    public ProviderKeyFilesTests() => Directory.CreateDirectory(_root);

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

    private ApiKeyStore Store()
    {
        var store = new ApiKeyStore(_root);
        store.Load();
        return store;
    }

    /// <summary>Копия перечисления 1.33.0 и записи ключа — так файл читает прежняя версия.</summary>
    private enum LegacyProvider
    {
        Venice,
        OpenRouter
    }

    private sealed class LegacyRecord
    {
        public LegacyProvider Provider { get; set; }
    }

    private sealed class LegacyFile
    {
        public List<LegacyRecord> Keys { get; set; } = [];
    }

    private static readonly JsonSerializerOptions LegacyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void A_new_provider_key_never_reaches_the_old_file()
    {
        var store = Store();
        store.Add("Рабочий", VeniceKey);
        store.Add("OpenAI", OpenAiKey, LlmProvider.OpenAI);

        var legacy = JsonSerializer.Deserialize<LegacyFile>(
            File.ReadAllText(Path.Combine(_root, "keys.json")),
            LegacyOptions);

        Assert.Equal([LegacyProvider.Venice], legacy!.Keys.Select(key => key.Provider));
        Assert.True(File.Exists(Path.Combine(_root, ApiKeyStore.ProviderKeysFileName)));
    }

    [Fact]
    public void Both_files_read_back_as_one_list()
    {
        Store().Add("Рабочий", VeniceKey);
        Store().Add("OpenAI", OpenAiKey, LlmProvider.OpenAI);

        var keys = Store().List();

        Assert.Equal([LlmProvider.Venice, LlmProvider.OpenAI], keys.Select(key => key.Provider));
        Assert.True(Store().Contains(OpenAiKey));
    }

    /// <summary>У человека без новых провайдеров новый файл не появляется.</summary>
    [Fact]
    public void Nothing_new_appears_for_the_old_providers_alone()
    {
        Store().Add("Рабочий", VeniceKey);

        Assert.False(File.Exists(Path.Combine(_root, ApiKeyStore.ProviderKeysFileName)));
    }

    /// <summary>Два ключа одного сервера — один провайдер, а не два одинаковых столбца.</summary>
    [Fact]
    public void Two_keys_of_one_server_share_it()
    {
        var store = Store();
        var first = store.AddCustom("Пакет 10М", StarKey, Star, tokenLimit: 10_000_000);
        var second = store.AddCustom("Пакет 5М", StarKey2, Star with { Name = "" });

        Assert.Equal(first!.Provider, second!.Provider);
        Assert.Single(store.Endpoints);

        var entries = Store().List();
        Assert.All(entries, entry => Assert.Equal(Star, entry.Endpoint));
        Assert.Equal(10_000_000, entries[0].TokenLimit);
        Assert.Equal("ai.starimg.ru", entries[0].ProviderName);
    }

    /// <summary>Адрес сервера едет вместе с ключом до самого запроса.</summary>
    [Fact]
    public void The_server_address_rides_with_the_key()
    {
        var store = Store();
        var record = store.AddCustom("Star", StarKey, Star)!;

        var keys = new ApiKeyProvider();
        keys.Use(store.ActiveCredential(), null, store.Handles());
        var credential = keys.CredentialFor(record.Provider.Id + ":claude-sonnet-5-5", null);

        Assert.Equal(StarKey, credential.Secret);
        Assert.Equal(Star, credential.Endpoint);
        Assert.Equal("https://ai.starimg.ru/v1", ProviderSpec.Of(credential).BaseUrl);
        Assert.Contains(keys.Providers, spec => spec.Provider == record.Provider && spec.Name == "ai.starimg.ru");
    }

    /// <summary>Последний ключ сервера уносит и сервер: пустой столбец в плашке ни к чему.</summary>
    [Fact]
    public void Removing_the_last_key_removes_the_server()
    {
        var store = Store();
        var record = store.AddCustom("Star", StarKey, Star)!;

        Assert.True(store.Remove(record.Id));
        Assert.Empty(Store().Endpoints);
    }

    [Fact]
    public void A_token_limit_survives_a_restart_and_can_be_cleared()
    {
        var record = Store().AddCustom("Star", StarKey, Star)!;

        Assert.True(Store().SetTokenLimit(record.Id, 10_000_000));
        Assert.Equal(10_000_000, Store().List().Single().TokenLimit);

        Assert.True(Store().SetTokenLimit(record.Id, 0));
        Assert.Null(Store().List().Single().TokenLimit);
    }

    /// <summary>Ключ своего сервера, отправленный в другой профиль, едет вместе с адресом.</summary>
    [Fact]
    public void Sending_a_server_key_takes_the_server_along()
    {
        Store().AddCustom("Star", StarKey, Star, tokenLimit: 1_000);
        var target = Path.Combine(_root, "other");
        Directory.CreateDirectory(target);

        var result = ApiKeyTransfer.Send(Store().List().Single(), target, "", "");

        Assert.Equal(KeyTransferResult.Added, result);
        var there = new ApiKeyStore(target);
        there.Load();
        var key = there.List().Single();
        Assert.Equal(Star, key.Endpoint);
        Assert.Equal(1_000, key.TokenLimit);
    }

    [Fact]
    public void The_new_file_never_travels_and_is_wiped()
    {
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf(ApiKeyStore.ProviderKeysFileName));
        Assert.Contains(ApiKeyStore.ProviderKeysFileName, ProfileDataWiper.DefaultProfileFiles);
    }

    [Theory]
    [InlineData("https://ai.starimg.ru/", "https://ai.starimg.ru")]
    [InlineData("ai.starimg.ru", "https://ai.starimg.ru")]
    [InlineData("https://ai.starimg.ru/v1?x=1", "https://ai.starimg.ru/v1")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1")]
    public void An_address_is_tidied(string typed, string expected) =>
        Assert.Equal(expected, EndpointAddress.Normalize(typed, out _));

    /// <summary>Ключ по открытому каналу к чужому серверу читается любым посредником.</summary>
    [Theory]
    [InlineData("http://ai.starimg.ru", "insecure")]
    [InlineData("ftp://ai.starimg.ru", "invalid")]
    [InlineData("  ", "empty")]
    public void A_risky_or_broken_address_is_refused(string typed, string problem)
    {
        Assert.Null(EndpointAddress.Normalize(typed, out var reason));
        Assert.Equal(problem, reason);
    }

    [Fact]
    public void Claude_takes_the_native_protocol_when_the_server_has_both()
    {
        Assert.True(Star.UsesAnthropicFor("claude-sonnet-5-5"));
        Assert.False(Star.UsesAnthropicFor("gpt-6-sol"));
        Assert.True((Star with { OpenAiChat = false }).UsesAnthropicFor("gpt-6-sol"));
        Assert.False((Star with { AnthropicMessages = false }).UsesAnthropicFor("claude-sonnet-5-5"));
    }
}
