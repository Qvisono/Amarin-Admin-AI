using System.IO.Compression;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>Вложения отдельными файлами (F4): оба формата, повторы, шифрование, экспорт.</summary>
public sealed class ChatAttachmentFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-att-" + Guid.NewGuid().ToString("N"));

    public ChatAttachmentFilesTests() => Directory.CreateDirectory(Path.Combine(_root, "chats"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Картинка крупнее порога выноса: байты разные, чтобы отпечатки не совпадали случайно.</summary>
    private static string Image(byte seed)
    {
        var bytes = new byte[6000];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31 + seed);
        }

        return Convert.ToBase64String(bytes);
    }

    private static ChatSession Chat(string base64)
    {
        var session = new ChatSession { Id = "c1", Title = "pictures" };
        var user = new ChatDisplayMessage
        {
            Id = "u1",
            Role = "user",
            Text = "look",
            Images = [new ImageAttachment(base64, "image/png", "cat.png")]
        };
        session.Messages.Add(user);
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.ForUser(user, [user], 0) });
        return session;
    }

    private string ChatFile => Path.Combine(_root, "chats", "c1.json");

    private string Folder => Path.Combine(_root, "chats", "c1", "attachments");

    [Fact]
    public void The_same_picture_is_stored_once_and_the_chat_reads_back_whole()
    {
        var base64 = Image(1);
        var store = new ChatStore(_root);
        store.Save(Chat(base64));
        store.Flush();

        var text = File.ReadAllText(ChatFile);
        Assert.DoesNotContain(base64, text);
        Assert.Contains(ChatAttachmentFiles.RefPrefix, text);
        Assert.Single(Directory.GetFiles(Folder, "*.bin"));

        var loaded = new ChatStore(_root).TryLoad("c1")!;
        Assert.Equal(base64, loaded.Messages[0].Images[0].Base64);
        Assert.Contains("data:image/png;base64," + base64, loaded.ApiMessages[0].Content!.Value.GetRawText());
    }

    [Fact]
    public void A_chat_written_before_the_change_is_read_as_is_and_can_be_rewritten()
    {
        var base64 = Image(2);
        var json = System.Text.Json.JsonSerializer.Serialize(Chat(base64), AppJson.Options);
        File.WriteAllText(ChatFile, json);

        Assert.Equal(base64, new ChatStore(_root).TryLoad("c1")!.Messages[0].Images[0].Base64);

        Assert.Equal(1, new ChatStore(_root).ExternalizeExisting(CancellationToken.None));
        Assert.DoesNotContain(base64, File.ReadAllText(ChatFile));
        Assert.Equal(base64, new ChatStore(_root).TryLoad("c1")!.Messages[0].Images[0].Base64);
    }

    [Fact]
    public void Only_the_attachment_strings_change_in_the_file()
    {
        var json = "{\n  \"text\": \"Привет, \\u00ABмир\\u00BB\",\n  \"image\": \"" + Image(3) + "\",\n  \"short\": \"QUJD\"\n}";

        var stored = ChatAttachmentFiles.Externalize(json, out var blobs);

        Assert.Single(blobs);
        Assert.StartsWith("{\n  \"text\": \"Привет, \\u00ABмир\\u00BB\",\n  \"image\": \"amrn-att:", stored);
        Assert.EndsWith("\",\n  \"short\": \"QUJD\"\n}", stored);
        Assert.Equal(json, ChatAttachmentFiles.Inline(stored, sha => blobs.Single(blob => blob.Sha == sha).Bytes));
    }

    [Fact]
    public void A_string_that_would_not_round_trip_stays_in_place()
    {
        // Base64 с переносами строк декодируется, но обратно даст другую строку — не трогаем.
        var wrapped = string.Join("\\n", Enumerable.Range(0, 60).Select(_ => Image(4)[..76]));
        var json = "{\"a\":\"" + wrapped + "\"}";

        Assert.Equal(json, ChatAttachmentFiles.Externalize(json, out var blobs));
        Assert.Empty(blobs);
    }

    [Fact]
    public void Deleting_the_chat_deletes_its_attachments_and_removed_pictures_do_not_linger()
    {
        var store = new ChatStore(_root);
        var session = Chat(Image(5));
        store.Save(session);
        store.Flush();

        session.Messages.Clear();
        session.ApiMessages.Clear();
        session.UpdatedAt = DateTime.Now;
        store.Save(session);
        store.Flush();
        Assert.Empty(Directory.GetFiles(Folder, "*.bin"));

        store.Save(Chat(Image(6)));
        store.Flush();
        store.Delete("c1");
        Assert.False(Directory.Exists(Path.Combine(_root, "chats", "c1")));
    }

    [Fact]
    public void An_encrypted_chat_keeps_its_pictures_encrypted_too()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var base64 = Image(7);
        var store = new ChatStore(_root) { Encrypt = () => true };
        store.Save(Chat(base64));
        store.Flush();

        var blob = File.ReadAllBytes(Directory.GetFiles(Folder, "*.bin").Single());
        Assert.True(AtRestCipher.IsEncrypted(blob));
        Assert.Equal(base64, new ChatStore(_root).TryLoad("c1")!.Messages[0].Images[0].Base64);
    }

    [Fact]
    public void The_data_archive_carries_the_pictures_inside_the_chat()
    {
        var base64 = Image(8);
        var store = new ChatStore(_root);
        store.Save(Chat(base64));
        store.Flush();

        var archive = Path.Combine(_root, "all" + DataBundle.FileExtension);
        new DataBundleExporter(_root).Write(archive, DataCategory.Chats);

        using var zip = ZipFile.OpenRead(archive);
        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("/attachments/", StringComparison.Ordinal));
        using var reader = new StreamReader(zip.Entries.Single(entry => entry.FullName.EndsWith("chats/c1.json", StringComparison.Ordinal)).Open(), Encoding.UTF8);
        Assert.Contains(base64, reader.ReadToEnd());
    }
}
