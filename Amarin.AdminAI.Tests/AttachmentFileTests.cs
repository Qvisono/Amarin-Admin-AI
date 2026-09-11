using System.Net;
using System.Text;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Раньше к сообщению прикреплялись только картинки, хотя меню обещало «Фото, PDF, документы».
/// Venice принимает документы частью <c>file</c> и сам извлекает из них текст — эти тесты держат
/// форму запроса, отбор форматов и то, что вложение переживает сохранение чата.
/// </summary>
public sealed class AttachmentFileTests
{
    private static FileAttachment Pdf(string name = "spec.pdf", long size = 2048) =>
        new(Convert.ToBase64String("%PDF-1.4"u8.ToArray()), "application/pdf", name, size);

    [Fact]
    public void A_document_travels_as_a_file_part()
    {
        var content = ChatContent.Multipart("что здесь?", images: null, files: [Pdf()]);
        var parts = content.EnumerateArray().ToList();

        // Текстовая часть обязана быть первой — иначе часть моделей спотыкается на массиве.
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("что здесь?", parts[0].GetProperty("text").GetString());

        Assert.Equal("file", parts[1].GetProperty("type").GetString());
        var file = parts[1].GetProperty("file");
        Assert.Equal("spec.pdf", file.GetProperty("filename").GetString());
        Assert.StartsWith("data:application/pdf;base64,", file.GetProperty("file_data").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Images_and_documents_travel_together()
    {
        var image = new ImageAttachment("aW1n", "image/png", "shot.png");
        var parts = ChatContent.Multipart("сравни", [image], [Pdf()]).EnumerateArray().ToList();

        Assert.Equal(["text", "image_url", "file"], parts.Select(p => p.GetProperty("type").GetString()));
    }

    [Fact]
    public void The_old_vision_helper_still_produces_the_same_thing()
    {
        // Картинки от инструментов идут через VisionMultiple; их путь трогать было нельзя.
        var image = new ImageAttachment("aW1n", "image/png");
        var vision = ChatContent.VisionMultiple("подпись", [image]);
        var multipart = ChatContent.Multipart("подпись", [image], files: null);

        Assert.Equal(multipart.GetRawText(), vision.GetRawText());
    }

    [Theory]
    [InlineData("отчёт.pdf", true)]
    [InlineData("таблица.xlsx", true)]
    [InlineData("script.py", true)]
    [InlineData("данные.csv", true)]
    [InlineData("readme.md", true)]
    [InlineData("установщик.exe", false)]
    [InlineData("архив.zip", false)]
    [InlineData("ролик.mp4", false)]
    [InlineData("безрасширения", false)]
    public void Only_formats_the_model_can_read_are_accepted(string name, bool supported)
    {
        Assert.Equal(supported, AttachmentTypes.IsSupportedDocument(name));
    }

    [Fact]
    public void The_mime_type_tells_venice_which_parser_to_use()
    {
        Assert.Equal("application/pdf", AttachmentTypes.GuessMimeType("a.pdf"));
        Assert.Equal("text/csv", AttachmentTypes.GuessMimeType("a.csv"));
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            AttachmentTypes.GuessMimeType("a.DOCX"));
        Assert.Equal("application/octet-stream", AttachmentTypes.GuessMimeType("a.unknown"));
    }

    [Fact]
    public void Sizes_read_like_sizes()
    {
        Assert.Equal("512 Б", AttachmentTypes.FormatSize(512));
        Assert.Equal("1,5 КБ", AttachmentTypes.FormatSize(1536).Replace('.', ','));
        Assert.Equal("2 МБ", AttachmentTypes.FormatSize(2 * 1024 * 1024));
    }

    [Fact]
    public void Stale_documents_are_replaced_by_a_note_that_names_them()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = ChatContent.Multipart("первый", null, [Pdf("старый.pdf")]) },
            new() { Role = "assistant", Content = ChatContent.Text("прочитал") },
            new() { Role = "user", Content = ChatContent.Multipart("второй", null, [Pdf("свежий.pdf")]) }
        };

        var prepared = ApiContextLimiter.Prepare(messages);

        // Первое сообщение сжалось до заметки: иначе каждый следующий ход тащил бы base64 заново.
        var first = ChatContent.ReadText(prepared[0].Content);
        Assert.NotNull(first);
        Assert.Contains("старый.pdf", first, StringComparison.Ordinal);
        Assert.Contains("первый", first, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", prepared[0].Content!.Value.GetRawText(), StringComparison.Ordinal);

        // Свежее — целиком, вместе с содержимым файла.
        Assert.Equal(JsonValueKind.Array, prepared[2].Content!.Value.ValueKind);
        Assert.Equal(["свежий.pdf"], FileNamesOf(prepared[2].Content!.Value));
    }

    [Fact]
    public void A_single_document_message_is_left_alone()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = ChatContent.Multipart("глянь", null, [Pdf()]) }
        };

        var prepared = ApiContextLimiter.Prepare(messages);

        Assert.Equal(JsonValueKind.Array, prepared[0].Content!.Value.ValueKind);
    }

    [Fact]
    public void A_chat_with_a_document_survives_a_round_trip_to_disk()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew("grok-4-6");
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "user",
                Id = "m1",
                CreatedAt = DateTime.Now,
                Text = "разбери",
                Files = [Pdf("договор.pdf", 4096)]
            });
            store.Save(session);

            var loaded = store.TryLoad(session.Id);

            Assert.NotNull(loaded);
            var file = Assert.Single(loaded.Messages[0].Files);
            Assert.Equal("договор.pdf", file.FileName);
            Assert.Equal(4096, file.SizeBytes);
            Assert.Equal("application/pdf", file.MimeType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void An_older_chat_without_the_field_loads_with_no_documents()
    {
        // Чат сериализуется рефлексией, а не source-gen контекстом, — миграция не нужна.
        const string json = """
            {
              "id": "s1",
              "title": "Старый чат",
              "messages": [ { "role": "user", "id": "m1", "text": "привет", "images": [] } ],
              "apiMessages": []
            }
            """;

        var session = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options);

        Assert.NotNull(session);
        Assert.Empty(session.Messages[0].Files);
    }

    [Fact]
    public async Task A_document_only_turn_is_a_complete_request()
    {
        string? body = null;
        var handler = new ScriptedHandler((_, sent) =>
        {
            body = sent;
            return Sse("прочитал");
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };
        var engine = new ChatEngine(
            new VeniceClient(http, options), options, () => new AppSettings(), new ToolRegistry([]));
        var session = new ChatSession { Id = "s", SelectedModelId = "grok-4-6" };

        // Ни слова текста — только документ. Раньше такой ход молча не отправлялся.
        await engine.RunTurnAsync(session, "", images: null, files: [Pdf()], new RecordingObserver(),
            CancellationToken.None);

        Assert.Equal(2, session.Messages.Count);
        Assert.Single(session.Messages[0].Files);
        Assert.NotNull(body);
        var content = JsonDocument.Parse(body).RootElement
            .GetProperty("messages").EnumerateArray().Last().GetProperty("content");
        var sentParts = content.EnumerateArray().ToList();

        Assert.Equal(["spec.pdf"], FileNamesOf(content));

        // Заглушка вместо пустого текста: первая часть массива обязана быть текстовой.
        Assert.Equal("Прочитай вложенные файлы.", sentParts[0].GetProperty("text").GetString());
    }

    /// <summary>Имена файлов из содержимого сообщения — JSON экранирует кириллицу, сравнивать надо разобранное.</summary>
    private static List<string> FileNamesOf(JsonElement content) =>
    [
        .. content.EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "file")
            .Select(part => part.GetProperty("file").GetProperty("filename").GetString()!)
    ];

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

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _script;

        public ScriptedHandler(Func<HttpRequestMessage, string, HttpResponseMessage> script) =>
            _script = script;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _script(request, body);
        }
    }

    private sealed class RecordingObserver : IChatTurnObserver
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
