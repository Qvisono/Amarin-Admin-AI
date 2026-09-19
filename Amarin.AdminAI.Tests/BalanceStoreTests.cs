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
        store.Save([new KeyBalance("abc123", 12.34m, 5m, DateTime.UtcNow)]);

        var loaded = Assert.Single(store.Load());

        Assert.Equal("abc123", loaded.Fingerprint);
        Assert.Equal(12.34m, loaded.Usd);
        Assert.Equal(5m, loaded.Diem);
    }

    /// <summary>Ключей несколько — значит и остатков столько же, каждый со своим отпечатком.</summary>
    [Fact]
    public void Every_key_keeps_its_own_figure()
    {
        var store = new BalanceStore(_root);
        store.Save(
        [
            new KeyBalance("aaa", 1m, null, DateTime.UtcNow),
            new KeyBalance("bbb", 2m, null, DateTime.UtcNow)
        ]);

        var loaded = store.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal(3m, loaded.Sum(row => row.Usd ?? 0m));
    }

    /// <summary>Сам секрет на диск не попадает — только его отпечаток.</summary>
    [Fact]
    public void The_file_holds_no_secret()
    {
        var store = new BalanceStore(_root);
        store.Save([new KeyBalance(ApiKeyStore.Fingerprint("vk-super-secret"), 1m, null, DateTime.UtcNow)]);

        var text = File.ReadAllText(Path.Combine(_root, "balance.json"));

        Assert.DoesNotContain("vk-super-secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_file_yet_is_not_an_error() => Assert.Empty(new BalanceStore(_root).Load());

    [Fact]
    public void A_damaged_cache_reads_as_no_cache()
    {
        File.WriteAllText(Path.Combine(_root, "balance.json"), "{ not json");

        Assert.Empty(new BalanceStore(_root).Load());
    }

    /// <summary>
    /// Файл прежнего формата — один остаток без имени владельца — читается как пустой: чей он
    /// был, в нём не записано, и приписать его какому-то ключу значило бы выдумать.
    /// </summary>
    [Fact]
    public void A_file_from_before_several_keys_reads_as_empty()
    {
        File.WriteAllText(
            Path.Combine(_root, "balance.json"),
            """{"canConsume":true,"usd":12.34,"diem":5}""");

        Assert.Empty(new BalanceStore(_root).Load());
    }
}
