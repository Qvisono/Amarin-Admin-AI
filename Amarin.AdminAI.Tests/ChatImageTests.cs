using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class ChatImageTests
{
    private static ImageAttachment Sample(string label = "shot.png") =>
        new("iVBORw0KGgo=", "image/png", label);

    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort — a leftover temp dir must not fail the run.
        }
    }

    [Fact]
    public void Attached_images_survive_a_save_and_load()
    {
        var root = NewTempRoot();
        try
        {
            var store = new ChatStore(root);
            var session = store.CreateNew("grok-4-6");
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "user",
                Id = "u1",
                CreatedAt = DateTime.Now,
                Text = "что на картинке?",
                Images = [Sample(), Sample("second.png")]
            });
            store.Save(session);
            store.Flush();

            var loaded = new ChatStore(root).TryLoad(session.Id);

            Assert.NotNull(loaded);
            var user = Assert.Single(loaded!.Messages);
            Assert.Equal(2, user.Images.Count);
            Assert.Equal("iVBORw0KGgo=", user.Images[0].Base64);
            Assert.Equal("image/png", user.Images[0].MimeType);
            Assert.Equal("second.png", user.Images[1].Label);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Messages_without_images_still_load()
    {
        // Chats written before attachments existed have no "images" key at all.
        var json = """
            {"id":"old","title":"Старый чат","selectedModelId":"grok-4-6",
             "messages":[{"role":"user","id":"u1","text":"привет","status":"complete","toolRounds":[]}],
             "apiMessages":[]}
            """;

        var session = JsonSerializer.Deserialize<ChatSession>(json, AppJson.Options);

        Assert.NotNull(session);
        var user = Assert.Single(session!.Messages);
        Assert.NotNull(user.Images);
        Assert.Empty(user.Images);
    }

    [Fact]
    public void Vision_content_carries_every_image_after_the_prompt()
    {
        var content = ChatContent.VisionMultiple("опиши", [Sample("a.png"), Sample("b.png")]);

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        var parts = content.EnumerateArray().ToList();
        Assert.Equal(3, parts.Count);
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("опиши", parts[0].GetProperty("text").GetString());
        Assert.All(parts.Skip(1), part => Assert.Equal("image_url", part.GetProperty("type").GetString()));
        Assert.StartsWith(
            "data:image/png;base64,",
            parts[1].GetProperty("image_url").GetProperty("url").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Context_limiter_keeps_the_newest_images_and_strips_the_older_ones()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = "user", Content = ChatContent.VisionMultiple("первый", [Sample()]) },
            new() { Role = "assistant", Content = ChatContent.Text("вижу") },
            new() { Role = "user", Content = ChatContent.VisionMultiple("второй", [Sample()]) }
        };

        var prepared = ApiContextLimiter.Prepare(messages);

        var first = prepared.First(m => m.Role == "user");
        var last = prepared.Last(m => m.Role == "user");
        Assert.Equal(JsonValueKind.String, first.Content!.Value.ValueKind);
        Assert.Contains("уже было передано", first.Content.Value.GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Array, last.Content!.Value.ValueKind);
    }

    [Fact]
    public void Editing_a_message_keeps_its_images()
    {
        var session = new ChatSession { Id = "s1" };
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "user",
            Id = "u1",
            Text = "старый текст",
            Images = [Sample()]
        });
        session.ApiMessages.Add(new ChatMessage
        {
            Role = "user",
            Content = ChatContent.VisionMultiple("старый текст", [Sample()])
        });

        var edited = ChatSessionEdit.ReplaceUserText(session, "u1", "новый текст");

        Assert.True(edited);
        var api = Assert.Single(session.ApiMessages);
        Assert.Equal(JsonValueKind.Array, api.Content!.Value.ValueKind);
        var parts = api.Content.Value.EnumerateArray().ToList();
        Assert.Equal("новый текст", parts[0].GetProperty("text").GetString());
        Assert.Equal("image_url", parts[1].GetProperty("type").GetString());
    }
}
