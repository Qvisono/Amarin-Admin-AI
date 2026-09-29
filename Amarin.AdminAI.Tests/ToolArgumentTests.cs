using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Командные строки внешних программ Windows, которые собирают инструменты: откат задач
/// планировщика, создание задачи, потолки долгих операций.
/// </summary>
public sealed class ToolArgumentTests
{
    [Theory]
    [InlineData(false, "/enable")]
    [InlineData(true, "/disable")]
    public void Rolling_a_task_back_uses_the_change_command(bool disable, string key)
    {
        // «schtasks /enable /tn …» schtasks отвергал как неверный синтаксис: /enable и /disable —
        // ключи команды /change. Откат задач поэтому не срабатывал ни разу.
        Assert.Equal(
            ["/change", "/tn", @"\Microsoft\Windows\Defrag\ScheduledDefrag", key],
            ChangeRollbackStore.TaskToggleArguments(@"\Microsoft\Windows\Defrag\ScheduledDefrag", disable));
    }

    [Fact]
    public void A_long_repair_gets_the_time_it_asks_for()
    {
        // DISM просил 900 секунд, а общий потолок молча урезал их до 600.
        Assert.Equal(900, PowerShellProcessRunner.EffectiveTimeout(900, longOperation: true));
        Assert.Equal(
            SystemRepairTool.DismSeconds,
            PowerShellProcessRunner.EffectiveTimeout(SystemRepairTool.DismSeconds, longOperation: true));
        Assert.Equal(
            SystemRepairTool.SfcSeconds,
            PowerShellProcessRunner.EffectiveTimeout(SystemRepairTool.SfcSeconds, longOperation: true));
        Assert.Equal(
            DiskManagementTool.ChkdskSeconds,
            PowerShellProcessRunner.EffectiveTimeout(DiskManagementTool.ChkdskSeconds, longOperation: true));
    }

    [Fact]
    public void An_ordinary_command_keeps_its_ten_minute_ceiling()
    {
        Assert.Equal(600, PowerShellProcessRunner.EffectiveTimeout(900, longOperation: false));
        Assert.Equal(
            PowerShellProcessRunner.LongOperationMaxSeconds,
            PowerShellProcessRunner.EffectiveTimeout(int.MaxValue, longOperation: true));
        Assert.Equal(5, PowerShellProcessRunner.EffectiveTimeout(0, longOperation: true));
    }

    [Theory]
    [InlineData("daily /ru SYSTEM")]
    [InlineData("daily\" /ru \"SYSTEM")]
    [InlineData("onevent")]
    [InlineData("DAILY /RL HIGHEST")]
    public void Keys_smuggled_into_the_schedule_are_refused(string schedule)
    {
        Assert.False(ScheduledTaskTool.TryBuildCreateArguments(
            "Напоминание", "notepad.exe", schedule, null, out _, out var error));
        Assert.Contains("schedule", error, StringComparison.Ordinal);
        Assert.Contains("не повторяй", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Напоминание\" /ru \"SYSTEM")]
    [InlineData("\"Напоминание\"")]
    [InlineData("имя\nс переводом")]
    public void Quotes_in_the_task_name_are_refused(string name)
    {
        Assert.False(ScheduledTaskTool.TryBuildCreateArguments(
            name, "notepad.exe", "daily", null, out _, out var error));
        Assert.Contains("task_name", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_with_a_quoted_path_stays_one_argument()
    {
        const string command = "\"C:\\Program Files\\App\\app.exe\" --quiet /ru SYSTEM";

        Assert.True(ScheduledTaskTool.TryBuildCreateArguments(
            "Напоминание", command, "Weekly", "09:30", out var arguments, out _));

        // Всё, что модель написала в command, — одно значение /tr: приписанное «/ru SYSTEM»
        // до schtasks ключом не доходит.
        Assert.Equal(
            ["/create", "/tn", "Напоминание", "/tr", command, "/sc", "WEEKLY", "/st", "09:30", "/f"],
            arguments);
    }

    [Fact]
    public void A_command_split_into_lines_is_refused()
    {
        Assert.False(ScheduledTaskTool.TryBuildCreateArguments(
            "Напоминание", "notepad.exe\r\ncalc.exe", "daily", null, out _, out var error));
        Assert.Contains("command", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("once", null, false)]
    [InlineData("once", "25:00", false)]
    [InlineData("once", "9:30", false)]
    [InlineData("once", "21:05", true)]
    [InlineData("onlogon", null, true)]
    public void A_one_time_task_needs_a_valid_start_time(string schedule, string? time, bool valid)
    {
        Assert.Equal(valid, ScheduledTaskTool.TryBuildCreateArguments(
            "Напоминание", "notepad.exe", schedule, time, out _, out _));
    }

    [Fact]
    public void The_schedule_defaults_to_daily()
    {
        Assert.True(ScheduledTaskTool.TryBuildCreateArguments(
            "Напоминание", "notepad.exe", null, null, out var arguments, out _));
        Assert.Contains("DAILY", arguments);
    }
}
