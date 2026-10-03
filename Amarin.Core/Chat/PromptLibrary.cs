using System.Text.Json;

namespace Amarin.Core;

/// <summary>Одна сохранённая заготовка основного промпта.</summary>
public sealed class PromptPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Text { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Библиотека заготовок основного промпта: человек держит несколько ролей модели и переключает
/// их одним нажатием, вместо того чтобы хранить тексты где-то на стороне и вставлять руками.
/// </summary>
/// <remarks>
/// Свой файл, а не поле в <c>settings.json</c>: тексты промптов длинные, а настройки
/// переписываются на каждое переключение тумблера — вся библиотека уходила бы на диск вместе
/// с ними. Устройство списано с <see cref="BalanceStore"/>: ни <see cref="Load"/>, ни
/// <see cref="Save"/> не бросают, и оба ходят через <see cref="AppJson.Options"/> и
/// <see cref="AppDataFile.WriteAtomic"/>.
/// <para>
/// Файл лежит в папке профиля, рядом с <c>settings.json</c>: библиотека промптов — вещь личная,
/// как и сам основной промпт, который она подменяет.
/// </para>
/// </remarks>
internal sealed class PromptLibrary
{
    /// <summary>Длиннее человек всё равно не прочитает в плитке, а файл раздувается.</summary>
    public const int NameLimit = 40;

    private readonly string _path;

    public PromptLibrary(string? root = null) =>
        _path = Path.Combine(root ?? AppPaths.Root, "prompts.json");

    /// <summary>Никогда не бросает: повреждённый или отсутствующий файл — это пустая библиотека.</summary>
    public List<PromptPreset> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var presets = JsonSerializer.Deserialize<List<PromptPreset>>(
                File.ReadAllText(_path), AppJson.Options);
            if (presets is null)
            {
                return [];
            }

            // Запись без имени или без текста показать нечем, а удалить её человеку было бы
            // нечем тоже: плитка без подписи не нажимается осмысленно.
            presets.RemoveAll(preset =>
                string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrWhiteSpace(preset.Text));
            foreach (var preset in presets)
            {
                if (string.IsNullOrWhiteSpace(preset.Id))
                {
                    preset.Id = Guid.NewGuid().ToString("N");
                }
            }

            return presets;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Best-effort, как и у остатка на счету: не записавшаяся библиотека — досадная потеря
    /// одной заготовки, а не повод показывать человеку окно аварии посреди настроек.
    /// </summary>
    public void Save(IReadOnlyList<PromptPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        try
        {
            AppDataFile.WriteAtomic(_path, JsonSerializer.Serialize(presets, AppJson.Options));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>Подпись под названием плитки: начало текста одной строкой.</summary>
    public static string Preview(string text, int limit = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        // Переносы в одну строку: плитка фиксированной высоты, и перевод строки внутри текста
        // вытолкнул бы название соседней плитки за её край.
        var flat = string.Join(" ", text.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= limit ? flat : flat[..limit].TrimEnd() + "…";
    }

    public static string TrimName(string? name) =>
        (name ?? "").Trim() is var trimmed && trimmed.Length > NameLimit
            ? trimmed[..NameLimit]
            : trimmed;
}
