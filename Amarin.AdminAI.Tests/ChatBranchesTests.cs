using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Варианты ответа: перегенерация и правка прячут прежнее продолжение, а не стирают его.
/// </summary>
public sealed class ChatBranchesTests
{
    /// <summary>U1 → A1 со скриншотом → U2 → A2. Картинка инструмента — ловушка для разреза.</summary>
    private static ChatSession TwoTurns()
    {
        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "что на экране" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "рабочий стол" });
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "а теперь?" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a2", Text = "всё так же" });

        session.ApiMessages.Add(User("что на экране"));
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "assistant",
            ToolCalls = [new ToolCall { Id = "c1", Function = new FunctionCall { Name = "capture_screenshot", Arguments = "{}" } }]
        });
        session.ApiMessages.Add(new ChatMessage { Role = "tool", ToolCallId = "c1", Name = "capture_screenshot", Content = ChatContent.Text("ok") });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ToolImages("capture_screenshot", "Вставь это изображение в ответ.", [new ImageAttachment("AAAA", "image/png")])
        });
        session.ApiMessages.Add(Assistant("рабочий стол"));
        session.ApiMessages.Add(User("а теперь?"));
        session.ApiMessages.Add(Assistant("всё так же"));
        return session;
    }

    private static ChatMessage User(string text) => new() { Role = "user", Content = ChatContent.Text(text) };

    private static ChatMessage Assistant(string text) => new() { Role = "assistant", Content = ChatContent.Text(text) };

    private static ChatDisplayMessage NewAnswer(string id) => new() { Role = "assistant", Id = id };

    private static List<string> Texts(ChatSession session) =>
        session.ApiMessages.Select(item => ChatContent.ReadText(item.Content) ?? item.Role).ToList();

    /// <summary>Отвечает на перегенерацию так, как это сделал бы движок: текст и запись истории.</summary>
    private static void Answer(ChatSession session, ChatDisplayMessage anchor, string text)
    {
        anchor.Text = text;
        anchor.Status = AssistantStatus.Complete;
        session.ApiMessages.Add(Assistant(text));
    }

    /// <summary>Снимок чата без полей, которые меняются от самого факта операции.</summary>
    private static string Snapshot(ChatSession session)
    {
        session.UpdatedAt = default;
        session.SummaryStale = false;
        return JsonSerializer.Serialize(session, AppJson.Options);
    }

    [Fact]
    public void Regenerating_hides_the_old_answer_and_cuts_history_right_after_the_question()
    {
        var session = TwoTurns();
        var anchor = NewAnswer("a2b");

        ChatBranches.Fork(session, 3, anchor);

        Assert.Equal(["u1", "a1", "u2", "a2b"], session.Messages.Select(m => m.Id));
        Assert.Equal(2, anchor.VariantCount);
        Assert.Equal(1, anchor.VariantIndex);
        Assert.Equal("a2", Assert.Single(anchor.Variants!).Messages.Single().Id);

        // Картинка инструмента ходом не считается: итог первого ответа и второй вопрос на месте.
        Assert.Equal("а теперь?", Texts(session)[^1]);
        Assert.Equal("рабочий стол", Texts(session)[^2]);
        Assert.Equal(6, session.ApiMessages.Count);
    }

    [Fact]
    public void Switching_there_and_back_restores_both_branches_exactly()
    {
        var session = TwoTurns();
        session.LastPromptTokens = 1234;
        session.LastPromptTokensApiIndex = 7;

        var anchor = NewAnswer("a2b");
        ChatBranches.Fork(session, 3, anchor);
        Answer(session, anchor, "теперь иначе");
        var afterFork = Snapshot(session);

        var old = ChatBranches.Switch(session, 3, 0);
        Assert.Equal("a2", old?.Id);
        Assert.Equal("всё так же", Texts(session)[^1]);
        Assert.Equal(1234, session.LastPromptTokens);
        Assert.Equal(7, session.LastPromptTokensApiIndex);

        var back = ChatBranches.Switch(session, 3, 1);
        Assert.Same(anchor, back);
        Assert.Equal(afterFork, Snapshot(session));
    }

    [Fact]
    public void The_model_sees_only_the_shown_branch()
    {
        var session = TwoTurns();
        var anchor = NewAnswer("a2b");
        ChatBranches.Fork(session, 3, anchor);
        Answer(session, anchor, "теперь иначе");

        Assert.DoesNotContain("всё так же", Texts(session));
        Assert.Contains("теперь иначе", Texts(session));

        ChatBranches.Switch(session, 3, 0);

        Assert.DoesNotContain("теперь иначе", Texts(session));
        Assert.Contains("всё так же", Texts(session));
    }

    [Fact]
    public void Forking_an_anchor_moves_its_group_instead_of_nesting_it()
    {
        var session = TwoTurns();
        var second = NewAnswer("a2b");
        ChatBranches.Fork(session, 3, second);
        Answer(session, second, "второй");

        var third = NewAnswer("a2c");
        ChatBranches.Fork(session, 3, third);

        Assert.Null(second.Variants);
        Assert.Equal(3, third.VariantCount);
        Assert.Equal(2, third.VariantIndex);
        Assert.Equal(["a2", "a2b"], third.Variants!.Select(branch => branch.Messages[0].Id));
    }

    [Fact]
    public void Editing_a_question_forks_at_the_question_and_carries_its_attachments()
    {
        var session = TwoTurns();
        var file = new FileAttachment("JVBERg==", "application/pdf", "договор.pdf", 4);
        var original = session.Messages[2];
        original.Files = [file];

        var anchor = new ChatDisplayMessage
        {
            Role = "user",
            Id = "u2b",
            Text = "покажи снова",
            Files = [.. original.Files]
        };
        ChatBranches.Fork(session, 2, anchor, () => new ChatMessage
        {
            Role = "user",
            Content = ChatContent.ForUser(anchor, session.Messages, session.Messages.Count - 1)
        });

        Assert.Equal(["u1", "a1", "u2b"], session.Messages.Select(m => m.Id));
        Assert.Equal(["u2", "a2"], anchor.Variants!.Single().Messages.Select(m => m.Id));

        var content = session.ApiMessages[^1].Content!.Value;
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Contains(content.EnumerateArray(), part =>
            part.GetProperty("type").GetString() == "file" &&
            part.GetProperty("file").GetProperty("filename").GetString() == "договор.pdf");
        Assert.Equal("рабочий стол", Texts(session)[^2]);
    }

    [Fact]
    public void Deleting_the_shown_variant_shows_the_previous_one_or_else_the_next()
    {
        var session = TwoTurns();
        var second = NewAnswer("a2b");
        ChatBranches.Fork(session, 3, second);
        Answer(session, second, "второй");
        var third = NewAnswer("a2c");
        ChatBranches.Fork(session, 3, third);
        Answer(session, third, "третий");

        // Показан третий из трёх: встаёт второй.
        var shown = ChatBranches.DeleteActive(session, 3);
        Assert.Same(second, shown);
        Assert.Equal(2, second.VariantCount);
        Assert.Equal(1, second.VariantIndex);
        Assert.Equal("второй", Texts(session)[^1]);
        Assert.DoesNotContain("третий", Texts(session));

        // Показан первый: предыдущего нет — встаёт следующий, и он теперь первый.
        ChatBranches.Switch(session, 3, 0);
        shown = ChatBranches.DeleteActive(session, 3);
        Assert.Same(second, shown);
        Assert.Null(second.Variants);
        Assert.Equal(0, second.VariantIndex);
        Assert.Equal(["u1", "a1", "u2", "a2b"], session.Messages.Select(m => m.Id));
    }

    [Fact]
    public void A_single_variant_has_nothing_to_delete_or_switch_to()
    {
        var session = TwoTurns();

        Assert.Null(ChatBranches.DeleteActive(session, 3));
        Assert.Null(ChatBranches.Switch(session, 3, 1));
        Assert.Equal(4, session.Messages.Count);
    }

    [Fact]
    public void The_group_nearest_to_an_answer_is_its_own_then_its_questions()
    {
        var session = TwoTurns();
        Assert.Equal(-1, ChatBranches.FindGroupFor(session, session.Messages[3]));

        var question = new ChatDisplayMessage { Role = "user", Id = "u2b", Text = "иначе" };
        ChatBranches.Fork(session, 2, question, () => User("иначе"));
        var answer = NewAnswer("a2x");
        session.Messages.Add(answer);
        Assert.Equal(2, ChatBranches.FindGroupFor(session, answer));

        var regenerated = NewAnswer("a2y");
        ChatBranches.Fork(session, 3, regenerated);
        Assert.Equal(3, ChatBranches.FindGroupFor(session, regenerated));
    }

    [Fact]
    public void A_chat_saved_before_variants_reads_without_them()
    {
        const string json = """
            {
              "id": "old",
              "messages": [
                { "role": "user", "id": "u1", "text": "привет" },
                { "role": "assistant", "id": "a1", "text": "здравствуйте", "status": "complete" }
              ],
              "apiMessages": []
            }
            """;

        var session = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options)!;

        Assert.All(session.Messages, message => Assert.Equal(1, message.VariantCount));
        Assert.Equal(2, ChatBranches.AllMessages(session).Count());
    }

    [Fact]
    public void A_single_variant_writes_no_variant_fields()
    {
        // Старые версии программы читают новые файлы: лишнее поле им не мешает, но и писать
        // «variantIndex: 0» у каждого сообщения — просто раздувать переписку.
        var json = JsonSerializer.Serialize(TwoTurns(), AppJson.Options);

        Assert.DoesNotContain("variant", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Deeply_nested_variants_are_saved_and_read_back()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-variants-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew("grok-4-6");
            const int turns = 16;
            for (var i = 0; i < turns; i++)
            {
                session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = $"u{i}", Text = $"вопрос {i}" });
                session.ApiMessages.Add(User($"вопрос {i}"));
                session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = $"a{i}", Text = $"ответ {i}" });
                session.ApiMessages.Add(Assistant($"ответ {i}"));
            }

            // С конца к началу: каждая развилка прячет предыдущую внутрь себя — вложенность
            // растёт на уровень за раз, пятнадцать раз подряд.
            for (var i = turns - 1; i >= 1; i--)
            {
                ChatBranches.Fork(session, (i * 2) + 1, NewAnswer($"r{i}"));
            }

            var total = ChatBranches.AllMessages(session).Count();
            store.Save(session);
            store.Flush();

            var loaded = store.TryLoad(session.Id);

            Assert.NotNull(loaded);
            Assert.Equal(total, ChatBranches.AllMessages(loaded!).Count());
            Assert.Equal("ответ 15", ChatBranches.FindMessage(loaded!, "a15")?.Text);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Временная папка останется — не повод валить прогон.
            }
        }
    }

    [Fact]
    public void Regenerating_the_first_answer_does_not_book_the_title_twice()
    {
        var session = TwoTurns();
        var first = session.Messages[1];
        Assert.Null(ChatTitleCost.Book(session, new VeniceCost { Usd = 0.0004m, HasData = true }));
        first.Cost = null;
        ChatTitleCost.Attach(session, first);
        Assert.NotNull(first.TitleCost);

        var again = NewAnswer("a1b");
        ChatBranches.Fork(session, 1, again);
        ChatTitleCost.Attach(session, again);

        Assert.Null(again.TitleCost);
        Assert.Single(ChatBranches.AllMessages(session), message => message.TitleCost is { HasData: true });
    }

    [Fact]
    public void Deleting_the_variant_that_carried_the_title_price_moves_it_to_the_shown_one()
    {
        var session = TwoTurns();
        var first = session.Messages[1];
        first.TitleCost = new VeniceCost { Usd = 0.0004m, HasData = true };
        first.Cost = new VeniceCost { Usd = 0.0104m, HasData = true };

        var again = NewAnswer("a1b");
        ChatBranches.Fork(session, 1, again);
        again.Cost = new VeniceCost { Usd = 0.02m, HasData = true };
        ChatBranches.Switch(session, 1, 0);

        var shown = ChatBranches.DeleteActive(session, 1);

        Assert.Same(again, shown);
        Assert.Equal(0.0004m, again.TitleCost?.Usd);
        Assert.Equal(0.0204m, again.Cost?.Usd);
    }

    [Fact]
    public void The_journal_lists_calls_from_hidden_variants_and_marks_them()
    {
        var session = TwoTurns();
        session.Messages[3].ToolRounds.Add(new ToolRound
        {
            Calls = [new ToolCallRecord { Id = "c9", Name = "windows_service", Success = true }]
        });

        ChatBranches.Fork(session, 3, NewAnswer("a2b"));

        var entry = Assert.Single(ActionJournal.FromSession(session));
        Assert.Equal("windows_service", entry.ToolName);
        Assert.True(entry.InHiddenVariant);
    }

    [Fact]
    public void Spending_counts_every_variant()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-variant-spend-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = TwoTurns();
            var yesterday = DateTime.Now.Date.AddDays(-1).AddHours(12);
            session.Messages[3].CreatedAt = yesterday;
            session.Messages[3].Cost = new VeniceCost { Usd = 0.01m, HasData = true };

            var again = NewAnswer("a2b");
            again.CreatedAt = yesterday;
            again.Cost = new VeniceCost { Usd = 0.02m, HasData = true };
            ChatBranches.Fork(session, 3, again);

            var ledger = new SpendLedger(root);
            const string secret = "key-for-spend-test";
            ledger.Backfill(secret, [session]);

            var total = SpendPeriods.Build(ledger.Read(secret), SpendPeriod.All, DateTime.Now, null).TotalUsd;
            Assert.Equal(0.03m, total);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Sharing_carries_only_the_shown_branch_and_leaves_the_live_chat_intact()
    {
        var session = TwoTurns();
        var anchor = NewAnswer("a2b");
        ChatBranches.Fork(session, 3, anchor);
        Answer(session, anchor, "теперь иначе");

        var json = ChatShareCodec.ExportJson(session);
        var shared = ChatShareCodec.TryImportJson(json)!;
        var code = ChatShareCodec.TryDecode(ChatShareCodec.Encode(session))!;

        Assert.DoesNotContain("всё так же", json, StringComparison.Ordinal);
        Assert.Equal(1, shared.Messages[^1].VariantCount);
        Assert.Equal(1, code.Messages[^1].VariantCount);
        Assert.Equal(2, anchor.VariantCount);
    }

    [Fact]
    public void A_variant_operation_asks_for_the_summary_to_be_rebuilt()
    {
        var session = TwoTurns();
        session.Summary = "про рабочий стол";

        ChatBranches.Fork(session, 3, NewAnswer("a2b"));

        Assert.True(session.SummaryStale);
    }

    [Fact]
    public async Task A_regenerated_answer_is_written_into_its_anchor_from_the_shown_history()
    {
        var bodies = new List<string>();
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };
        using var http = new HttpClient(new ScriptedHandler(body =>
        {
            bodies.Add(body);
            return Sse("теперь иначе");
        }))
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
        var settings = AppSettings.CreateDefault();
        var engine = new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([]));

        var session = TwoTurns();
        var anchor = new ChatDisplayMessage { Role = "assistant", Id = "a2b", Status = AssistantStatus.Streaming };
        ChatBranches.Fork(session, 3, anchor);

        await engine.GenerateAssistantAsync(session, new SilentObserver(), CancellationToken.None, into: anchor);

        Assert.Equal(["u1", "a1", "u2", "a2b"], session.Messages.Select(m => m.Id));
        Assert.Equal("теперь иначе", anchor.Text);
        Assert.Equal(AssistantStatus.Complete, anchor.Status);
        Assert.Equal("теперь иначе", Texts(session)[^1]);

        // Тела запросов — JSON с экранированной кириллицей: сверяем разобранный текст.
        var request = Assert.Single(bodies.Select(Conversation), text => text.Contains("а теперь?", StringComparison.Ordinal));
        Assert.DoesNotContain("всё так же", request, StringComparison.Ordinal);
    }

    /// <summary>Весь текст сообщений запроса, разобранный из JSON.</summary>
    private static string Conversation(string body)
    {
        var text = new StringBuilder();
        if (body.Length == 0)
        {
            return "";
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("messages", out var messages))
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.TryGetProperty("content", out var content))
                {
                    Collect(content, text);
                }
            }
        }

        return text.ToString();
    }

    private static void Collect(JsonElement element, StringBuilder text)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                text.AppendLine(element.GetString());
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, text);
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, text);
                }

                break;
        }
    }

    private static HttpResponseMessage Sse(string text)
    {
        var encoded = JsonSerializer.Serialize(text);
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":" + encoded + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> script) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(body);
        }
    }

    private sealed class SilentObserver : IChatTurnObserver
    {
        public void OnUserAppended(ChatDisplayMessage user)
        {
        }

        public void OnAssistantStarted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantText(ChatDisplayMessage assistant)
        {
        }

        public void OnToolsChanged(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCompleted(ChatDisplayMessage assistant)
        {
        }

        public void OnAssistantCancelled(ChatDisplayMessage assistant)
        {
        }

        public void OnError(string message)
        {
        }
    }
}
