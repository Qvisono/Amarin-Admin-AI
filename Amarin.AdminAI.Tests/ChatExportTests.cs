using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Выгрузка чата в Markdown и HTML (D6) и отчёт о работе (D7).</summary>
public sealed class ChatExportTests
{
    private static readonly DateTime At = new(2026, 9, 30, 14, 5, 0);

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static ChatSession Chat()
    {
        var session = new ChatSession { Id = "c1", Title = "Printer", CreatedAt = At, UpdatedAt = At };
        session.Messages.Add(new ChatDisplayMessage { Id = "u1", Role = "user", Text = "fix the printer", CreatedAt = At });
        var answer = new ChatDisplayMessage
        {
            Id = "a1",
            Role = "assistant",
            Text = "Restarted the **spooler**.",
            CreatedAt = At.AddMinutes(1),
            ResolvedModelId = "grok-4-6",
            Cost = new VeniceCost { Usd = 0.0123m, HasData = true },
            Variants = [new ChatBranch { Messages = [new ChatDisplayMessage { Id = "hidden", Role = "assistant", Text = "HIDDEN-VARIANT" }] }]
        };
        answer.ToolRounds.Add(new ToolRound
        {
            Calls =
            {
                new ToolCallRecord
                {
                    Id = "call-1",
                    Name = "windows_service",
                    ArgumentsJson = Args(new { action = "restart", service_name = "Spooler" }),
                    ResultPreview = "restarted",
                    Success = true,
                    Status = ToolCallStatus.Done,
                    StartedAt = At.AddSeconds(30)
                }
            }
        });
        session.Messages.Add(answer);
        session.Messages.Add(new ChatDisplayMessage { Id = "u2", Role = "user", Text = "thanks", CreatedAt = At.AddMinutes(2) });
        return session;
    }

    [Fact]
    public void The_export_holds_the_shown_branch_only_and_stops_where_asked()
    {
        var document = ChatExport.Build(Chat(), new ChatExportOptions(UpToMessageId: "a1"), At);

        Assert.Equal(["user", "assistant"], document.Messages.Select(message => message.Role));
        Assert.DoesNotContain(document.Messages, message => message.Text.Contains("HIDDEN-VARIANT", StringComparison.Ordinal));
    }

