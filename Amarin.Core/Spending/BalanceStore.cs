using System.Text.Json;

namespace Amarin.Core;

/// <summary>Содержимое <c>balance.json</c>: остаток по каждому ключу.</summary>
internal sealed class BalanceFile
{
    public List<KeyBalance> Keys { get; set; } = [];
}

/// <summary>
/// Хранит остатки ключей между запусками, чтобы плашке было что показать сразу.
/// </summary>
/// <remarks>
/// Venice называет остаток только на заголовках ответа, а OpenRouter — лишь на прямой вопрос;
/// без этого файла плашка стояла бы пустой от запуска до первого ответа — ровно в тот миг,
/// когда на неё чаще всего и смотрят.
/// <para>
/// Сохранённая цифра по определению устаревшая: она говорит, сколько было, когда программу
/// закрыли. Это то же запаздывание, с каким плашка живёт и между ходами, поэтому показывается
/// без оговорок и переписывается первым же настоящим ответом.
/// </para>
/// <para>
/// Ключи названы отпечатками, как и файлы трат в <c>usage/</c>: сам секрет на диск не попадает.
/// Файл прежнего формата — один остаток без имени владельца — читается как пустой: чей он был,
/// в нём не записано, и приписать его какому-то ключу значило бы выдумать.
/// </para>
/// </remarks>
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
    /// По возможности: неудачная запись стоит лишь устаревшей плашки на следующем запуске,
    /// а зовут это в конце каждого хода, где исключению совсем не место.
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
