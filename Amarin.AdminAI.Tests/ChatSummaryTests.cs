using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сводка переписки: как её чистят после модели, как разбирают ответ поиска и как она живёт
/// на диске.
/// </summary>
public sealed class ChatSummaryTests
{
    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-summary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        return root;
    }

    [Fact]
    public void The_summary_is_stripped_of_what_models_wrap_it_in()
    {
        Assert.Equal("Настройка принтера", ChatSummary.Sanitize("  «Настройка принтера»  "));
        Assert.Equal("Настройка принтера", ChatSummary.Sanitize("\"Настройка принтера\""));

        // Перевод строки внутри сводки сломал бы строку списка: она одной высоты.
        Assert.Equal("Про диск и про память", ChatSummary.Sanitize("Про диск\nи  про   память"));
    }

    [Fact]
    public void An_empty_answer_is_not_a_summary()
    {
        Assert.Null(ChatSummary.Sanitize(null));
        Assert.Null(ChatSummary.Sanitize("   "));
        Assert.Null(ChatSummary.Sanitize("\"\""));
    }

    [Fact]
    public void A_long_summary_is_cut_on_a_word_boundary()
    {
        var raw = string.Join(' ', Enumerable.Repeat("слово", 200));
        var cut = ChatSummary.Sanitize(raw)!;

        Assert.True(cut.Length <= ChatSummary.MaxLength + 1, $"длина {cut.Length}");
        Assert.EndsWith("…", cut, StringComparison.Ordinal);

        // Обрыв на середине слова читался бы как порча текста, а не как сокращение.
        Assert.DoesNotContain("сло…", cut, StringComparison.Ordinal);
    }

    [Fact]
    public void The_search_answer_survives_everything_models_put_around_it()
    {
        Assert.Equal([2, 5, 1], ChatSummary.ParseSearchAnswer("2, 5, 1", 6));
        Assert.Equal([3, 1], ChatSummary.ParseSearchAnswer("```\n3. 1.\n```", 6));
        Assert.Equal([4], ChatSummary.ParseSearchAnswer("Подходит только чат 4, остальные - нет.", 6));
    }

    [Fact]
    public void Numbers_outside_the_list_are_dropped_rather_than_trusted()
    {
        // Выдуманный чат хуже, чем ненайденный: открывать было бы нечего.
        Assert.Equal([2], ChatSummary.ParseSearchAnswer("2, 9, 40, 0", 3));
        Assert.Empty(ChatSummary.ParseSearchAnswer("7", 3));
        Assert.Empty(ChatSummary.ParseSearchAnswer("", 3));
        Assert.Empty(ChatSummary.ParseSearchAnswer("1", 0));
    }

    [Fact]
    public void A_repeated_number_is_counted_once_and_order_is_kept()
    {
        // Порядок ответа — это порядок близости к запросу, и терять его нельзя.
        Assert.Equal([3, 1, 2], ChatSummary.ParseSearchAnswer("3, 1, 3, 2, 1", 3));
    }

    [Fact]
    public void The_prompt_numbers_the_chats_from_one()
    {
        var prompt = ChatSummary.SearchUserPrompt("принтер", ["про диск", "про принтер"]);

        Assert.Contains("1. про диск", prompt, StringComparison.Ordinal);
        Assert.Contains("2. про принтер", prompt, StringComparison.Ordinal);
        Assert.Contains("принтер", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_running_summary_is_given_the_previous_one_to_carry_forward()
    {
        var fresh = ChatSummary.UserPrompt(null, "User: привет", "Русский");
        Assert.DoesNotContain("Summary so far", fresh, StringComparison.Ordinal);

        var next = ChatSummary.UserPrompt("Про принтер", "User: а теперь про диск", "Русский");
        Assert.Contains("Summary so far", next, StringComparison.Ordinal);
        Assert.Contains("Про принтер", next, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fast_model_writes_the_summary()
    {
        var settings = new AppSettings { AgentFastModelId = "deepseek-v4-flash-0731-fast" };
        Assert.Equal("deepseek-v4-flash-0731-fast", ChatSummary.ResolveModel(settings, "grok-4-6"));

        // «Авто» здесь не значение: служебному вызову нужна конкретная модель.
        settings.AgentFastModelId = "auto";
        Assert.Equal("grok-4-6", ChatSummary.ResolveModel(settings, "grok-4-6"));
    }

    [Fact]
    public void The_exchange_leaves_out_what_the_summary_has_no_use_for()
    {
        var text = ChatSummaryGenerator.BuildExchange([
            new ChatDisplayMessage { Role = "user", Text = "поставь 7-Zip" },
            new ChatDisplayMessage { Role = "assistant", Text = "" },
            new ChatDisplayMessage { Role = "assistant", Text = "готово" }
        ]);

        Assert.Equal("User: поставь 7-Zip\nAssistant: готово", text);
    }

    [Fact]
    public void The_summary_survives_a_save_and_a_reload()
    {
        var root = TempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew("grok-4-6");
            session.Summary = "Ставили 7-Zip и чинили принтер";
            store.Save(session);

            Assert.Equal("Ставили 7-Zip и чинили принтер", store.TryLoad(session.Id)!.Summary);

            // И в индексе тоже: по нему ищут, не открывая каждый файл.
            var entry = store.List().Single(item => item.Id == session.Id);
            Assert.Equal("Ставили 7-Zip и чинили принтер", entry.Summary);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_shared_chat_does_not_carry_the_summary_along()
    {
        var session = new ChatSession
        {
            Id = "s1",
            Title = "Про принтер",
            Summary = "Чинили принтер",
            Messages = [new ChatDisplayMessage { Role = "user", Text = "привет" }]
        };

        var code = ChatShareCodec.Encode(session);
        var decoded = ChatShareCodec.TryDecode(code)!;

        // Переписка доехала целиком — а сводка нет: делиться чатом не то же, что делиться
        // пересказом, и у получателя она соберётся заново при первом же ответе.
        Assert.Equal("привет", decoded.Messages[0].Text);
        Assert.Null(decoded.Summary);
    }
}

/// <summary>
/// Генератор сводок против подставного Venice: что уходит в запрос и что получается из ответа.
/// </summary>
public sealed class ChatSummaryGeneratorTests
{
    private static ChatSummaryGenerator Build(
        Func<HttpRequestMessage, HttpResponseMessage> reply,
        out HttpClient http)
    {
        http = new HttpClient(new StubHandler(reply))
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };

        return new ChatSummaryGenerator(
            http,
            new AgentOptions
            {
                ApiKey = "test",
                BaseUrl = "https://api.venice.ai/api/v1",
                Model = "grok-4-6"
            },
            () => new AppSettings { AgentFastModelId = "deepseek-v4-flash-0731-fast" });
    }

    private static HttpResponseMessage Answer(string content) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { role = "assistant", content } } }
                }),
                System.Text.Encoding.UTF8,
                "application/json")
        };

    [Fact]
    public async Task The_search_returns_the_chats_in_the_order_the_model_ranked_them()
    {
        var generator = Build(_ => Answer("3, 1"), out var http);
        using (http)
        {
            var result = await generator.SearchAsync(
                "принтер",
                [("a", "про диск"), ("b", "про сеть"), ("c", "про принтер")]);

            // Порядок ответа — порядок близости: третий чат подошёл лучше первого.
            Assert.Equal(["c", "a"], result.ChatIds);
        }
    }

    [Fact]
    public async Task A_number_the_model_invented_does_not_become_a_chat()
    {
        var generator = Build(_ => Answer("2, 99"), out var http);
        using (http)
        {
            var result = await generator.SearchAsync("принтер", [("a", "про диск"), ("b", "про сеть")]);
            Assert.Equal(["b"], result.ChatIds);
        }
    }

    [Fact]
    public async Task An_empty_query_or_no_summaries_never_reaches_the_network()
    {
        var called = false;
        var generator = Build(
            _ =>
            {
                called = true;
                return Answer("1");
            },
            out var http);

        using (http)
        {
            Assert.Empty((await generator.SearchAsync("", [("a", "про диск")])).ChatIds);
            Assert.Empty((await generator.SearchAsync("принтер", [])).ChatIds);
            Assert.Empty((await generator.UpdateAsync(null, "   ")).Summary ?? "");
            Assert.False(called);
        }
    }

    [Fact]
    public async Task A_refusal_leaves_the_summary_alone_instead_of_throwing()
    {
        var generator = Build(
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":"nope"}""", System.Text.Encoding.UTF8, "application/json")
            },
            out var http);

        using (http)
        {
            // Сводка — удобство, а не работа, за которой пришёл человек: её провал тихий.
            var draft = await generator.UpdateAsync("прошлая сводка", "User: привет");
            Assert.Null(draft.Summary);

            var search = await generator.SearchAsync("принтер", [("a", "про диск")]);
            Assert.Empty(search.ChatIds);
        }
    }

    [Fact]
    public async Task The_summary_comes_back_cleaned_up()
    {
        var generator = Build(_ => Answer("  «Ставили 7-Zip»  "), out var http);
        using (http)
        {
            var draft = await generator.UpdateAsync(null, "User: поставь 7-Zip");
            Assert.Equal("Ставили 7-Zip", draft.Summary);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(reply(request));
    }
}
