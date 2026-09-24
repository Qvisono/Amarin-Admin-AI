using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Ответить» на фрагмент прежнего ответа: как цитата хранится, в каком виде уходит модели и
/// что с ней бывает при правке, удалении хода и сохранении чата.
/// </summary>
/// <remarks>
/// Модель должна понимать цитату без раздумий, поэтому форма блока проверяется строкой
/// целиком, а не «содержит что-то похожее»: любое её изменение — осознанное решение, а не
/// побочный эффект.
/// </remarks>
public sealed class QuoteTests
{
    private static ChatDisplayMessage User(string id, string text, params MessageQuote[] quotes) => new()
    {
        Role = "user",
        Id = id,
        CreatedAt = DateTime.Now,
        Text = text,
        Quotes = [.. quotes]
    };

    private static ChatDisplayMessage Answer(string id, string text, AssistantStatus status = AssistantStatus.Complete) => new()
    {
        Role = "assistant",
        Id = id,
        CreatedAt = DateTime.Now,
        Text = text,
        Status = status
    };

    private static MessageQuote Quote(int number, string source, string text, string? context = null) => new()
    {
        Number = number,
        SourceMessageId = source,
        Text = text,
        Context = context
    };

    // ───────────────────────── Блок для модели ─────────────────────────

    [Fact]
    public void Without_quotes_the_message_goes_out_exactly_as_before()
    {
        // Чаты без цитат обязаны уходить модели байт в байт как до этой функции.
        var messages = new List<ChatDisplayMessage> { Answer("a1", "ответ") };

        Assert.Equal("просто вопрос", ChatQuotes.Wrap("просто вопрос", null, messages, 1));
        Assert.Equal("просто вопрос", ChatQuotes.Wrap("просто вопрос", [], messages, 1));
    }

    [Fact]
    public void A_quote_from_the_last_reply_is_named_so()
    {
        var messages = new List<ChatDisplayMessage>
        {
            User("u1", "как открыть порт"),
            Answer("a1", "Выполните команду netsh.\nПотом перезапустите службу.")
        };

        var text = ChatQuotes.Wrap("а зачем перезапуск?", [Quote(1, "a1", "Потом перезапустите службу.")], messages, 2);

        Assert.Equal(
            ChatQuotes.Header + "\n" +
            "@1, from your last reply:\n" +
            "> Потом перезапустите службу.\n" +
            ChatQuotes.MessageMarker + "\n" +
            "а зачем перезапуск?",
            text);
    }

