using System.Text.Json;
using System.Text.Json.Serialization;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Провайдер на диске — та же строка, что писало перечисление до 1.34.0.
/// </summary>
/// <remarks>
/// Провайдер лежит в <c>keys.json</c> и в <c>settings.json</c>. Прежняя версия, на которую
/// человек может откатиться кнопкой «Вернуть v…», читает его перечислением и, встретив
/// незнакомую строку, считает файл повреждённым: ключи и настройки пропали бы.
/// </remarks>
public sealed class ProviderJsonTests
{
    /// <summary>Копия перечисления 1.33.0 — им файлы читает прежняя версия.</summary>
    private enum LegacyProvider
    {
        Venice,
        OpenRouter
    }

    private sealed class LegacyRecord
    {
        public LegacyProvider Provider { get; set; }
    }

    private static readonly JsonSerializerOptions LegacyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static string? Field(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(name).GetString();
    }

    [Theory]
    [InlineData("venice")]
    [InlineData("openRouter")]
    public void The_old_providers_are_written_as_before(string expected)
    {
        var record = new ApiKeyRecord { Provider = LlmProvider.Parse(expected) };

        var json = JsonSerializer.Serialize(record, AppJson.Options);

        Assert.Equal(expected, Field(json, "provider"));
    }

    /// <summary>То, что пишет 1.34.0 про Venice и OpenRouter, прежняя версия читает своим перечислением.</summary>
    [Fact]
    public void The_previous_version_reads_what_this_one_writes()
    {
        var venice = JsonSerializer.Serialize(new ApiKeyRecord { Provider = LlmProvider.Venice }, AppJson.Options);
        var openRouter = JsonSerializer.Serialize(new ApiKeyRecord { Provider = LlmProvider.OpenRouter }, AppJson.Options);

        Assert.Equal(LegacyProvider.Venice, JsonSerializer.Deserialize<LegacyRecord>(venice, LegacyOptions)!.Provider);
        Assert.Equal(LegacyProvider.OpenRouter, JsonSerializer.Deserialize<LegacyRecord>(openRouter, LegacyOptions)!.Provider);
    }

    /// <summary>И наоборот: всё, что могло лежать на диске, читается.</summary>
    [Theory]
    [InlineData("\"venice\"", "venice")]
    [InlineData("\"Venice\"", "venice")]
    [InlineData("\"openRouter\"", "openRouter")]
    [InlineData("\"OPENROUTER\"", "openRouter")]
    [InlineData("0", "venice")]
    [InlineData("1", "openRouter")]
    public void Everything_the_old_files_hold_is_read(string stored, string expected)
    {
        var record = JsonSerializer.Deserialize<ApiKeyRecord>($"{{\"provider\":{stored}}}", AppJson.Options)!;

        Assert.Equal(LlmProvider.Parse(expected), record.Provider);
    }

    /// <summary>Незнакомую строку (её записала более новая версия) чтение не стирает.</summary>
    [Fact]
    public void An_unknown_provider_survives_a_round_trip()
    {
        var record = JsonSerializer.Deserialize<ApiKeyRecord>("{\"provider\":\"fromTheFuture\"}", AppJson.Options)!;

        var json = JsonSerializer.Serialize(record, AppJson.Options);

        Assert.Equal("fromTheFuture", Field(json, "provider"));
    }

    /// <summary>Пустое поле старого файла и необязательный параметр значат Venice, как у перечисления.</summary>
    [Fact]
    public void Default_is_venice()
    {
        Assert.Equal(LlmProvider.Venice, default(LlmProvider));
        Assert.Equal(LlmProvider.Venice, new ApiKeyRecord().Provider);
        Assert.Equal(LlmProvider.Venice, LlmProvider.Parse(""));
    }

    [Fact]
    public void Equality_ignores_case_and_works_as_a_key()
    {
        var set = new HashSet<LlmProvider> { LlmProvider.OpenRouter, LlmProvider.Parse("OpenRouter"), LlmProvider.Venice };

        Assert.Equal(2, set.Count);
        Assert.True(LlmProvider.Parse("OPENROUTER") == LlmProvider.OpenRouter);
    }

    /// <summary>Имя в журналах — прежнее: по нему читают журнал скорости.</summary>
    [Fact]
    public void The_name_in_logs_is_unchanged()
    {
        Assert.Equal("Venice", LlmProvider.Venice.ToString());
        Assert.Equal("OpenRouter", LlmProvider.OpenRouter.ToString());
    }

    [Fact]
    public void A_custom_server_is_its_own_provider()
    {
        var server = LlmProvider.Custom("3F2A9C");

        Assert.True(server.IsCustom);
        Assert.Equal("3f2a9c", server.CustomId);
        Assert.Equal("custom-3f2a9c", server.Id);
        Assert.NotEqual(LlmProvider.Custom("aaaaaa"), server);
        Assert.Equal(server, LlmProvider.Parse("CUSTOM-3f2a9c"));
    }

    /// <summary>Поиск в интернете в настройках — той же строкой, иначе 1.33 не прочтёт настройки целиком.</summary>
    [Fact]
    public void The_search_provider_in_settings_is_written_as_before()
    {
        var settings = new AppSettings { WebSearchProvider = LlmProvider.OpenRouter };

        var json = JsonSerializer.Serialize(settings, AppJson.Options);

        Assert.Equal("openRouter", Field(json, "webSearchProvider"));
    }
}
