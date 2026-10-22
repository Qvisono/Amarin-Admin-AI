using System.Runtime.InteropServices;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Кто держит файл: Restart Manager на файле, который держит сам тест, и отказы закрытия. Чужие
/// программы тест не закрывает.
/// </summary>
public sealed class FileLocksToolTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "amarin-locks-" + Guid.NewGuid().ToString("N"));

    public FileLocksToolTests() => Directory.CreateDirectory(Path.Combine(_temp, "sub"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string Run(object arguments, bool success = true)
    {
        var result = new FileLocksTool().ExecuteAsync(Args(arguments)).GetAwaiter().GetResult();
        Assert.True(result.Success == success, result.Output);
        return result.Output;
    }

    [Fact]
    public void The_holder_of_a_file_and_of_a_file_inside_a_folder_is_found()
    {
        var file = Path.Combine(_temp, "sub", "held.txt");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Contains($"PID {Environment.ProcessId}", Run(new { action = "find", path = file }), StringComparison.Ordinal);
        Assert.Contains($"PID {Environment.ProcessId}", Run(new { action = "find", path = _temp }), StringComparison.Ordinal);
    }

    [Fact]
    public void A_free_file_has_no_holders()
    {
        var file = Path.Combine(_temp, "free.txt");
        File.WriteAllText(file, "x");

        Assert.Contains("No process holds it", Run(new { action = "find", path = file }), StringComparison.Ordinal);
    }

    [Fact]
    public void This_program_is_never_closed_even_when_it_holds_the_file()
    {
        var file = Path.Combine(_temp, "mine.txt");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Contains("ЗАПРЕЩЕНО", Run(new { action = "close", path = file, pid = Environment.ProcessId }, success: false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_pid_that_does_not_hold_the_path_closes_nothing()
    {
        var file = Path.Combine(_temp, "free.txt");
        File.WriteAllText(file, "x");

        Assert.Contains("не держит", Run(new { action = "close", path = file, pid = 999_999 }, success: false), StringComparison.Ordinal);
    }

    [Fact]
    public void Relative_and_missing_paths_are_refused()
    {
        Run(new { action = "find", path = "relative.txt" }, success: false);
        Run(new { action = "find", path = Path.Combine(_temp, "missing.txt") }, success: false);
    }

    [Fact]
    public void The_process_info_layout_matches_the_windows_structure()
    {
        // RM_PROCESS_INFO: PID и время запуска (12), имя (256 знаков), служба (64 знака), четыре поля по 4.
        Assert.Equal(12 + 512 + 128 + 16, Marshal.SizeOf<RestartManager.RmProcessInfo>());
    }
}
