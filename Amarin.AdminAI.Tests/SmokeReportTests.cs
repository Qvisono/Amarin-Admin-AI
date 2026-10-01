using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Прогон инструментов в CI (H6): ключ, пометка SKIP и отчёт для сводки шага.</summary>
public sealed class SmokeReportTests
{
    [Fact]
    public void The_report_key_turns_the_smoke_run_on_and_skips_the_lock()
    {
        var startup = StartupArgs.Parse(["--smoke-report", @"D:\a\_temp\smoke.md"]);

        Assert.True(startup.SmokeTools);
        Assert.Equal(@"D:\a\_temp\smoke.md", startup.SmokeReportPath);
        Assert.Equal(StartupRoute.SmokeTools, StartupRouter.Decide(startup, () => throw new InvalidOperationException("lock asked")));
    }

    [Fact]
    public void Without_a_path_the_key_does_nothing()
    {
        var startup = StartupArgs.Parse(["--smoke-report", " "]);

        Assert.False(startup.SmokeTools);
        Assert.Null(startup.SmokeReportPath);
    }

    [Theory]
    [InlineData("Hyper-V is not installed on this computer.", true)]
    [InlineData("Get-VM : The term 'Get-VM' is not recognized as the name of a cmdlet", true)]
    [InlineData("The Wireless AutoConfig Service (wlansvc) is not running.", true)]
    [InlineData("Компонент не установлен.", true)]
    [InlineData("failed to connect to the docker API at npipe:////./pipe/docker_engine; check if the path is correct", true)]
    [InlineData("Access is denied.", false)]
    [InlineData("", false)]
    public void Only_a_missing_component_counts_as_a_skip(string output, bool skipped) =>
        Assert.Equal(skipped, ToolSmokeRunner.IsUnavailable(output));

    [Fact]
    public void Skips_do_not_fail_the_run_but_failures_do()
    {
        ToolSmokeResult[] skipped = [new("a", true, 1, "ok"), new("b", false, 2, "not installed", Skipped: true)];
        ToolSmokeResult[] failed = [.. skipped, new("c", false, 3, "boom")];

        Assert.Equal(0, ToolSmokeRunner.ExitCode(skipped));
        Assert.Equal(1, ToolSmokeRunner.ExitCode(failed));
        Assert.Equal("Total: 1 OK, 1 SKIP, 1 FAIL of 3.", ToolSmokeRunner.Totals(failed));
    }

    [Fact]
    public void The_report_is_a_table_that_output_cannot_break()
    {
        var report = ToolSmokeRunner.FormatReport(
        [
            new ToolSmokeResult("registry", false, 12, "a | b\nc", Skipped: false),
            new ToolSmokeResult("virtualization", false, 5, "Hyper-V is not installed", Skipped: true)
        ]);

        var rows = report.Split('\n').Where(line => line.StartsWith("| ", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Contains("| FAIL | `registry` | 12 | a \\| b c |", report, StringComparison.Ordinal);
        Assert.Contains("| SKIP | `virtualization` |", report, StringComparison.Ordinal);
        Assert.Contains("Total: 0 OK, 1 SKIP, 1 FAIL of 2.", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_runs_the_smoke_test_without_letting_it_block()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".github", "workflows", "ci.yml")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var text = File.ReadAllText(Path.Combine(directory!.FullName, ".github", "workflows", "ci.yml"));
        var step = text[text.IndexOf("- name: Smoke-test tools", StringComparison.Ordinal)..];
        step = step[..step.IndexOf("- name:", 10, StringComparison.Ordinal)];

        Assert.Contains("continue-on-error: true", step, StringComparison.Ordinal);
        Assert.Contains("--smoke-report", step, StringComparison.Ordinal);
        Assert.Contains("GITHUB_STEP_SUMMARY", step, StringComparison.Ordinal);
    }
}