    [Fact]
    public void An_earlier_reply_is_named_by_how_it_starts_and_not_by_a_number()
    {
        // Номер ответа («№3 из 7») разошёлся бы с тем, что видит модель: отменённые ответы в
        // историю не попадают, а раунды инструментов добавляют свои записи. Начало ответа лежит
        // в её истории дословно.
        var messages = new List<ChatDisplayMessage>
        {
            User("u1", "вопрос"),
            Answer("a1", "## Настройка брандмауэра\n\nОткройте консоль."),
            User("u2", "ещё вопрос"),
            Answer("a2", "Второй ответ.")
        };

        var text = ChatQuotes.Wrap("поясни", [Quote(1, "a1", "Откройте консоль.")], messages, 4);

        Assert.Contains("@1, from your reply that starts \"## Настройка брандмауэра\":\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Interrupted_and_deleted_sources_are_called_what_they_are()
    {
        var messages = new List<ChatDisplayMessage>
        {
            User("u1", "вопрос"),
            Answer("a1", "Недописанн", AssistantStatus.Cancelled)
        };

        var text = ChatQuotes.Wrap(
            "что ты хотел сказать?",
            [Quote(1, "a1", "Недописанн"), Quote(2, "gone", "старое")],
            messages,
            2);

        Assert.Contains("@1, from an interrupted reply of yours (not in the history above):\n", text, StringComparison.Ordinal);
        Assert.Contains("@2, from a reply of yours that has since been deleted:\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_source_after_the_quoting_message_does_not_count()
    {
        // Классификация смотрит только назад: ответ, появившийся позже сообщения, не может быть
        // его источником — иначе пересборка старого сообщения ссылалась бы в будущее.
        var messages = new List<ChatDisplayMessage>
        {
            User("u1", "вопрос"),
            Answer("a1", "первый"),
            User("u2", "ответ на цитату"),
            Answer("a2", "второй")
        };

        Assert.Equal(QuoteSourceKind.Last, ChatQuotes.Classify(messages, 2, "a1", out _));
        Assert.Equal(QuoteSourceKind.Gone, ChatQuotes.Classify(messages, 2, "a2", out _));
        Assert.Equal(QuoteSourceKind.Earlier, ChatQuotes.Classify(messages, 4, "a1", out _));
    }

    [Fact]
    public void A_short_fragment_carries_its_passage_and_every_line_is_marked()
    {
        var messages = new List<ChatDisplayMessage> { Answer("a1", "текст") };

        var text = ChatQuotes.Wrap(
            "почему именно он?",
            [
                Quote(1, "a1", "8080", "Откройте порт 8080 в брандмауэре."),
                Quote(3, "a1", "первая\n\nвторая")
            ],
            messages,
            1);

        Assert.Contains("@1, from your last reply (in the passage \"Откройте порт 8080 в брандмауэре.\"):\n> 8080\n", text, StringComparison.Ordinal);

        // Номер не перенумеровывается: «@3» остаётся «@3», даже если «@2» убрали.
        Assert.Contains("@3, from your last reply:\n> первая\n>\n> вторая\n", text, StringComparison.Ordinal);
        Assert.EndsWith(ChatQuotes.MessageMarker + "\nпочему именно он?", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_with_its_own_fences_stays_inside_the_quote()
    {
        var messages = new List<ChatDisplayMessage> { Answer("a1", "текст") };

        var text = ChatQuotes.Wrap("что делает?", [Quote(1, "a1", "```powershell\nGet-Service\n```")], messages, 1);

        Assert.Contains("> ```powershell\n> Get-Service\n> ```\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_block_speaks_english_whatever_the_interface()
    {
        // Русская обвязка в сообщении англоязычного человека сбивала бы модель на русский.
        Assert.DoesNotMatch("[А-Яа-яЁё]", ChatQuotes.Header);
        Assert.DoesNotMatch("[А-Яа-яЁё]", ChatQuotes.MessageMarker);
    }

    // ───────────────────────── Разбор и нормализация ─────────────────────────

    [Fact]
    public void Normalize_drops_layout_noise()
    {
        var raw = "​первая  \r\n\r\n\r\n\r\nвторая строка\t\r\n  ";

        Assert.Equal("первая\n\nвторая строка", ChatQuotes.Normalize(raw));
        Assert.Equal("", ChatQuotes.Normalize(null));
        Assert.Equal("", ChatQuotes.Normalize(" \n \n"));
    }

    [Fact]
    public void A_long_fragment_is_clipped_in_the_middle()
    {
        var text = new string('а', 1200) + "СЕРЕДИНА" + new string('я', 1200);

        var clipped = ChatQuotes.Clip(text);

        Assert.True(clipped.Length < text.Length);
        Assert.StartsWith(new string('а', 100), clipped, StringComparison.Ordinal);
        Assert.EndsWith(new string('я', 100), clipped, StringComparison.Ordinal);
        Assert.Contains("[…]", clipped, StringComparison.Ordinal);
        Assert.DoesNotContain("СЕРЕДИНА", clipped, StringComparison.Ordinal);
        Assert.Equal("короткий", ChatQuotes.Clip("короткий"));
    }

    [Theory]
    [InlineData("см. @1 и @2", new[] { 1, 2 })]
    [InlineData("(@2), @1.", new[] { 2, 1 })]
    [InlineData("@12 это двенадцать", new[] { 12 })]
    [InlineData("user@1host a@1 @0 @1abc @123 \\\\srv\\@1 x.@1", new int[0])]
    [InlineData("«@3»", new[] { 3 })]
    public void References_are_whole_tokens(string text, int[] expected)
    {
        Assert.Equal(expected, ChatQuotes.References(text).Select(r => r.Number));
    }

    [Fact]
    public void A_reference_knows_where_it_stands()
    {
        var found = ChatQuotes.References("про @2 и всё").Single();

        Assert.Equal((4, 2, 2), (found.Start, found.Length, found.Number));
    }

    [Theory]
    [InlineData("@", 1, true, 0, "")]
    [InlineData("текст @", 7, true, 6, "")]
    [InlineData("текст @1", 8, true, 6, "1")]
    [InlineData("текст (@1", 9, true, 7, "1")]
    [InlineData("почта a@", 8, false, -1, "")]
    [InlineData("текст @123", 10, false, -1, "")]
    // Каретка между «@» и цифрой — правка уже готовой ссылки, подсказке там нечего дописать.
    [InlineData("текст @1", 7, false, -1, "")]
    [InlineData("@1abc", 2, false, -1, "")]
    [InlineData("нет", 3, false, -1, "")]
    public void The_reference_being_typed_is_recognised(string text, int caret, bool typing, int start, string digits)
    {
        var found = ChatQuotes.TryGetTypingReference(text, caret, out var at, out var typed);

        Assert.Equal(typing, found);
        Assert.Equal(start, at);
        Assert.Equal(digits, typed);
    }

    [Fact]
    public void Numbers_are_stable_and_restart_only_from_empty()
    {
        var pending = new List<MessageQuote> { Quote(1, "a", "x"), Quote(2, "a", "y") };
        Assert.Equal(3, ChatQuotes.NextNumber(pending));

        // Убрали первую — следующая всё равно третья: «@2» в тексте указывает на прежнюю.
        pending.RemoveAt(0);
        Assert.Equal(3, ChatQuotes.NextNumber(pending));

        Assert.Equal(1, ChatQuotes.NextNumber([]));
    }

    [Fact]
    public void The_same_fragment_of_the_same_reply_is_a_duplicate()
    {
        var pending = new List<MessageQuote> { Quote(1, "a1", "текст") };

        Assert.True(ChatQuotes.IsDuplicate(pending, "a1", "текст"));
        Assert.False(ChatQuotes.IsDuplicate(pending, "a2", "текст"));
        Assert.False(ChatQuotes.IsDuplicate(pending, "a1", "другой"));
    }

    [Fact]
    public void Context_goes_only_with_a_short_one_line_fragment()
    {
        const string paragraph = "Откройте порт 8080 в брандмауэре Windows и перезапустите службу.";

        Assert.Equal(paragraph, ChatQuotes.ContextAround(paragraph, "8080"));

        // Длинный или многострочный фрагмент однозначен сам.
        Assert.Null(ChatQuotes.ContextAround(paragraph, new string('x', 70)));
        Assert.Null(ChatQuotes.ContextAround(paragraph, "8080\nпорт"));

        // Фрагмент — почти весь абзац: окружение ничего не добавит.
        Assert.Null(ChatQuotes.ContextAround("порт 8080", "порт 8080"));

        // Фрагмента в абзаце нет (выделение начиналось в другом) — не выдумываем.
        Assert.Null(ChatQuotes.ContextAround(paragraph, "9090"));
    }

    [Fact]
    public void Context_of_a_long_paragraph_is_a_window_around_the_fragment()
    {
        var paragraph = new string('а', 400) + " 8080 " + new string('я', 400);

        var context = ChatQuotes.ContextAround(paragraph, "8080")!;

        Assert.Contains("8080", context, StringComparison.Ordinal);
        Assert.StartsWith("…", context, StringComparison.Ordinal);
        Assert.EndsWith("…", context, StringComparison.Ordinal);
        Assert.True(context.Length <= 205, context.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_anchor_is_the_first_line_cut_at_a_word()
    {
        Assert.Equal("## Итог", ChatQuotes.Anchor("\n\n## Итог\nдальше"));
        Assert.Equal("", ChatQuotes.Anchor(null));

        var anchor = ChatQuotes.Anchor("Чтобы открыть порт восемь тысяч восемьдесят, выполните команду в консоли администратора");
        Assert.EndsWith("…", anchor, StringComparison.Ordinal);
        Assert.True(anchor.Length <= 61);
        Assert.False(anchor.TrimEnd('…').EndsWith(' '));
    }

    // ───────────────────────── Сообщение для модели ─────────────────────────

    [Fact]
    public void ForUser_matches_the_old_builder_when_there_are_no_quotes()
    {
        var file = new FileAttachment(Convert.ToBase64String("%PDF"u8.ToArray()), "application/pdf", "spec.pdf", 10);
        var plain = User("u1", "вопрос");
        var withFile = User("u2", "вопрос");
        withFile.Files = [file];

        Assert.Equal(
            ChatContent.Text("вопрос").GetRawText(),
            ChatContent.ForUser(plain, [plain], 0).GetRawText());
        Assert.Equal(
            ChatContent.Multipart(ChatContent.BuildPrompt("вопрос", null, [file]), null, [file]).GetRawText(),
            ChatContent.ForUser(withFile, [withFile], 0).GetRawText());
    }

    [Fact]
    public void ForUser_puts_quotes_ahead_of_the_attachment_list()
    {
        var file = new FileAttachment(Convert.ToBase64String("%PDF"u8.ToArray()), "application/pdf", "spec.pdf", 10);
        var answer = Answer("a1", "ответ");
        var user = User("u1", "сверь с файлом", Quote(1, "a1", "фрагмент"));
        user.Files = [file];

        var content = ChatContent.ForUser(user, [answer, user], 1);
        var text = content.EnumerateArray().First().GetProperty("text").GetString()!;

        Assert.StartsWith(ChatQuotes.Header, text, StringComparison.Ordinal);
        Assert.Contains(ChatQuotes.MessageMarker + "\nсверь с файлом", text, StringComparison.Ordinal);
        Assert.Contains("spec.pdf", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_the_text_keeps_the_quotes()
    {
        // Правка пересобирает сообщение для модели заново — прежде оттуда уже выпадали
        // документы, и цитаты обязаны пережить её так же.
        var session = new ChatSession { Id = "s" };
        session.Messages.Add(User("u1", "вопрос"));
        session.Messages.Add(Answer("a1", "ответ"));
        session.Messages.Add(User("u2", "старый текст", Quote(1, "a1", "фрагмент")));
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("вопрос") });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("ответ") });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("старый текст") });

        Assert.True(ChatSessionEdit.ReplaceUserText(session, "u2", "новый текст"));

        var text = ChatContent.ReadText(session.ApiMessages[^1].Content)!;
        Assert.StartsWith(ChatQuotes.Header, text, StringComparison.Ordinal);
        Assert.Contains("> фрагмент", text, StringComparison.Ordinal);
        Assert.EndsWith("новый текст", text, StringComparison.Ordinal);
        Assert.Single(session.Messages[^1].Quotes);
    }

    [Fact]
    public void Deleting_the_quoted_turn_tells_the_model_the_source_is_gone()
    {
        var session = QuotedSession();

        Assert.True(ChatSessionEdit.DeleteTurn(session, "a1"));

        var text = ChatContent.ReadText(session.ApiMessages[2].Content)!;
        Assert.Contains("@1, from a reply of yours that has since been deleted:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Deleting_an_unrelated_turn_leaves_the_quoting_message_untouched()
    {
        var session = QuotedSession();
        var before = session.ApiMessages[4].Content!.Value.GetRawText();

        // Средний ход — не источник цитаты: сообщение с ней обязано остаться байт в байт.
        Assert.True(ChatSessionEdit.DeleteTurn(session, "a2"));

        Assert.Equal("u3", session.Messages[2].Id);
        Assert.Equal(before, session.ApiMessages[2].Content!.Value.GetRawText());
    }

    /// <summary>
    /// Три хода: вопрос → a1, вопрос → a2, ответ на цитату из a1 → a3.
    /// </summary>
    private static ChatSession QuotedSession()
    {
        var session = new ChatSession { Id = "s" };
        session.Messages.Add(User("u1", "первый"));
        session.Messages.Add(Answer("a1", "Первый ответ."));
        session.Messages.Add(User("u2", "второй"));
        session.Messages.Add(Answer("a2", "Второй ответ."));
        session.Messages.Add(User("u3", "про цитату", Quote(1, "a1", "Первый ответ.")));
        session.Messages.Add(Answer("a3", "Третий ответ."));

        for (var i = 0; i < session.Messages.Count; i++)
        {
            var message = session.Messages[i];
            session.ApiMessages.Add(new ChatMessage
            {
                Role = message.Role,
                Content = message.Role == "user"
                    ? ChatContent.ForUser(message, session.Messages, i)
                    : ChatContent.Text(message.Text)
            });
        }

        return session;
    }

    // ───────────────────────── Хранение ─────────────────────────

    [Fact]
    public void Quotes_survive_saving_the_chat()
    {
        var session = new ChatSession { Id = "s" };
        session.Messages.Add(User("u1", "текст", Quote(2, "a1", "фрагмент", "окружение")));

        var json = JsonSerializer.Serialize(session, AppJson.Options);
        var back = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options)!;

        var quote = Assert.Single(back.Messages[0].Quotes);
        Assert.Equal((2, "a1", "фрагмент", "окружение"), (quote.Number, quote.SourceMessageId, quote.Text, quote.Context));
    }

    [Fact]
    public void A_chat_saved_before_quotes_existed_still_loads()
    {
        const string json = """
            { "id": "s", "messages": [ { "role": "user", "id": "u1", "text": "старое" } ] }
            """;

        var session = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options)!;

        Assert.Empty(session.Messages[0].Quotes);
    }

    [Fact]
    public void A_shared_chat_carries_its_quotes()
    {
        var session = new ChatSession { Id = "s", Title = "t" };
        session.Messages.Add(User("u1", "вопрос"));
        session.Messages.Add(Answer("a1", "ответ"));
        session.Messages.Add(User("u2", "про это", Quote(1, "a1", "ответ")));

        var decoded = ChatShareCodec.TryDecode(ChatShareCodec.Encode(session));
        var imported = ChatShareCodec.TryImportJson(ChatShareCodec.ExportJson(session));

        Assert.Equal("a1", Assert.Single(decoded!.Messages[2].Quotes).SourceMessageId);
        Assert.Equal("a1", Assert.Single(imported!.Messages[2].Quotes).SourceMessageId);
    }

    // ───────────────────────── Движок ─────────────────────────

    [Fact]
    public async Task The_request_carries_the_block_and_the_transcript_keeps_plain_text()
    {
        var bodies = new List<string>();
        var handler = new ScriptedHandler((_, sent) =>
        {
            bodies.Add(sent);
            return Sse("ok");
        });
        var (engine, session) = Engine(handler, new AppSettings(), "grok-4-6");
        session.Messages.Add(User("u0", "first-question"));
        session.Messages.Add(Answer("a1", "Run netsh first.\nThen restart the service."));

        await engine.RunTurnAsync(
            session,
            "why-restart",
            images: null,
            files: null,
            quotes: [Quote(1, "a1", "Then restart the service.")],
            new SilentObserver(),
            CancellationToken.None);

        var user = session.Messages[2];
        Assert.Equal("why-restart", user.Text);
        Assert.Single(user.Quotes);

        var sent = JsonDocument.Parse(bodies.Last()).RootElement
            .GetProperty("messages").EnumerateArray().Last(m => m.GetProperty("role").GetString() == "user")
            .GetProperty("content").GetString()!;
        Assert.Equal(
            ChatQuotes.Header + "\n@1, from your last reply:\n> Then restart the service.\n" +
            ChatQuotes.MessageMarker + "\nwhy-restart",
            sent);
    }

    [Fact]
    public async Task The_router_never_reads_the_quote()
    {
        // Цитата — слова ассистента, а в них бывает чужой текст из сети. Выбор модели и цены
        // по-прежнему решает только то, что человек написал сам.
        var bodies = new List<string>();
        var handler = new ScriptedHandler((_, sent) =>
        {
            bodies.Add(sent);
            return sent.Contains("\"stream\":true", StringComparison.Ordinal)
                ? Sse("ok")
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"lite\"}}]}",
                        Encoding.UTF8,
                        "application/json")
                };
        });
        var (engine, session) = Engine(handler, new AppSettings { ChatModelId = "auto" }, "auto");
        session.Messages.Add(User("u0", "earlier-question"));
        session.Messages.Add(Answer("a1", "reply heavy scraped-words"));

        await engine.RunTurnAsync(
            session,
            "typed-now",
            images: null,
            files: null,
            quotes: [Quote(1, "a1", "reply heavy scraped-words")],
            new SilentObserver(),
            CancellationToken.None);

        var routed = bodies.Single(body => body.Contains("Classify the user request", StringComparison.Ordinal));
        Assert.Contains("typed-now", routed, StringComparison.Ordinal);
        Assert.DoesNotContain("scraped-words", routed, StringComparison.Ordinal);
    }

    private static (ChatEngine Engine, ChatSession Session) Engine(
        ScriptedHandler handler,
        AppSettings settings,
        string model)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        return (
            new ChatEngine(new VeniceClient(http, options), options, () => settings, new ToolRegistry([])),
            new ChatSession { Id = "s1", SelectedModelId = model });
    }

    private static HttpResponseMessage Sse(string text)
    {
        var payload =
            "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return script(request, body);
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
