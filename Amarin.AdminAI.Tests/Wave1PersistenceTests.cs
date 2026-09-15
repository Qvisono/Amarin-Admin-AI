using System.Text.Json;
using System.Windows;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

public sealed class Wave1PersistenceTests
{
    [Fact]
    public void Router_thinking_is_off_out_of_the_box()
    {
        // Маршрутизатор отвечает одним словом, и проход размышления перед ним только
        // оплачивается — а на списке мелких подзадач ещё и уговаривает себя на «heavy».
        var fresh = AppSettings.CreateDefault().RouterReasoning;
        Assert.True(fresh.DisableThinking);
        Assert.Null(fresh.ReasoningEffort);
    }

    [Theory]
    // Прежнее заводское значение — переписываем.
    [InlineData(false, "medium", true, null)]
    // Выбранное руками — не трогаем ни в каком виде.
    [InlineData(false, "high", false, "high")]
    [InlineData(false, null, false, null)]
    [InlineData(true, "medium", true, "medium")]
    public void The_old_router_default_stops_thinking_and_a_chosen_one_does_not(
        bool disableThinking,
        string? effort,
        bool expectedDisabled,
        string? expectedEffort)
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            var settings = store.Load();
            settings.RouterReasoning.DisableThinking = disableThinking;
            settings.RouterReasoning.ReasoningEffort = effort;
            store.Save(settings);

            var loaded = new AppSettingsStore(root).Load().RouterReasoning;

