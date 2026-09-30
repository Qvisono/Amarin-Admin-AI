using System.Net;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// План перед выполнением (C1). Главное обещание — одобренный план не превращается в «разрешить
/// всё»: без вопроса проходит только вызов, совпавший с шагом, а вопрос SynGuard задаётся всегда.
/// </summary>
public sealed class AgentPlanTests
{
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static AgentPlan Plan(params PlanStep[] steps) => new() { Summary = "s", Steps = [.. steps] };

    // ───────────────────────── разбор ─────────────────────────

    [Fact]
    public void A_plan_is_read_from_the_submitted_arguments()
    {
        var plan = AgentPlans.Parse(Args(new
        {
            summary = "Почистить временные файлы",
            steps = new object[]
            {
                new { description = "Посмотреть объём", tool = "disk_space", changes_system = false },
                new { description = "Удалить", tool = "filesystem", action = "DELETE", target = "C:\\Temp\\x", changes_system = true },
                new { description = "без инструмента" },
                "мусор"
            }
        }));

        Assert.NotNull(plan);
        Assert.Equal("Почистить временные файлы", plan.Summary);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("delete", plan.Steps[1].Action);
        Assert.True(plan.Steps[1].ChangesSystem);
        Assert.Equal(1, plan.ChangingSteps);
    }

    [Fact]
    public void A_plan_without_steps_is_not_a_plan()
    {
        Assert.Null(AgentPlans.Parse(Args(new { summary = "x", steps = Array.Empty<object>() })));
        Assert.Null(AgentPlans.Parse(Args(new { summary = "x" })));
        Assert.Null(AgentPlans.Parse(default));
    }

    [Fact]
    public void A_very_long_plan_is_cut_to_what_a_person_can_read()
    {
        var steps = Enumerable.Range(0, 100).Select(i => new { description = "d" + i, tool = "t", changes_system = false });

        Assert.Equal(AgentPlans.MaxSteps, AgentPlans.Parse(Args(new { summary = "x", steps }))!.Steps.Count);
    }

    // ───────────────────────── что покрывает шаг ─────────────────────────

    [Fact]
    public void A_step_covers_its_own_target_whatever_the_spelling()
    {
        var plan = Plan(new PlanStep("Записать", "write_file", Target: "C:\\Temp\\A.txt", ChangesSystem: true));

        Assert.True(AgentPlans.Covers(plan, "write_file", Args(new { path = "c:\\temp\\a.txt", content = "x" })));
        Assert.True(AgentPlans.Covers(plan, "WRITE_FILE", Args(new { path = "\"C:\\Temp\\A.txt\"" })));
    }

    [Fact]
    public void A_step_does_not_cover_another_target_or_tool_or_action()
    {
        var plan = Plan(new PlanStep("Остановить", "windows_service", "stop", "Spooler", ChangesSystem: true));

        Assert.False(AgentPlans.Covers(plan, "windows_service", Args(new { action = "stop", service_name = "WinDefend" })));
        Assert.False(AgentPlans.Covers(plan, "windows_service", Args(new { action = "set_start_type", service_name = "Spooler" })));
        Assert.False(AgentPlans.Covers(plan, "process", Args(new { action = "stop", name = "Spooler" })));
        Assert.True(AgentPlans.Covers(plan, "windows_service", Args(new { action = "stop", service_name = "spooler" })));
    }

    [Fact]
    public void A_step_without_a_target_covers_only_a_call_that_has_none()
    {
        // Иначе «registry write» без пути разрешал бы запись в любой раздел.
        var plan = Plan(new PlanStep("Сбросить кэш", "network", "flush_dns"), new PlanStep("Записать", "registry", "write"));

        Assert.True(AgentPlans.Covers(plan, "network", Args(new { action = "flush_dns" })));
        Assert.False(AgentPlans.Covers(plan, "registry", Args(new { action = "write", path = "HKLM\\SOFTWARE\\X" })));
    }

    [Fact]
    public void A_script_is_matched_by_its_text_not_by_its_line_breaks()
    {
        var plan = Plan(new PlanStep("Скрипт", "run_powershell", Script: "Stop-Service Spooler\n  Start-Service Spooler", ChangesSystem: true));

        Assert.True(AgentPlans.Covers(plan, "run_powershell", Args(new { command = "Stop-Service  Spooler\r\nStart-Service Spooler " })));
        Assert.False(AgentPlans.Covers(plan, "run_powershell", Args(new { command = "Stop-Service Spooler; Remove-Item C:\\ -Recurse" })));
        Assert.False(AgentPlans.Covers(Plan(new PlanStep("Без скрипта", "run_powershell")), "run_powershell", Args(new { command = "Get-Date" })));
    }

