using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Amarin.AdminAI.Benchmarks;

/// <summary>
/// Точка входа замеров. В том же процессе (InProcess): отдельный проект под каждый замер
/// BenchmarkDotNet собирал бы для net10.0-windows, а на Linux это требует обходов, без которых
/// сборка падает.
/// </summary>
/// <remarks>
/// <c>dotnet run -c Release --project Amarin.AdminAI.Benchmarks -- --filter '*'</c> — все замеры;
/// <c>--filter '*Streaming*'</c> — один класс; <c>--quick</c> — по одному прогону, чтобы проверить,
/// что замеры работают. «До» и «после» сравнивают на одной машине подряд: прогон из git worktree
/// прежнего коммита и из нынешнего.
/// </remarks>
public static class Program
{
    public static void Main(string[] args)
    {
        // --quick — короткий прогон для проверки, что замеры вообще работают.
        var quick = args.Contains("--quick");
        var rest = args.Where(arg => arg != "--quick").ToArray();

        var job = (quick ? Job.Dry : Job.Default)
            .WithToolchain(InProcessEmitToolchain.Instance)
            .WithWarmupCount(quick ? 1 : 3)
            .WithIterationCount(quick ? 1 : 12);

        var config = ManualConfig.Create(DefaultConfig.Instance)
            .AddJob(job)
            .AddDiagnoser(MemoryDiagnoser.Default)
            .WithOptions(ConfigOptions.JoinSummary);

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(rest, config);
    }
}
