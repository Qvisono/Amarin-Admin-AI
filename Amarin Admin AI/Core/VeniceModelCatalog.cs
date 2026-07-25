namespace Amarin.Core;

internal sealed record ModelTierGroup(string Title, string Subtitle, string[] Models);

internal static class VeniceModelCatalog
{
    /// <summary>
    /// Preset models for /model, grouped by reliability / cost tier.
    /// </summary>
    public static readonly ModelTierGroup[] Tiers =
    [
        new(
            "Флагманский уровень",
            "макс. надёжность",
            ["claude-sonnet-5", "grok-4-5"]),
        new(
            "Средний уровень",
            "цена/качество",
            ["openai-gpt-53-codex", "kimi-k2-7-code"]),
        new(
            "Бюджетный уровень",
            "для массовых/простых задач",
            ["minimax-m3-preview", "qwen-3-7-plus"])
    ];

    public static readonly string[] PresetModels =
        Tiers.SelectMany(t => t.Models).ToArray();

    private static readonly string[] ExcludedModels =
    [
        "grok-41-fast"
    ];

    public static IReadOnlyList<string> GetSelectableModels(string currentModel)
    {
        var models = new List<string>();

        if (!string.IsNullOrWhiteSpace(currentModel) &&
            !IsExcluded(currentModel) &&
            !models.Contains(currentModel, StringComparer.OrdinalIgnoreCase) &&
            !PresetModels.Contains(currentModel, StringComparer.OrdinalIgnoreCase))
        {
            models.Add(currentModel);
        }

        foreach (var preset in PresetModels)
        {
            if (!models.Contains(preset, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(preset);
            }
        }

        return models;
    }

    private static bool IsExcluded(string model) =>
        ExcludedModels.Contains(model, StringComparer.OrdinalIgnoreCase);
}