    // ───────────────────────── настройки ─────────────────────────

    [Fact]
    public void Without_the_setting_only_heavy_tasks_are_planned()
    {
        var settings = new AppSettings();

        Assert.True(AgentPlanSettings.IsOn(settings, "heavy"));
        Assert.False(AgentPlanSettings.IsOn(settings, "lite"));
        Assert.False(AgentPlanSettings.IsOn(settings, "fast"));
    }

    [Fact]
    public void Tiers_are_switched_one_by_one_and_kept_in_their_order()
    {
        var settings = new AppSettings();

        AgentPlanSettings.Set(settings, "lite", true);
        AgentPlanSettings.Set(settings, "heavy", false);
        AgentPlanSettings.Set(settings, "fast", true);

        Assert.Equal(["lite", "fast"], settings.PlanFirstTiers);
        Assert.False(AgentPlanSettings.IsOn(settings, "heavy"));
    }

    [Fact]
    public void An_empty_list_means_no_plan_anywhere_and_unknown_tiers_are_ignored()
    {
        Assert.Empty(AgentPlanSettings.Tiers(new AppSettings { PlanFirstTiers = [] }));
        Assert.Equal(["fast"], AgentPlanSettings.Tiers(new AppSettings { PlanFirstTiers = ["ultra", "fast"] }));
    }

