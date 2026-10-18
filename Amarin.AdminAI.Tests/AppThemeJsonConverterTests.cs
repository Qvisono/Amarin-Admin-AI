using System.Text.Json;
using System.Text.Json.Serialization;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Тема в <c>settings.json</c> (1.36.0): старые темы — именем, новые — числом, чтобы откат на 1.35
/// не сбрасывал все настройки из-за незнакомого имени.
/// </summary>
public sealed class AppThemeJsonConverterTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "amarin-theme-json-" + Guid.NewGuid().ToString("N"));

    public AppThemeJsonConverterTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Old_themes_keep_their_names_and_new_ones_are_numbers()
    {
        var nord = JsonSerializer.Serialize(new AppSettings { Theme = AppTheme.Nord }, AppJson.Options);
        var edge = JsonSerializer.Serialize(new AppSettings { Theme = AppTheme.EdgeBlue }, AppJson.Options);
        var onyx = JsonSerializer.Serialize(new AppSettings { Theme = AppTheme.Onyx }, AppJson.Options);

        Assert.Contains("\"theme\": \"nord\"", nord);
        Assert.Contains("\"theme\": \"edgeBlue\"", edge);
        Assert.Contains("\"theme\": " + (int)AppTheme.Onyx, onyx);
    }

    [Theory]
    [InlineData("\"nord\"", AppTheme.Nord)]
    [InlineData("\"Nord\"", AppTheme.Nord)]
    [InlineData("\"matteLight\"", AppTheme.MatteLight)]
    [InlineData("55", AppTheme.Carbon)]
    [InlineData("\"themeFromTheFuture\"", AppTheme.Dark)]
    public void Reading_takes_names_and_numbers_and_never_throws(string stored, AppTheme expected)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(
            "{ \"theme\": " + stored + ", \"uiScalePercent\": 120 }", AppJson.Options)!;

        Assert.Equal(expected, settings.Theme);

        // Незнакомая тема не роняет чтение: остальные настройки на месте.
        Assert.Equal(120, settings.UiScalePercent);
    }

    /// <summary>
    /// Так читает 1.35: своим перечислением через <c>JsonStringEnumConverter</c>. Незнакомое имя
    /// бросает — ради этого конвертер и пишет новые темы числом, а число проходит.
    /// </summary>
    [Fact]
    public void The_previous_version_reads_the_number_and_would_choke_on_the_name()
    {
        var legacy = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var written = JsonSerializer.Serialize(new AppSettings { Theme = AppTheme.Carbon }, AppJson.Options);

        var read = JsonSerializer.Deserialize<LegacySettings>(written, legacy)!;

        Assert.Equal((int)AppTheme.Carbon, (int)read.Theme);
        _ = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LegacySettings>("{ \"theme\": \"carbon\" }", legacy));
    }

    [Fact]
    public void A_new_theme_survives_the_store_and_the_data_archive()
    {
        var source = Path.Combine(_work, "source");
        var target = Path.Combine(_work, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        new AppSettingsStore(source).Save(new AppSettings { Theme = AppTheme.Onyx, UiScalePercent = 115 });
        AppSettingsStore.FlushAll();
        Assert.Equal(AppTheme.Onyx, new AppSettingsStore(source).Load().Theme);

        var archive = Path.Combine(_work, "bundle" + DataBundle.FileExtension);
        new DataBundleExporter(source, null).Write(archive, DataCategory.Settings);
        new DataBundleImporter(target).Apply(archive, DataCategory.Settings, DataImportMode.Replace);

        var imported = new AppSettingsStore(target).Load();
        Assert.Equal(AppTheme.Onyx, imported.Theme);
        Assert.Equal(115, imported.UiScalePercent);
    }

    /// <summary>Перечисление тем 1.35 — без Onyx и Carbon.</summary>
    private enum LegacyTheme
    {
        System,
        Dark
    }

    private sealed class LegacySettings
    {
        public LegacyTheme Theme { get; set; }
    }
}
