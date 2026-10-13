using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ядро лисёнка Розы (1.35.0): слот модели, настройки и своя строка в тратах.
/// </summary>
public sealed class FoxCoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-fox-core-" + Guid.NewGuid().ToString("N"));

    public FoxCoreTests() => Directory.CreateDirectory(_root);

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

    /// <summary>
    /// Имена слотов лежат ключами в <c>settings.json</c>, а числа — нигде, но порядок тоже не
    /// трогаем: новый слот в конце не сдвигает ни одного прежнего.
    /// </summary>
    [Fact]
    public void The_fox_slot_is_appended_after_every_existing_one()
    {
        var slots = Enum.GetValues<ModelSlot>();
        Assert.Equal(ModelSlot.Fox, slots[^1]);
        Assert.Equal(9, (int)ModelSlot.Summary);
        Assert.Contains(ModelSlots.All, entry => entry.Slot == ModelSlot.Fox);
    }

    [Fact]
    public void The_fox_slot_reads_and_writes_its_model_and_key()
    {
        var settings = AppSettings.CreateDefault();
        ModelSlots.WriteBinding(settings, ModelSlot.Fox, new ModelBinding("openrouter:google/gemini-2.5-flash", "key-o"));

        Assert.Equal("openrouter:google/gemini-2.5-flash", settings.FoxModelId);
        Assert.Equal(new ModelBinding("openrouter:google/gemini-2.5-flash", "key-o"), ModelSlots.Binding(settings, ModelSlot.Fox));
    }

    /// <summary>
    /// Файл прежней версии ничего о Розе не знает: он получает заводского Розу — включённого,
    /// маленького, с лёгкой моделью без размышления, — и никаких переносов.
    /// </summary>
    [Fact]
    public void Settings_of_an_older_version_get_the_factory_fox()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{ "ChatModelId": "glm-5", "FactoryModelsRevision": 1 }""");

        var settings = new AppSettingsStore(_root).Load();

        Assert.True(settings.Fox.Enabled);
        Assert.Equal(FoxSettings.MediumSize, settings.Fox.Size);
        Assert.True(settings.Fox.Mischief);
        Assert.True(settings.Fox.ReadChat);
        Assert.True(settings.Fox.WebSearch);
        Assert.Equal(AppSettings.FactoryLiteModelId, settings.FoxModelId);
        Assert.True(settings.FoxReasoning.DisableThinking);
    }

    /// <summary>Пустые объекты в руками поправленном файле не роняют чтение: Роза заводской.</summary>
    [Fact]
    public void Null_fox_sections_are_healed_on_load()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), """{ "Fox": null, "FoxReasoning": null, "FactoryModelsRevision": 1 }""");

        var settings = new AppSettingsStore(_root).Load();

        Assert.NotNull(settings.Fox);
        Assert.NotNull(settings.FoxReasoning);
        Assert.True(settings.FoxReasoning.DisableThinking);
    }

    [Theory]
    [InlineData(48, 48)]
    [InlineData(72, 72)]
    [InlineData(96, 96)]
    [InlineData(0, 48)]
    [InlineData(60, 48)]
    [InlineData(70, 72)]
    [InlineData(80, 72)]
    [InlineData(85, 96)]
    [InlineData(500, 96)]
    public void A_hand_edited_size_snaps_to_a_supported_one(int written, int expected)
    {
        var fox = new FoxSettings { Size = written };

        Assert.Equal(written != expected, fox.Normalize());
        Assert.Equal(expected, fox.Size);
    }

    /// <summary>Роза зовут часто: пропавшую модель слота заменяет дешёвая, а не флагман.</summary>
    [Fact]
    public void The_fox_slot_falls_back_to_a_cheap_model()
    {
        IReadOnlyList<VeniceModelInfo> catalog =
        [
            new() { Id = "openrouter:anthropic/claude-sonnet-4.5" },
            new() { Id = "openrouter:google/gemini-2.5-flash" }
        ];

        Assert.Equal("openrouter:google/gemini-2.5-flash", ModelSlotDefaults.Resolve(LlmProvider.OpenRouter, ModelSlot.Fox, catalog));
    }

    /// <summary>Траты Розы — своя строка «Роза», а не «прочее» и не строка модели.</summary>
    [Fact]
    public void Fox_requests_get_their_own_spending_line()
    {
        var identity = VeniceSku.Resolve(VeniceSku.Fox, known: null);

        Assert.Null(identity.ModelId);
        Assert.Equal("S.Spend.Kind.Fox", identity.KindKey);
    }
}
