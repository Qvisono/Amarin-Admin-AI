using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The cache exists so the plate is not blank at startup. Everything worth pinning here is a
/// failure mode: a missing file, a damaged one, or a figure that came back changed.
/// </summary>
public sealed class BalanceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "amarin-balance-" + Guid.NewGuid().ToString("N"));

    public BalanceStoreTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void A_saved_balance_comes_back_intact()
    {
        var store = new BalanceStore(_root);
        store.Save(new VeniceBalance { CanConsume = true, Usd = 12.34m, Diem = 5m });

        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(12.34m, loaded!.Usd);
        Assert.Equal(5m, loaded.Diem);
        Assert.True(loaded.CanConsume);
    }

    [Fact]
    public void No_file_yet_is_not_an_error() => Assert.Null(new BalanceStore(_root).Load());

    [Fact]
    public void A_damaged_cache_reads_as_no_cache()
    {
        File.WriteAllText(Path.Combine(_root, "balance.json"), "{ not json");

        Assert.Null(new BalanceStore(_root).Load());
    }

    [Fact]
    public void A_cache_without_any_figure_reads_as_no_cache()
    {
        // Venice omits the headers on some responses; a record that carries neither currency
        // would put the plate back to a dash, which is worse than showing the last known figure.
        var store = new BalanceStore(_root);
        store.Save(new VeniceBalance { CanConsume = true });

        Assert.Null(store.Load());
    }
}