            Assert.Equal(expectedDisabled, loaded.DisableThinking);
            Assert.Equal(expectedEffort, loaded.ReasoningEffort);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Agent_system_prompt_does_not_mention_ask_user()
    {
        Assert.DoesNotContain("ask_user", Agent.BaseSystemPrompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void App_settings_roundtrip_in_appdata_root()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            Assert.False(store.Exists);

            var settings = store.Load();
            settings.AutoScroll = false;
            settings.ApprovalMode = ApprovalMode.AlwaysApprove;
            settings.ChatModelId = "grok-4-6";
            settings.ChatReasoning.DisableThinking = false;
            settings.ChatReasoning.ReasoningEffort = "high";
            settings.SessionMode = SessionMode.Isolated;
            settings.MainPrompt = "custom";
            store.Save(settings);

            Assert.True(store.Exists);
            var loaded = new AppSettingsStore(root).Load();
            Assert.False(loaded.AutoScroll);
            Assert.Equal(ApprovalMode.AlwaysApprove, loaded.ApprovalMode);
            Assert.Equal("grok-4-6", loaded.ChatModelId);
            Assert.False(loaded.ChatReasoning.DisableThinking);
            Assert.Equal("high", loaded.ChatReasoning.ReasoningEffort);
            Assert.False(loaded.LiteReasoning.DisableThinking);
            Assert.Equal(SessionMode.Isolated, loaded.SessionMode);
            Assert.Equal("custom", loaded.MainPrompt);
            Assert.Equal("grok-4-6", loaded.HeavyModelId);
            Assert.Equal("openai-gpt-56-luna", loaded.LiteModelId);
            Assert.Equal("medium", loaded.HeavyReasoning.ReasoningEffort);
            Assert.True(loaded.TitleReasoning.DisableThinking);
            Assert.False(loaded.AgentLiteReasoning.DisableThinking);
            Assert.Equal("high", loaded.AgentHeavyReasoning.ReasoningEffort);
            Assert.Equal("deepseek-v4-flash-0731-fast", loaded.AgentFastModelId);
            Assert.True(loaded.AgentFastReasoning.DisableThinking);
            Assert.True(loaded.RouterReasoning.DisableThinking);
            Assert.True(AppSettings.CreateDefault().AutoScroll);
            Assert.Equal(ApprovalMode.Normal, AppSettings.CreateDefault().ApprovalMode);
            Assert.Equal("", AppSettings.CreateDefault().MainPrompt);
            Assert.Equal(100, AppSettings.CreateDefault().UiScalePercent);
            Assert.Equal(100, loaded.UiScalePercent);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Ui_scale_normalizes_and_roundtrips()
    {
        Assert.Equal([80, 90, 100, 110, 125, 150, 175, 200, 225, 250], UiScale.Percents);
        Assert.Equal(100, UiScale.Normalize(100));
        Assert.Equal(150, UiScale.Normalize(150));
        Assert.Equal(250, UiScale.Normalize(250));
        Assert.Equal(80, UiScale.Normalize(80));
        Assert.Equal(100, UiScale.Normalize(117));
        Assert.Equal(100, UiScale.Normalize(300));
        Assert.Equal(100, UiScale.Normalize(0));
        Assert.Equal(100, UiScale.Normalize(-10));

        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            var settings = store.Load();
            Assert.Equal(100, settings.UiScalePercent);
            settings.UiScalePercent = 150;
            store.Save(settings);
            Assert.Equal(150, new AppSettingsStore(root).Load().UiScalePercent);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Warn_tooltip_stays_centered_below_target()
    {
        var at100 = UiScale.PlaceBelowCenter(new Size(230, 80), new Size(32, 32), new Point(0, 6));
        Assert.Single(at100);
        Assert.Equal(-99, at100[0].Point.X, 3);
        Assert.Equal(38, at100[0].Point.Y, 3);

        var at150 = UiScale.PlaceBelowCenter(new Size(345, 120), new Size(48, 48), new Point(0, 9));
        Assert.Single(at150);
        Assert.Equal((48 - 345) / 2.0, at150[0].Point.X, 3);
        Assert.Equal(57, at150[0].Point.Y, 3);
    }

    [Fact]
    public void Legacy_chat_prompts_migrate_on_load()
    {
        var root = NewTempRoot();
        try
        {
            var store = new AppSettingsStore(root);
            store.Save(new AppSettings
            {
                MainPrompt = AppSettingsStore.LegacyPersonalityPrompts[0],
                TechAiPrompt = ChatEngine.LegacyDefaultTechPrompt,
                TechAgentPrompt = "агент кастом"
            });

            var loaded = new AppSettingsStore(root).Load();
            Assert.Equal("", loaded.MainPrompt);
            Assert.Equal("", loaded.TechAiPrompt);
            Assert.Equal("агент кастом", loaded.TechAgentPrompt);

            store.Save(new AppSettings
            {
                MainPrompt = AppSettingsStore.LegacyPersonalityPrompts[^1],
                TechAiPrompt = ChatEngine.LegacyDefaultTechPromptV10,
                TechAgentPrompt = "агент кастом"
            });
            loaded = new AppSettingsStore(root).Load();
            Assert.Equal("", loaded.MainPrompt);
            Assert.Equal("", loaded.TechAiPrompt);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Chat_store_roundtrips_messages_toolcalls_and_agent_report()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew("claude-sonnet-5");
            session.DisableThinking = false;
            session.ReasoningEffort = "medium";
            session.Title = "Ночная сводка";
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "user",
                Id = "u1",
                CreatedAt = new DateTime(2026, 8, 30, 7, 1, 0),
                Text = "чекни сеть"
            });
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "assistant",
                Id = "a1",
                CreatedAt = new DateTime(2026, 8, 30, 7, 1, 4),
                Text = "Проверил сеть — всё в порядке.",
                RequestedModelId = "claude-sonnet-5",
                ResolvedModelId = "claude-sonnet-5",
                Duration = TimeSpan.FromSeconds(4),
                ThinkingDuration = TimeSpan.FromSeconds(2),
                Cost = new VeniceCost { Usd = 0.0236m, HasData = true },
                ModelCost = new VeniceCost { Usd = 0.0136m, HasData = true },
                Status = AssistantStatus.Complete,
                ToolRounds =
                [
                    new ToolRound
                    {
                        InfoLine = "Инструменты выполнены · 1",
                        Calls =
                        [
                            new ToolCallRecord
                            {
                                Id = "call_1",
                                Name = "init_agent",
                                ArgumentsJson = """{"prompt":"тщательно проверь сеть","complexity":"heavy"}""",
                                ResultPreview = "Отчёт агента: сеть исправна, 8 проверок пройдено",
                                Success = true,
                                Status = ToolCallStatus.Done,
                                NestedAgent = new AgentRunRecord
                                {
                                    SlotIndex = 0,
                                    ModelId = "grok-4-6",
                                    DisplayName = "Агент grok-4-6",
                                    Status = AgentRunStatus.Complete,
                                    ReportText = "сеть исправна, 8 проверок пройдено",
                                    Cost = new VeniceCost { Usd = 0.01m, HasData = true },
                                    ToolRounds =
                                    [
                                        new ToolRound
                                        {
                                            InfoLine = "8 инструментов",
                                            Calls =
                                            [
                                                new ToolCallRecord
                                                {
                                                    Id = "ac1",
                                                    Name = "network",
                                                    ArgumentsJson = """{"action":"adapters"}""",
                                                    ResultPreview = "Radmin VPN | Ethernet | Up",
                                                    Success = true,
                                                    Status = ToolCallStatus.Done
                                                }
                                            ]
                                        }
                                    ]
                                }
                            }
                        ]
                    }
                ]
            });
            session.ApiMessages.Add(new ChatMessage
            {
                Role = "user",
                Content = ChatContent.Text("чекни сеть")
            });
            session.ApiMessages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = ChatContent.Text("Проверил сеть — всё в порядке."),
                ToolCalls =
                [
                    new ToolCall
                    {
                        Id = "call_1",
                        Function = new FunctionCall
                        {
                            Name = "init_agent",
                            Arguments = """{"prompt":"тщательно проверь сеть","complexity":"heavy"}"""
                        }
                    }
                ]
            });
            session.ApiMessages.Add(new ChatMessage
            {
                Role = "tool",
                ToolCallId = "call_1",
                Name = "init_agent",
                Content = ChatContent.Text("Отчёт агента: сеть исправна")
            });

            store.Save(session);

            var loaded = store.TryLoad(session.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Ночная сводка", loaded.Title);
            Assert.False(loaded.DisableThinking);
            Assert.Equal("medium", loaded.ReasoningEffort);
            Assert.Equal(2, loaded.Messages.Count);
            Assert.Equal("чекни сеть", loaded.Messages[0].Text);
            Assert.Equal(AssistantStatus.Complete, loaded.Messages[1].Status);
            Assert.Equal(0.0236m, loaded.Messages[1].Cost?.Usd);

            // The price breakdown and the thinking time are rebuilt from the reopened chat, so
            // both have to survive the round trip.
            Assert.Equal(0.0136m, loaded.Messages[1].ModelCost?.Usd);
            Assert.Equal(TimeSpan.FromSeconds(2), loaded.Messages[1].ThinkingDuration);
            Assert.Equal("init_agent", loaded.Messages[1].ToolRounds[0].Calls[0].Name);
            Assert.Equal("heavy", JsonDocument.Parse(loaded.Messages[1].ToolRounds[0].Calls[0].ArgumentsJson)
                .RootElement.GetProperty("complexity").GetString());
            var agent = loaded.Messages[1].ToolRounds[0].Calls[0].NestedAgent;
            Assert.NotNull(agent);
            Assert.Equal("grok-4-6", agent.ModelId);
            Assert.Equal("network", agent.ToolRounds[0].Calls[0].Name);
            Assert.Equal(3, loaded.ApiMessages.Count);
            Assert.Equal("tool", loaded.ApiMessages[2].Role);
            Assert.Equal("init_agent", loaded.ApiMessages[2].Name);
            Assert.Equal("чекни сеть", ChatContent.ReadText(loaded.ApiMessages[0].Content));
            Assert.Equal("call_1", loaded.ApiMessages[1].ToolCalls?[0].Id);

            var index = store.List();
            Assert.Single(index);
            Assert.Equal(session.Id, index[0].Id);
            Assert.Single(store.Search("свод"));
            Assert.Empty(store.Search("xyz"));

            Assert.True(store.Delete(session.Id));
            Assert.Null(store.TryLoad(session.Id));
            Assert.Empty(store.List());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Chat_store_delete_all_wipes_files_and_index()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var a = store.CreateNew("claude-sonnet-5");
            a.Title = "А";
            a.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "раз" });
            store.Save(a);
            var b = store.CreateNew("grok-4-6");
            b.Title = "Б";
            b.Messages.Add(new ChatDisplayMessage { Role = "user", Text = "два" });
            store.Save(b);
            Assert.Equal(2, store.List().Count);

            Assert.Equal(2, store.DeleteAll());
            Assert.Empty(store.List());
            Assert.Null(store.TryLoad(a.Id));
            Assert.Null(store.TryLoad(b.Id));
            Assert.Equal(0, new ChatStore(root).DeleteAll());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Session_and_model_stores_write_to_appdata_not_exe_directory()
    {
        var root = NewTempRoot();
        try
        {
            var settingsStore = new AppSettingsStore(root);
            settingsStore.Update(s =>
            {
                s.SessionMode = SessionMode.Isolated;
                s.ChatModelId = "kimi-k2-7-code";
            });

            var loaded = settingsStore.Load();
            Assert.Equal(SessionMode.Isolated, loaded.SessionMode);
            Assert.Equal("kimi-k2-7-code", loaded.ChatModelId);
            Assert.StartsWith(root, settingsStore.FilePath, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("appsettings.json", settingsStore.FilePath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-wave1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // temp leftovers are acceptable
        }
    }
}
