using System.Collections.Frozen;
using System.Text.Json;

namespace Amarin.Core;

/// <summary>Общий справочник логотипов: автор модели и её семейство, независимо от транспорта.</summary>
/// <remarks>
/// Справочник вшит в сборку: показ списка не требует сети и не читает пользовательские файлы.
/// Его же читает оснастка обновления картинок, чтобы проверка покрытия совпадала с интерфейсом.
/// </remarks>
internal static class ModelLogoCatalog
{
    private const string ResourceName = "Amarin.ModelLogoCatalog.json";
    private static readonly Lazy<Catalog> Data = new(Load);

    /// <summary>Ключ WPF-ресурса или <see langword="null"/>, если автор и семейство неизвестны.</summary>
    public static string? GetResourceKey(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var id = ModelRef.Bare(modelId).Trim().ToLowerInvariant();
        if (id == "auto")
        {
            return "Auto";
        }

        // Подвижное имя и варианты меняют маршрутизацию, но не владельца логотипа.
        id = id.TrimStart('~');
        var variant = id.IndexOf(':', StringComparison.Ordinal);
        if (variant >= 0)
        {
            id = id[..variant];
        }

        var catalog = Data.Value;
        if (catalog.Models.TryGetValue(id, out var exactKey))
        {
            return exactKey;
        }

        var slash = id.IndexOf('/');
        if (slash >= 0)
        {
            var author = id[..slash];
            var name = id[(slash + 1)..];
            foreach (var family in catalog.Families)
            {
                if (author.Equals(family.Author, StringComparison.Ordinal) &&
                    name.Contains(family.Contains, StringComparison.Ordinal))
                {
                    return family.Key;
                }
            }

            // Производная llama/gpt чужого автора не должна выдавать себя за Meta/OpenAI.
            return catalog.Authors.GetValueOrDefault(author);
        }

        foreach (var family in catalog.Unqualified)
        {
            if (id.Contains(family.Contains, StringComparison.Ordinal))
            {
                return family.Key;
            }
        }

        return null;
    }

    private static Catalog Load()
    {
        using var stream = typeof(ModelLogoCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("В сборке отсутствует справочник логотипов моделей.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new InvalidOperationException("Неизвестная версия справочника логотипов моделей.");
        }

        var families = root.GetProperty("families").EnumerateArray()
            .Select(item => new FamilyRule(Text(item, "author"), Text(item, "contains"), Text(item, "key")))
            .ToArray();
        var unqualified = root.GetProperty("unqualified").EnumerateArray()
            .Select(item => new UnqualifiedRule(Text(item, "contains"), Text(item, "key")))
            .ToArray();

        return new Catalog(ReadMap(root.GetProperty("authors")), ReadMap(root.GetProperty("models")),
            families, unqualified);
    }

    private static FrozenDictionary<string, string> ReadMap(JsonElement element) =>
        element.EnumerateObject().ToFrozenDictionary(property => property.Name,
            property => property.Value.GetString()
                ?? throw new InvalidOperationException("В справочнике отсутствует ключ логотипа модели."),
            StringComparer.Ordinal);

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new InvalidOperationException("В справочнике отсутствует правило логотипа модели.");

    private sealed record Catalog(
        FrozenDictionary<string, string> Authors,
        FrozenDictionary<string, string> Models,
        FamilyRule[] Families,
        UnqualifiedRule[] Unqualified);

    private sealed record FamilyRule(string Author, string Contains, string Key);
    private sealed record UnqualifiedRule(string Contains, string Key);
}