    [Fact]
    public void Settings_without_the_field_read_as_the_default()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}", AppJson.Options)!;

        Assert.Null(settings.PlanFirstTiers);
        Assert.True(AgentPlanSettings.IsOn(settings, "heavy"));
    }

    // ───────────────────────── очередь ─────────────────────────

    [Fact]
    public async Task Stopping_one_chat_drops_only_its_plans()
    {
        var queue = new PlanReviewQueue();
        var mine = queue.ReviewAsync("A", Plan(new PlanStep("d", "t")), "chat-1", CancellationToken.None);
        var other = queue.ReviewAsync("B", Plan(new PlanStep("d", "t")), "chat-2", CancellationToken.None);

        queue.CancelForSession("chat-1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mine);
        Assert.False(other.IsCompleted);
        Assert.Equal(1, queue.Count);
        Assert.True(queue.TryPeek(out var head));
        Assert.Equal("B", head.AgentLabel);

        queue.Complete(head, PlanDecision.Execute);
        Assert.Equal(PlanVerdict.Execute, (await other).Verdict);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task A_stopped_turn_takes_its_plan_off_the_screen()
    {
        var queue = new PlanReviewQueue();
        var changes = 0;
        queue.Changed += () => changes++;
        using var cts = new CancellationTokenSource();
        var review = queue.ReviewAsync("A", Plan(new PlanStep("d", "t")), "chat-1", cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => review);
        Assert.Equal(0, queue.Count);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task An_answer_to_a_plan_already_gone_changes_nothing()
    {
        var queue = new PlanReviewQueue();
        var first = queue.ReviewAsync("A", Plan(new PlanStep("d", "t")), "chat-1", CancellationToken.None);
        queue.TryPeek(out var shown);
        queue.CancelForSession("chat-1");

        queue.Complete(shown, PlanDecision.Execute);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    // ───────────────────────── фаза плана у агента ─────────────────────────

    [Fact]
    public async Task Approved_steps_run_without_a_question_and_the_rest_is_asked()
    {
        var ran = new List<string>();
        var ui = new PlanUi(PlanDecision.Execute);
        var agent = CreateAgent(ui, ran, [
            PlanRound(extra: WriteCall("w0", "C:\\\\Temp\\\\early.txt")),
            Calls(WriteCall("w1", "C:\\\\Temp\\\\a.txt"), WriteCall("w2", "C:\\\\Temp\\\\b.txt")),
            Final()
        ]);

        await agent.RunAsync("почини");

        Assert.Single(ui.Plans);
        // Запись в фазе плана отклонена до шлюза: выполнились только два вызова после одобрения.
        Assert.Equal(["write_file:C:\\Temp\\a.txt", "write_file:C:\\Temp\\b.txt"], ran);
        var asked = Assert.Single(ui.Asked);
        Assert.Contains("b.txt", asked.Arguments!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_guard_is_asked_even_about_an_approved_step()
    {
        var ran = new List<string>();
        var ui = new PlanUi(PlanDecision.Execute);
        var agent = CreateAgent(ui, ran, [
            PlanRound(),
            Calls(WriteCall("w1", "C:\\\\Temp\\\\a.txt")),
            Final()
        ]);
        agent.Guard = (_, _) => Task.FromResult(new SynGuardReport([false], null));

        await agent.RunAsync("почини");

        var asked = Assert.Single(ui.Asked);
        Assert.True(asked.AlwaysAsk, "одобренный план заглушил вопрос SynGuard");
    }

    [Fact]
    public async Task A_cancelled_plan_changes_nothing_and_says_so()
    {
        var ran = new List<string>();
        var ui = new PlanUi(PlanDecision.Cancel);
        var agent = CreateAgent(ui, ran, [PlanRound(), Calls(WriteCall("w1", "C:\\\\Temp\\\\a.txt")), Final()]);

        var result = await agent.RunAsync("почини");

        Assert.Empty(ran);
        Assert.Equal(Loc.Get("S.Plan.CancelledReport"), result.AssistantText);
    }

    [Fact]
    public async Task A_remark_reaches_the_model_and_the_new_plan_is_asked_about()
    {
        var ran = new List<string>();
        var ui = new PlanUi(new PlanDecision(PlanVerdict.Amend, "только a.txt"), PlanDecision.Execute);
        var bodies = new List<string>();
        var agent = CreateAgent(ui, ran, [PlanRound(), PlanRound(), Calls(WriteCall("w1", "C:\\\\Temp\\\\a.txt")), Final()], bodies);

        await agent.RunAsync("почини");

        Assert.Equal(2, ui.Plans.Count);
        Assert.Contains("только a.txt", bodies[1], StringComparison.Ordinal);
        Assert.Empty(ui.Asked);
        Assert.Single(ran);
    }

    [Fact]
    public async Task The_planning_rule_is_only_there_while_planning()
    {
        var bodies = new List<string>();
        var agent = CreateAgent(new PlanUi(PlanDecision.Execute), [], [PlanRound(), Final()], bodies);

        await agent.RunAsync("почини");

        Assert.Contains("PLAN FIRST", bodies[0], StringComparison.Ordinal);
        Assert.Contains("submit_plan", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain("PLAN FIRST", bodies[1], StringComparison.Ordinal);
    }

    // ───────────────────────── помощники ─────────────────────────

    private static string PlanRound(string? extra = null)
    {
        var plan = JsonSerializer.Serialize(new
        {
            summary = "Записать файл",
            steps = new[]
            {
                new { description = "Записать a.txt", tool = "write_file", target = "C:\\Temp\\a.txt", changes_system = true }
            }
        });
        var submit = ToolCallJson("p" + Guid.NewGuid().ToString("N"), "submit_plan", plan);
        return Calls(extra is null ? [submit] : [extra, submit]);
    }

    private static string WriteCall(string id, string path) =>
        ToolCallJson(id, "write_file", "{\"path\":\"" + path + "\",\"content\":\"x\"}");

    private static string ToolCallJson(string id, string name, string arguments) =>
        JsonSerializer.Serialize(new { id, type = "function", function = new { name, arguments } });

    private static string Calls(params string[] calls) =>
        $$"""{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{{string.Join(",", calls)}}]},"finish_reason":"tool_calls"}]}""";

    private static string Final() =>
        """{"choices":[{"message":{"role":"assistant","content":"Готово."}}]}""";

    private static Agent CreateAgent(PlanUi ui, List<string> ran, string[] replies, List<string>? bodies = null)
    {
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 6
        };
        var http = new HttpClient(new SequenceHandler(replies, bodies ?? []))
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        return new Agent(
            new VeniceClient(http, options),
            new ToolRegistry([new PathTool("write_file", ran)]),
            options,
            ui,
            new SessionUndoTracker())
        {
            PlanFirst = true
        };
    }

    private sealed class SequenceHandler(string[] replies, List<string> bodies) : HttpMessageHandler
    {
        private int _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (bodies)
            {
                bodies.Add(request.Content is null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
            }

            var index = Math.Min(Interlocked.Increment(ref _next) - 1, replies.Length - 1);
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(replies[index], Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class PathTool(string name, List<string> ran) : ITool
    {
        public string Name => name;

        public string Description => "stub";

        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
        {
            lock (ran)
            {
                ran.Add(name + ":" + arguments.GetProperty("path").GetString());
            }

            return Task.FromResult(ToolResult.Ok("готово"));
        }
    }

    private sealed class PlanUi(params PlanDecision[] decisions) : IAgentUi
    {
        private int _next;

        public List<AgentPlan> Plans { get; } = [];

        public List<DangerousActionInfo> Asked { get; } = [];

        public Task<PlanDecision> ReviewPlanAsync(AgentPlan plan, CancellationToken cancellationToken = default)
        {
            Plans.Add(plan);
            return Task.FromResult(decisions[Math.Min(_next++, decisions.Length - 1)]);
        }

        public void Warn(string message)
        {
        }

        public void Error(string message)
        {
        }

        public void Info(string message)
        {
        }

        public void AssistantMessage(string text)
        {
        }

        public void ToolCall(string name, string argumentsJson)
        {
        }

        public void ToolResult(string name, ToolResult result)
        {
        }

        public Task<T> RunBusyAsync<T>(string message, Func<Task<T>> work, CancellationToken cancellationToken = default) => work();

        public Task<bool> ConfirmDangerousActionAsync(DangerousActionInfo info, CancellationToken cancellationToken = default)
        {
            lock (Asked)
            {
                Asked.Add(info);
            }

            return Task.FromResult(true);
        }
    }
}

/// <summary>Карточка плана: кнопки остаются на ней при любом числе шагов.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class PlanReviewOverlayTests
{
    private readonly WpfFixture _wpf;

    public PlanReviewOverlayTests(WpfFixture wpf) => _wpf = wpf;

    private static PlanReviewRequest LongRequest() => new()
    {
        AgentLabel = "Агент",
        Plan = new AgentPlan
        {
            Summary = "Длинный план",
            Steps = Enumerable.Range(1, 30)
                .Select(i => new PlanStep("Шаг номер " + i + " с длинным описанием, которое переносится на следующую строку",
                    "run_powershell", Script: "Get-Service\nStop-Service Spooler", ChangesSystem: i % 2 == 0))
                .ToList()
        },
        Completion = new TaskCompletionSource<PlanDecision>()
    };

    [Fact]
    public void The_buttons_stay_inside_the_card_with_a_long_plan()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var overlay = new PlanReviewOverlay { Width = 900, Height = 700 };
            overlay.Show(LongRequest());
            overlay.Measure(new Size(900, 700));
            overlay.Arrange(new Rect(0, 0, 900, 700));
            overlay.UpdateLayout();

            var card = (FrameworkElement)overlay.FindName("Card");
            var execute = (FrameworkElement)overlay.FindName("ExecuteButton");
            var bottom = execute.TransformToAncestor(card).Transform(new Point(0, execute.ActualHeight)).Y;

            Assert.True(bottom <= card.ActualHeight, $"кнопка ниже карточки: {bottom} > {card.ActualHeight}");
            Assert.True(card.ActualHeight <= 600.5);
            return null;
        });
    }

    [Fact]
    public void Revise_opens_the_remark_first_and_sends_only_a_written_one()
    {
        _wpf.Ui.Invoke<object?>(() =>
        {
            var overlay = new PlanReviewOverlay();
            var request = LongRequest();
            PlanDecision? decided = null;
            overlay.Decided += (_, decision) => decided = decision;
            overlay.Show(request);

            var amend = (Button)overlay.FindName("AmendButton");
            var remark = (TextBox)overlay.FindName("RemarkBox");
            var panel = (FrameworkElement)overlay.FindName("RemarkPanel");

            amend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.Null(decided);
            Assert.False(amend.IsEnabled);

            remark.Text = "  без перезапуска  ";
            Assert.True(amend.IsEnabled);
            amend.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(new PlanDecision(PlanVerdict.Amend, "без перезапуска"), decided);
            return null;
        });
    }

    [Fact]
    public void Each_step_shows_what_exactly_will_run()
    {
        var line = PlanStepRow.TechnicalLine(new PlanStep("d", "windows_service", "stop", "Spooler"));
        var script = PlanStepRow.TechnicalLine(new PlanStep("d", "run_powershell", Script: " Get-Date "));

        Assert.Equal("windows_service · stop · Spooler", line);
        Assert.Equal("run_powershell" + Environment.NewLine + "Get-Date", script);
    }
}
