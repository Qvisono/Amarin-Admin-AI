using System.Text.Json;

namespace Amarin.Core;

/// <summary>
/// Keeps the last balance Venice reported so the plate has something to show at startup.
/// Venice only sends the figure on response headers, so without this the plate would sit
/// empty from launch until the first answer came back — the one moment the user is most
/// likely to be checking whether there is money left to spend.
/// </summary>
/// <remarks>
/// The cached figure is by definition stale: it says what was left when the app last ran.
/// That is the same kind of lag the plate already carries between turns, so it is shown
/// without a disclaimer and overwritten by the first real response.
/// </remarks>
internal sealed class BalanceStore
{
    private readonly string _path;

    public BalanceStore(string? root = null) =>
        _path = root is null ? AppPaths.BalanceFile : Path.Combine(root, "balance.json");

    /// <summary>Never throws: a damaged or missing cache simply means no figure yet.</summary>
    public VeniceBalance? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var balance = JsonSerializer.Deserialize<VeniceBalance>(
                File.ReadAllText(_path), AppJson.Options);
            return balance is { Usd: null, Diem: null } ? null : balance;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Стирает кэш: остаток принадлежал прежнему ключу, и на следующем запуске он соврал бы.
    /// </summary>
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
    /// Best-effort: a failed write costs nothing but a stale plate on the next launch, and
    /// this runs at the end of every turn where an exception would be badly out of place.
    /// </summary>
    public void Save(VeniceBalance balance)
    {
        ArgumentNullException.ThrowIfNull(balance);
        try
        {
            AppDataFile.WriteAtomic(_path, JsonSerializer.Serialize(balance, AppJson.Options));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
