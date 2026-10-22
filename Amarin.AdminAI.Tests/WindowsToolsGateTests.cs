using System.Diagnostics;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Инструменты Windows 1.37.0 в общей обвязке: что чтение, что спрашивается, что попадает в снимок,
/// что запрещено до вопроса, видит ли их модель в промпте и покрывает ли их одобренный план.
/// Систему тест не меняет: проверяются таблицы и шлюз.
/// </summary>
public sealed class WindowsToolsGateTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    [Theory]
    [InlineData("audio", "set_default")]
    [InlineData("audio", "set_volume")]
    [InlineData("audio", "set_mute")]
    [InlineData("audio", "restart_services")]
    [InlineData("power", "set_scheme")]
    [InlineData("file_locks", "close")]
    [InlineData("permissions", "set_owner")]
    [InlineData("permissions", "grant")]
    [InlineData("permissions", "revoke")]
    [InlineData("permissions", "reset")]
    [InlineData("environment", "set")]
    [InlineData("environment", "delete")]
    [InlineData("environment", "path_add")]
    [InlineData("environment", "path_remove")]
    [InlineData("environment", "path_clean")]
    [InlineData("store_apps", "remove")]
    [InlineData("store_apps", "reset")]
    [InlineData("print_queue", "cancel_job")]
    [InlineData("print_queue", "clear_queue")]
    [InlineData("print_queue", "restart_spooler")]
    [InlineData("print_queue", "set_default")]
    [InlineData("shares", "set_private")]
    [InlineData("shares", "enable_discovery")]
    [InlineData("shares", "enable_file_sharing")]
    [InlineData("shares", "map_drive")]
    [InlineData("shares", "unmap_drive")]
    [InlineData("bluetooth", "radio_on")]
    [InlineData("bluetooth", "radio_off")]
    [InlineData("bluetooth", "forget")]
    [InlineData("audio", "brand_new_action")]
    public void Every_changing_action_is_a_write_and_is_asked_about(string tool, string action)
    {
        // Запись, которую забыли внести в вопросы, в обычном режиме без SynGuard шла бы молча;
        // незнакомое действие — тоже запись.
        var arguments = Args(new { action });

        Assert.Equal(ToolEffect.Write, ToolEffects.Classify(tool, arguments));
        Assert.True(DangerousActionGuard.RequiresConfirmation(tool, arguments), $"{tool}/{action} идёт без вопроса");
    }

    [Theory]
    [InlineData("audio", "devices")]
    [InlineData("audio", "sessions")]
    [InlineData("power", "schemes")]
    [InlineData("power", "requests")]
    [InlineData("power", "wake")]
    [InlineData("power", "battery")]
    [InlineData("file_locks", "find")]
    [InlineData("permissions", "show")]
    [InlineData("permissions", "explain")]
    [InlineData("environment", "list")]
    [InlineData("environment", "get")]
    [InlineData("environment", "path_check")]
    [InlineData("store_apps", "list")]
    [InlineData("print_queue", "list")]
    [InlineData("print_queue", "jobs")]
    [InlineData("shares", "status")]
    [InlineData("shares", "shares")]
    [InlineData("shares", "drives")]
    [InlineData("shares", "check_host")]
    [InlineData("bluetooth", "status")]
    [InlineData("bluetooth", "devices")]
    public void Reading_actions_stay_reads_without_a_question(string tool, string action)
    {
        // Иначе в «только чтение» и в прогонах по расписанию диагностика перестала бы работать.
        var arguments = Args(new { action });

        Assert.Equal(ToolEffect.Read, ToolEffects.Classify(tool, arguments));
        Assert.False(DangerousActionGuard.RequiresConfirmation(tool, arguments));
        Assert.Null(ToolGate.Check(tool, arguments, new AppSettings { ApprovalMode = ApprovalMode.ReadOnly }).Refusal);
    }

    [Theory]
    [InlineData("perf_record")]
    [InlineData("network_quality")]
    public void Recordings_are_reads_whatever_their_length(string tool)
    {
        var arguments = Args(new { minutes = 30, seconds = 120 });

        Assert.Equal(ToolEffect.Read, ToolEffects.Classify(tool, arguments));
        Assert.Null(ToolGate.Check(tool, arguments, new AppSettings { ApprovalMode = ApprovalMode.ReadOnly }).Refusal);
    }

    [Theory]
    [InlineData("permissions", "grant", true)]
    [InlineData("permissions", "set_owner", true)]
    [InlineData("permissions", "show", false)]
    [InlineData("environment", "path_add", true)]
    [InlineData("environment", "set", true)]
    [InlineData("environment", "list", false)]
    [InlineData("audio", "set_default", false)]
    [InlineData("power", "set_scheme", false)]
    [InlineData("shares", "enable_discovery", false)]
    [InlineData("bluetooth", "radio_off", false)]
    public void Only_permissions_and_environment_take_a_rollback_snapshot(string tool, string action, bool snapshot) =>
        Assert.Equal(snapshot, DangerousActionGuard.RequiresUndoSnapshot(tool, Args(new { action })));

    [Theory]
    [InlineData("audio")]
    [InlineData("power")]
    [InlineData("file_locks")]
    [InlineData("permissions")]
    [InlineData("environment")]
    [InlineData("store_apps")]
    [InlineData("print_queue")]
    [InlineData("shares")]
    [InlineData("bluetooth")]
    public void Changing_tools_get_the_explanation_parameter(string tool)
    {
        var schema = ToolRegistry.EnrichSchemaWithExplanation(tool, JsonSchema.Parse("""{"type":"object","properties":{}}"""));

        Assert.True(schema.GetProperty("properties").TryGetProperty("explanation", out _));
    }

    [Fact]
    public void Permission_changes_on_system_places_are_refused_before_any_question()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var path in new[] { windows, Path.Combine(windows, "System32"), Path.GetPathRoot(windows)!, @"HKLM\SOFTWARE", @"HKLM\SAM\SAM" })
        {
            var check = ToolGate.Check("permissions", Args(new { action = "set_owner", path }), new AppSettings());

            Assert.NotNull(check.Refusal);
            Assert.Null(check.Question);
        }

        // Посмотреть права системной папки можно всегда.
        Assert.Null(ToolGate.Check("permissions", Args(new { action = "show", path = windows }), new AppSettings()).Refusal);
    }

    [Theory]
    [InlineData("set", "ComSpec")]
    [InlineData("delete", "SystemRoot")]
    [InlineData("set", "Path")]
    [InlineData("delete", "PATH")]
    public void System_variables_and_the_whole_path_are_refused_before_any_question(string action, string name)
    {
        var check = ToolGate.Check("environment", Args(new { action, name, value = "x" }), new AppSettings());

        Assert.NotNull(check.Refusal);
        Assert.Null(check.Question);
    }

    [Fact]
    public void Removing_a_windows_folder_from_path_is_refused_but_another_folder_is_asked_about()
    {
        var system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");

        Assert.NotNull(ToolGate.Check("environment", Args(new { action = "path_remove", path = system32 }), new AppSettings()).Refusal);
        Assert.NotNull(ToolGate.Check("environment", Args(new { action = "path_remove", path = @"%SystemRoot%\system32" }), new AppSettings()).Refusal);

        var other = ToolGate.Check("environment", Args(new { action = "path_remove", path = @"D:\Tools\bin" }), new AppSettings());
        Assert.Null(other.Refusal);
        Assert.NotNull(other.Question);
        Assert.True(other.NeedsSnapshot);
    }

    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller_1.0.0.0_x64__8wekyb3d8bbwe")]
    public void The_store_and_its_installer_are_never_removed(string name)
    {
        var check = ToolGate.Check("store_apps", Args(new { action = "remove", name }), new AppSettings());

        Assert.NotNull(check.Refusal);
        Assert.Null(check.Question);

        // Сбросить Store можно: это обычная починка.
        Assert.Null(ToolGate.Check("store_apps", Args(new { action = "reset", name }), new AppSettings()).Refusal);
    }

    [Fact]
    public void This_program_and_the_system_process_are_never_closed_as_file_holders()
    {
        foreach (var pid in new[] { Environment.ProcessId, 4 })
        {
            var check = ToolGate.Check("file_locks", Args(new { action = "close", path = @"C:\x.txt", pid }), new AppSettings());

            Assert.NotNull(check.Refusal);
            Assert.Null(check.Question);
        }
    }

    [Fact]
    public void A_remote_target_refuses_the_new_tools()
    {
        // Они работают через API этого ПК: на удалённой машине ответ пришёл бы отсюда.
        using var target = ExecutionTarget.Push(new RemoteMachine { Name = "far", Address = "far.example" });

        Assert.NotNull(ToolGate.Check("audio", Args(new { action = "devices" }), new AppSettings()).Refusal);
        Assert.NotNull(ToolGate.Check("perf_record", Args(new { minutes = 1 }), new AppSettings()).Refusal);
    }

    [Fact]
    public void The_confirmation_names_what_changes()
    {
        var grant = DangerousActionGuard.DescribeDetailed("permissions",
            Args(new { action = "grant", path = @"D:\Data", principal = "users", rights = "modify" }));
        Assert.Contains("users", grant.ChangeSummary, StringComparison.Ordinal);
        Assert.Contains(@"D:\Data", grant.ChangeSummary, StringComparison.Ordinal);
        Assert.Equal(DangerousRiskLevel.High, grant.RiskLevel);

        var close = DangerousActionGuard.DescribeDetailed("file_locks", Args(new { action = "close", path = @"D:\a.docx", pid = 1234, force = true }));
        Assert.Contains("1234", close.ChangeSummary, StringComparison.Ordinal);
        Assert.Equal(DangerousRiskLevel.High, close.RiskLevel);

        var volume = DangerousActionGuard.DescribeDetailed("audio", Args(new { action = "set_volume", volume = 40, app = "player" }));
        Assert.Contains("40", volume.ChangeSummary, StringComparison.Ordinal);
        Assert.Contains("player", volume.ChangeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_agent_tool_is_named_in_the_agent_prompt()
    {
        // Промпт разводит задачи по инструментам: инструмент, которого в нём нет, модель берёт реже
        // и наугад.
        var options = new AgentOptions { ApiKey = "test", BaseUrl = "https://api.venice.ai/api/v1", Model = "grok-4-6" };
        using var http = new HttpClient { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        using var download = new HttpClient();
        var registry = AgentTools.Create(new VeniceClient(http, options), download, new DownloadOptions());

        Assert.All(registry.All, tool => Assert.Contains(tool.Name, Agent.BaseSystemPrompt, StringComparison.Ordinal));
    }

    [Fact]
    public void The_shipped_1_36_prompt_is_kept_so_an_untouched_saved_copy_is_reset()
    {
        Assert.DoesNotContain("perf_record", LegacyAgentPrompts.All[^1], StringComparison.Ordinal);
        Assert.Contains("local_users", LegacyAgentPrompts.All[^1], StringComparison.Ordinal);

        var settings = new AppSettings { TechAgentPrompt = LegacyAgentPrompts.All[^1].ReplaceLineEndings("\r\n") };
        Assert.True(AppSettingsStore.MigrateLegacyChatPrompts(settings));
        Assert.Equal("", settings.TechAgentPrompt);
    }

    [Theory]
    [InlineData("print_queue", "set_default", "printer", "Office")]
    [InlineData("power", "set_scheme", "scheme", "Balanced")]
    [InlineData("audio", "set_default", "device", "Speakers")]
    [InlineData("shares", "unmap_drive", "letter", "Z")]
    public void An_approved_plan_step_covers_the_call_by_its_target(string tool, string action, string field, string target)
    {
        var step = new PlanStep("step", tool, action, target, null, ChangesSystem: true);
        var arguments = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["action"] = action, [field] = target });
        var other = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["action"] = action, [field] = target + "2" });

        Assert.True(AgentPlans.Covers(step, tool, arguments));
        Assert.False(AgentPlans.Covers(step, tool, other));
    }

    [Fact]
    public void A_rollback_plan_line_names_the_object_whose_permissions_return()
    {
        var line = UndoCommands.Describe(new UndoStep { Kind = UndoKind.Acl, Target = @"D:\Data", Values = ["3", "tree"] });

        Assert.Contains(@"D:\Data", line, StringComparison.Ordinal);
        Assert.Contains("3", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_new_tools_have_parameterless_constructors_and_no_side_effects_in_them()
    {
        // Таблица ToolEffects сверяется с инструментами, созданными без аргументов (ToolGateTests).
        var watch = Stopwatch.StartNew();
        ITool[] tools =
        [
            new AudioTool(), new PowerTool(), new FileLocksTool(), new PermissionsTool(), new EnvironmentTool(),
            new StoreAppsTool(), new PrintQueueTool(), new SharesTool(), new BluetoothTool(), new PerfRecordTool(),
            new NetworkQualityTool()
        ];

        Assert.All(tools, tool => Assert.Contains(tool.Name, ToolCatalog.Agent));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "конструкторы инструментов не должны ничего делать");
    }
}