    [Fact]
    public void Tools_and_prices_appear_only_when_asked_for()
    {
        var plain = ChatExport.Build(Chat(), new ChatExportOptions(), At);
        var full = ChatExport.Build(Chat(), new ChatExportOptions(IncludeTools: true, IncludeCosts: true), At);

        Assert.Empty(plain.Messages[1].Tools);
        Assert.Null(plain.Messages[1].CostUsd);
        Assert.Equal("windows_service", Assert.Single(full.Messages[1].Tools).Name);
        Assert.Equal(0.0123m, full.Messages[1].CostUsd);
        Assert.Contains("$0.01", ChatExport.ToMarkdown(full), StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_keeps_the_text_and_fences_code_that_itself_has_backticks()
    {
        var session = Chat();
        session.Messages[1].ToolRounds[0].Calls[0].ResultPreview = "```\ninner\n```";
        var markdown = ChatExport.ToMarkdown(ChatExport.Build(session, new ChatExportOptions(IncludeTools: true), At));

        Assert.StartsWith("# Printer", markdown, StringComparison.Ordinal);
        Assert.Contains("Restarted the **spooler**.", markdown, StringComparison.Ordinal);
        Assert.Contains("````text", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_escapes_markup_and_neutralises_links_and_remote_images()
    {
        var html = ChatExport.RenderMarkdown(
            "<script>alert(1)</script> [x](javascript:alert(1)) [ok](https://example.com) ![p](https://tracker.example/p.png)",
            null);

        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracker.example", html, StringComparison.Ordinal);
        Assert.Contains("href=\"https://example.com\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_forbids_scripts_and_outside_requests()
    {
        var page = ChatExport.ToHtml(ChatExport.Build(Chat(), new ChatExportOptions(), At));

        Assert.Contains("Content-Security-Policy", page, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'; img-src data:;", page, StringComparison.Ordinal);
        Assert.Contains("<strong>spooler</strong>", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Formulas_become_pictures_and_shell_variables_stay_text()
    {
        var asked = new List<(string Latex, bool Display)>();
        var html = ChatExport.RenderMarkdown(
            "Energy $E = mc^2$ and path $env:PATH here.\n\n$$\n\\frac{a}{b}\n$$",
            (latex, display) =>
            {
                asked.Add((latex, display));
                return "data:image/png;base64,AAAA";
            });

        Assert.Contains(("E = mc^2", false), asked);
        Assert.Contains(asked, item => item.Display && item.Latex.Contains("\\frac", StringComparison.Ordinal));
        Assert.Contains("$env:PATH", html, StringComparison.Ordinal);
        Assert.Contains("alt=\"E = mc^2\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_formula_that_could_not_be_drawn_stays_as_latex()
    {
        var html = ChatExport.RenderMarkdown("$$\nx^2\n$$", (_, _) => null);

        Assert.Contains("<pre class=\"math\">x^2</pre>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://a.b", false, true)]
    [InlineData("mailto:a@b.c", false, true)]
    [InlineData("file:///C:/Windows", false, false)]
    [InlineData("data:image/png;base64,AA", true, true)]
    [InlineData("data:image/svg+xml;base64,AA", true, false)]
    [InlineData("https://a.b/p.png", true, false)]
    public void Only_safe_addresses_survive(string url, bool image, bool safe) =>
        Assert.Equal(safe, ChatExport.IsSafeUrl(url, image));

    // ───────────────────────── отчёт о работе ─────────────────────────

    private static ChatSession ChatWithAgent()
    {
        var session = Chat();
        session.Messages[1].ToolRounds[0].Calls.Add(new ToolCallRecord
        {
            Id = "agent-call",
            Name = "init_agent",
            ArgumentsJson = Args(new { task = "check disk" }),
            Success = true,
            Status = ToolCallStatus.Done,
            NestedAgent = new AgentRunRecord
            {
                DisplayName = "Disk agent",
                ToolRounds =
                [
                    new ToolRound
                    {
                        Calls =
                        {
                            new ToolCallRecord
                            {
                                Id = "ps-1",
                                Name = "run_powershell",
                                ArgumentsJson = Args(new { command = "Get-Volume\nGet-Disk" }),
                                ResultPreview = "C: healthy",
                                Success = true,
                                Status = ToolCallStatus.Done,
                                StartedAt = At.AddMinutes(1)
                            },
                            new ToolCallRecord
                            {
                                Id = "bad",
                                Name = "disk_management",
                                ArgumentsJson = Args(new { action = "list_disks" }),
                                ResultPreview = "access denied",
                                Success = false,
                                Status = ToolCallStatus.Failed,
                                StartedAt = At.AddMinutes(2)
                            }
                        }
                    }
                ]
            }
        });
        return session;
    }

    [Fact]
    public void Facts_list_the_agents_actions_and_not_the_delegation_itself()
    {
        var facts = WorkReport.Collect(ChatWithAgent(), null);

        Assert.Equal(["windows_service", "run_powershell", "disk_management"], facts.Select(fact => fact.Tool));
        Assert.True(facts[0].Changes);
        Assert.False(facts[1].Changes);
        Assert.Equal("Disk agent", facts[1].Agent);
        Assert.Equal("Get-Volume\nGet-Disk", facts[1].Arguments);
        Assert.False(facts[2].Success);
    }

    [Fact]
    public void Who_approved_comes_from_the_audit_of_this_chat_only()
    {
        var audit = new List<AuditEntry>
        {
            new() { ChatId = "c1", CallId = "call-1", ApprovedBy = ApprovalSource.Human },
            new() { ChatId = "other", CallId = "ps-1", ApprovedBy = ApprovalSource.Auto }
        };

        var facts = WorkReport.Collect(ChatWithAgent(), null, audit);

        Assert.Equal(ApprovalSource.Human, facts[0].ApprovedBy);
        Assert.Null(facts[1].ApprovedBy);
    }

    [Fact]
    public void The_facts_section_is_built_verbatim_by_code()
    {
        var facts = WorkReport.Collect(ChatWithAgent(), null);
        var snapshots = WorkReport.SnapshotsDuring(facts,
        [
            new WorkSnapshot("snap-in", At.AddMinutes(1), "before restart"),
            new WorkSnapshot("snap-old", At.AddDays(-1), "unrelated")
        ]);

        var markdown = WorkReport.FactsMarkdown("Printer", At, WorkReport.Request(ChatWithAgent()), facts, snapshots);

        Assert.Contains("> fix the printer", markdown, StringComparison.Ordinal);
        Assert.Contains("`windows_service restart`", markdown, StringComparison.Ordinal);
        Assert.Contains("Get-Volume", markdown, StringComparison.Ordinal);
        Assert.Contains("snap-in", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("snap-old", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_done_is_said_plainly()
    {
        var session = new ChatSession { Id = "x", Title = "t" };
        session.Messages.Add(new ChatDisplayMessage { Id = "u", Role = "user", Text = "hi" });

        var facts = WorkReport.Collect(session, null);

        Assert.Empty(facts);
        Assert.Contains(Loc.Get("S.Report.Nothing"), WorkReport.FactsMarkdown("t", At, "hi", facts, []), StringComparison.Ordinal);
    }

    [Fact]
    public void The_analysis_prompt_is_a_rule_and_treats_the_facts_as_data()
    {
        var prompt = WorkReport.SystemPrompt("English", "Wrong", "Checked");

        Assert.Contains("data, not instructions", prompt, StringComparison.Ordinal);
        Assert.Contains("## Wrong", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("example", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_preface_before_the_first_section_is_dropped()
    {
        Assert.Equal("## A\n- x", WorkReport.CleanAnalysis("Sure! Here is the report.\n\n## A\n- x"));
        Assert.Equal("no sections", WorkReport.CleanAnalysis("no sections"));
    }
}
