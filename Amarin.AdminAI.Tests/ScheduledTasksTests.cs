using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// <c>scheduled_task list/query</c> читают Планировщик через COM (1.37.0): строки в Unicode, а не в
/// кодировке консоли, где кириллица в путях становилась «?????». Только чтение: тест задач не
/// создаёт и не меняет.
/// </summary>
public sealed class ScheduledTasksTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public async Task List_reads_the_tasks_of_this_machine_in_one_english_table()
    {
        var all = ScheduledTasks.All();
        Assert.NotEmpty(all);
        Assert.All(all, task => Assert.StartsWith(@"\", task.Path, StringComparison.Ordinal));

        var result = await new ScheduledTaskTool().ExecuteAsync(Args(new { action = "list" }));

        Assert.True(result.Success, result.Output);
        Assert.StartsWith(all.Count + " tasks. path | state | next run | last run | last result | runs", result.Output,
            StringComparison.Ordinal);
        Assert.Contains(all[0].Path, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_finds_a_task_by_path_or_by_name_at_the_root()
    {
        var task = ScheduledTasks.All().First();

        var byPath = await new ScheduledTaskTool().ExecuteAsync(Args(new { action = "query", task_name = task.Path }));

        Assert.True(byPath.Success, byPath.Output);
        Assert.StartsWith("Task: " + task.Path, byPath.Output, StringComparison.Ordinal);
        Assert.Contains("Actions:", byPath.Output, StringComparison.Ordinal);
        Assert.Equal(task.Path, ScheduledTasks.Find(task.Path.TrimStart('\\'))?.Path);
    }

    [Fact]
    public async Task An_unknown_task_is_a_plain_answer_that_points_to_list()
    {
        var name = "AmarinNoSuchTask-" + Guid.NewGuid().ToString("N");

        var result = await new ScheduledTaskTool().ExecuteAsync(Args(new { action = "query", task_name = name }));

        Assert.False(result.Success);
        Assert.Equal(Loc.Format("S.Tool.Task.NotFound", name), result.Output);
    }

    [Fact]
    public void Cyrillic_survives_formatting_and_a_task_that_never_ran_has_no_result()
    {
        var task = new ScheduledTaskInfo(
            @"\Задачи\Проверка", true, "ready", null, null, 0, "Иван", null, "Иван", false,
            ["at logon"], [new ScheduledTaskAction("exec", @"C:\Users\Иван\скрипт.exe", "/тихо")]);

        var list = ScheduledTasks.FormatList([task]);
        var one = ScheduledTasks.FormatOne(task);

        Assert.Contains(@"\Задачи\Проверка | ready | - | - | - | C:\Users\Иван\скрипт.exe /тихо", list, StringComparison.Ordinal);
        Assert.Contains("Run as: Иван", one, StringComparison.Ordinal);
        Assert.Contains("- at logon", one, StringComparison.Ordinal);
        Assert.Equal("No scheduled tasks.", ScheduledTasks.FormatList([]));
    }
}
