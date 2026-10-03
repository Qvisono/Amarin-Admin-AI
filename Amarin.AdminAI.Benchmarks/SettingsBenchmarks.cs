using Amarin.Core;
using BenchmarkDotNet.Attributes;

namespace Amarin.AdminAI.Benchmarks;

/// <summary>
/// Настройки: чтение <c>settings.json</c> (его зовут на каждое обновление кольца контекста) и
/// запись (ползунок оформления пишет на каждый тик).
/// </summary>
public class SettingsBenchmarks
{
    private string _root = "";
    private (AppSettingsStore Store, AppSettings Settings)? _ready;

    private (AppSettingsStore Store, AppSettings Settings) Ready =>
        _ready ?? throw new InvalidOperationException("GlobalSetup не выполнялся.");

    [GlobalSetup]
    public void Setup()
    {
        _root = Fixtures.NewRoot();
        var store = new AppSettingsStore(_root);
        var settings = store.Load();
        store.Save(settings);
        _ready = (store, settings);
    }

    [GlobalCleanup]
    public void Cleanup() => Fixtures.Delete(_root);

    [Benchmark]
    public AppSettings Load() => Ready.Store.Load();

    [Benchmark]
    public void Save() => Ready.Store.Save(Ready.Settings);
}
