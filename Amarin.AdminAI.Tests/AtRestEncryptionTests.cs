using System.IO.Compression;
using System.Text;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Переписка на диске под DPAPI: файл не несёт открытого текста, читаются оба формата, смена
/// галочки переписывает файлы в фоне, а архив данных увозит их открытыми.
/// </summary>
/// <remarks>
/// DPAPI есть только в Windows. Вне её такие проверки выходят сразу: там программа не работает,
/// а на настоящей машине и на раннере Windows они идут целиком.
/// </remarks>
public sealed class AtRestEncryptionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-atrest-" + Guid.NewGuid().ToString("N"));

    public AtRestEncryptionTests() => Directory.CreateDirectory(Path.Combine(_root, "chats"));

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

    /// <summary>
    /// Латиницей нарочно: JSON пишет кириллицу как <c>\u043F…</c>, и поиск русской строки в
    /// файле не нашёл бы её и в открытом тексте — проверка «шифр прячет текст» прошла бы и без
    /// шифра.
    /// </summary>
    private const string Secret = "router-pass 7fq-Kd9-unique";

    private static ChatSession Session(string id = "c1")
    {
        var session = new ChatSession
        {
            Id = id,
            Title = "Роутер",
            Summary = "Сводка: " + Secret,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = Secret });
        return session;
    }

    private byte[] ChatFile(string id = "c1") => File.ReadAllBytes(Path.Combine(_root, "chats", id + ".json"));

    private static bool Contains(byte[] haystack, string text) =>
        haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    [Fact]
    public void Plain_text_files_of_earlier_versions_still_read()
    {
        // Без метки файл — обычный JSON, как его писали всегда (с BOM или без).
        var text = "{\"id\":\"x\",\"title\":\"старый\"}";

        Assert.Equal(text, AtRestCipher.DecryptFile(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(text, AtRestCipher.DecryptFile([.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)]));
        Assert.Equal("{\"a\":1}", AtRestCipher.DecryptLine("{\"a\":1}"));
    }

    [Fact]
    public void An_encrypted_chat_carries_no_plain_text_and_loads_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new ChatStore(_root) { Encrypt = () => true };
        store.Save(Session());
        store.Flush();

        var onDisk = ChatFile();
        var index = File.ReadAllBytes(Path.Combine(_root, "chats", "index.json"));
        Assert.True(AtRestCipher.IsEncrypted(onDisk));
        Assert.False(Contains(onDisk, Secret));

        // В описи лежат заголовки и сводки — ей шифр нужен не меньше.
        Assert.True(AtRestCipher.IsEncrypted(index));
        Assert.False(Contains(index, Secret));

        var fresh = new ChatStore(_root) { Encrypt = () => true };
        Assert.Equal(Secret, fresh.TryLoad("c1")!.Messages[0].Text);
        Assert.Equal("Роутер", Assert.Single(fresh.List()).Title);
    }

    [Fact]
    public async Task Turning_encryption_on_and_off_rewrites_existing_files_both_ways()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var encrypt = false;
        var store = new ChatStore(_root) { Encrypt = () => encrypt };
        store.Save(Session("c1"));
        store.Save(Session("c2"));
        store.Flush();
        Assert.False(AtRestCipher.IsEncrypted(ChatFile("c1")));

        encrypt = true;
        await store.EnsureFormat();
        Assert.True(AtRestCipher.IsEncrypted(ChatFile("c1")));
        Assert.True(AtRestCipher.IsEncrypted(ChatFile("c2")));
        Assert.True(AtRestCipher.IsEncrypted(File.ReadAllBytes(Path.Combine(_root, "chats", "index.json"))));

        encrypt = false;
        await store.EnsureFormat();
        Assert.False(AtRestCipher.IsEncrypted(ChatFile("c1")));
        Assert.True(Contains(ChatFile("c2"), Secret));
        Assert.Equal(Secret, new ChatStore(_root).TryLoad("c2")!.Messages[0].Text);
    }

    [Fact]
    public void An_unchanged_chat_is_rewritten_when_the_format_changes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Отпечаток записанного включает формат: иначе нетронутый чат так и лежал бы открытым.
        var encrypt = false;
        var store = new ChatStore(_root) { Encrypt = () => encrypt };
        var session = Session();
        store.Save(session);
        store.Flush();

        encrypt = true;
        store.Save(session);
        store.Flush();

        Assert.True(AtRestCipher.IsEncrypted(ChatFile()));
    }

    [Fact]
    public async Task A_file_deleted_during_the_rewrite_is_not_brought_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var encrypt = false;
        var store = new ChatStore(_root) { Encrypt = () => encrypt };
        store.Save(Session("c1"));
        store.Flush();

        encrypt = true;
        store.Delete("c1");
        await store.EnsureFormat();

        Assert.False(File.Exists(Path.Combine(_root, "chats", "c1.json")));
    }

    [Fact]
    public void Encrypted_audit_lines_read_back_and_hide_the_arguments()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var audit = new AuditLog(_root) { Encrypt = () => true };
        audit.Record(null, "call-1", "write_file", "{\"path\":\"C:\\\\" + Secret + ".txt\"}",
            ToolEffect.Write, AuditOutcome.Ok, ApprovalSource.Human, AuditGuard.Off, "ok");

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "audit")));
        Assert.False(Contains(File.ReadAllBytes(file), Secret));
        Assert.StartsWith(AtRestCipher.LinePrefix, File.ReadAllText(file));
        Assert.Contains(Secret, Assert.Single(audit.ReadAll()).Args);
    }

    [Fact]
    public void The_data_archive_carries_encrypted_chats_and_audit_in_the_open()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // DPAPI привязан к учётной записи: на другой машине зашифрованный чат не прочтёт никто,
        // и архив ради переноса стал бы бесполезен.
        var store = new ChatStore(_root) { Encrypt = () => true };
        store.Save(Session());
        store.Flush();
        var audit = new AuditLog(_root) { Encrypt = () => true };
        audit.Record(null, "call-1", "write_file", "{\"note\":\"" + Secret + "\"}",
            ToolEffect.Write, AuditOutcome.Ok, ApprovalSource.Human, AuditGuard.Off, "ok");

        var archive = Path.Combine(_root, "out.amrnbak");
        new DataBundleExporter(_root).Write(archive, DataCategory.Chats | DataCategory.Audit);

        using var zip = ZipFile.OpenRead(archive);
        var chat = zip.Entries.Single(entry => entry.FullName.EndsWith("chats/c1.json", StringComparison.Ordinal));
        using (var reader = new StreamReader(chat.Open()))
        {
            Assert.Contains(Secret, reader.ReadToEnd());
        }

        var line = zip.Entries.Single(entry => entry.FullName.EndsWith(".jsonl", StringComparison.Ordinal));
        using (var reader = new StreamReader(line.Open()))
        {
            var text = reader.ReadToEnd();
            Assert.DoesNotContain(AtRestCipher.LinePrefix, text);
            Assert.NotNull(AuditLog.Parse(text.Split('\n')[0]));
        }
    }

    [Fact]
    public void An_imported_archive_does_not_switch_off_encryption_or_auto_lock()
    {
        var settings = new AppSettingsStore(_root);
        var mine = settings.Load();
        mine.EncryptChats = true;
        mine.AutoLockMinutes = 15;
        settings.Save(mine);

        var source = Path.Combine(_root, "other");
        Directory.CreateDirectory(source);
        var theirs = new AppSettingsStore(source);
        var incoming = theirs.Load();
        incoming.EncryptChats = false;
        incoming.AutoLockMinutes = 0;
        theirs.Save(incoming);

        var archive = Path.Combine(_root, "settings.amrnbak");
        new DataBundleExporter(source).Write(archive, DataCategory.Settings);
        new DataBundleImporter(_root).Apply(archive, DataCategory.Settings, DataImportMode.Replace);

        var after = new AppSettingsStore(_root).Load();
        Assert.True(after.EncryptChats);
        Assert.Equal(15, after.AutoLockMinutes);
    }
}
