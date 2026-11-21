using System.Text.Json;

namespace Amarin.Core;

/// <summary>Содержимое <c>balance.json</c>: остаток по каждому ключу.</summary>
internal sealed class BalanceFile
{
    public List<KeyBalance> Keys { get; set; } = [];
}

/// <summary>Кэш последних известных остатков. Ключи обозначены отпечатками без секретов.</summary>
internal sealed class BalanceStore
{
    private readonly string _path;

    public BalanceStore(string? root = null) =>
        _path = root is null ? AppPaths.BalanceFile : Path.Combine(root, "balance.json");

    /// <summary>Никогда не бросает: повреждённый или пропавший кэш — просто «цифры пока нет».</summary>
    public IReadOnlyList<KeyBalance> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<BalanceFile>(
                File.ReadAllText(_path), AppJson.Options);
            return file?.Keys ?? [];
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Стирает кэш целиком — например, когда в программе не осталось ключей.</summary>
    public void Forget()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Ошибку записи можно пережить: остаток обновится после следующего ответа провайдера.
    /// </summary>
    public void Save(IReadOnlyList<KeyBalance> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        try
        {
            AppDataFile.WriteAtomic(
                _path,
                JsonSerializer.Serialize(new BalanceFile { Keys = [.. keys] }, AppJson.Options));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
