using System.Text;
using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Живой прогон чтений инструментов Windows 1.37.0 на этой машине: что они отвечают модели на
/// самом деле, целиком, а не первой сотней знаков, как в <c>--smoke-report</c>.
/// </summary>
/// <remarks>
/// Запуск: <c>AMARIN_WINDOWS_TOOLS_LIVE=&lt;файл отчёта&gt;</c>. Без переменной тест сразу
/// выходит. Только чтения: система не меняется, но запись нагрузки и замер связи идут минуту.
/// </remarks>
public sealed class WindowsToolsLiveTests
{
    private static readonly (ITool Tool, object Arguments)[] Reads =
    [
        (new AudioTool(), new { action = "devices" }),
        (new AudioTool(), new { action = "sessions" }),
        (new PowerTool(), new { action = "schemes" }),
        (new PowerTool(), new { action = "requests" }),
        (new PowerTool(), new { action = "wake" }),
        (new PowerTool(), new { action = "battery" }),
        (new FileLocksTool(), new { action = "find", path = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) }),
        (new PermissionsTool(), new { action = "show", path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }),
        (new PermissionsTool(), new { action = "explain", path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }),
        (new EnvironmentTool(), new { action = "list" }),
        (new EnvironmentTool(), new { action = "path_check" }),
        (new StoreAppsTool(), new { action = "list", max_items = 40 }),
        (new PrintQueueTool(), new { action = "list" }),
        (new PrintQueueTool(), new { action = "jobs" }),
        (new SharesTool(), new { action = "status" }),
        (new SharesTool(), new { action = "shares" }),
        (new SharesTool(), new { action = "drives" }),
        (new SharesTool(), new { action = "check_host", host = "localhost" }),
        (new BluetoothTool(), new { action = "status" }),
        (new BluetoothTool(), new { action = "devices" }),
        (new PerfRecordTool(), new { minutes = 1 }),
        (new NetworkQualityTool(), new { seconds = 30 }),
        (new StartupProgramsTool(), new { action = "audit" })
    ];

    [Fact]
    public async Task Every_read_answers_on_this_machine()
    {
        var output = Environment.GetEnvironmentVariable("AMARIN_WINDOWS_TOOLS_LIVE");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var report = new StringBuilder();
        var failed = new List<string>();
        foreach (var (tool, arguments) in Reads)
        {
            var json = JsonSerializer.Serialize(arguments);
            var result = await tool.ExecuteAsync(JsonSerializer.SerializeToElement(arguments));
            report.AppendLine($"## {tool.Name} {json} {(result.Success ? "OK" : "FAIL")}").AppendLine("```").AppendLine(result.Output).AppendLine("```").AppendLine();
            if (!result.Success)
            {
                failed.Add($"{tool.Name} {json}");
            }
        }

        // Отказ — тоже ответ (нет прав, нет адаптера), поэтому тест не падает, а пишет отчёт с
        // отказавшими в начале: разбирает их человек по тексту.
        var head = failed.Count == 0 ? "All reads answered.\n\n" : $"Refused or failed: {string.Join(", ", failed)}\n\n";
        await File.WriteAllTextAsync(output, head + report);
    }
}
