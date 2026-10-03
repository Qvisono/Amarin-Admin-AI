using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Черновики чатов (D12): файл, крупные вложения отдельно, шифрование, уборка.</summary>
public sealed class DraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-drafts-" + Guid.NewGuid().ToString("N"));

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

    private static string Base64(int bytes) => Convert.ToBase64String(Enumerable.Range(0, bytes).Select(i => (byte)(i % 251)).ToArray());

    [Fact]
    public void A_draft_comes_back_whole()
    {
        var store = new DraftStore(_root, () => false);
        var big = Base64(400_000);
        store.Save("chat1", new ChatDraftContent(
            "half written",
            [new MessageQuote { Number = 1, SourceMessageId = "a1", Text = "quoted" }],
            [new ImageAttachment(Base64(100), "image/png", "pic")],
            [new FileAttachment(big, "application/pdf", "report.pdf", 300_000, @"C:\x\report.pdf")]));

        var loaded = new DraftStore(_root, () => false).TryLoad("chat1")!;

        Assert.Equal("half written", loaded.Text);
        Assert.Equal("quoted", Assert.Single(loaded.Quotes).Text);
        Assert.Equal("pic", Assert.Single(loaded.Images).Label);
        Assert.Equal(big, Assert.Single(loaded.Files).Base64);

        // Крупное вложение — отдельным файлом, а не внутри черновика.
        Assert.True(new FileInfo(Path.Combine(_root, "drafts", "chat1.json")).Length < 10_000);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "drafts", "files")));
    }

    [Fact]
    public void An_empty_draft_is_removed_with_its_attachment_files()
    {
        var store = new DraftStore(_root, () => false);
        store.Save("chat1", new ChatDraftContent("x", [], [], [new FileAttachment(Base64(400_000), "a/b", "f", 1)]));

        store.Save("chat1", new ChatDraftContent("   ", [], [], []));

        Assert.Null(store.TryLoad("chat1"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "drafts", "files")));
    }

    [Fact]
    public void The_same_attachment_in_two_drafts_is_stored_once()
    {
        var store = new DraftStore(_root, () => false);
        var file = new FileAttachment(Base64(400_000), "a/b", "f", 1);
        store.Save("chat1", new ChatDraftContent("1", [], [], [file]));
        store.Save("chat2", new ChatDraftContent("2", [], [], [file]));

        Assert.Single(Directory.GetFiles(Path.Combine(_root, "drafts", "files")));

        store.Delete("chat1");
        Assert.Equal(file.Base64, store.TryLoad("chat2")!.Files.Single().Base64);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("")]
    public void A_key_that_is_not_a_chat_id_is_refused(string key)
    {
        var store = new DraftStore(_root, () => false);
        store.Save(key, new ChatDraftContent("x", [], [], []));

        Assert.Null(store.TryLoad(key));
        Assert.False(Directory.Exists(Path.Combine(_root, "drafts")) &&
                     Directory.GetFiles(Path.Combine(_root, "drafts")).Length > 0);
    }

    [Fact]
    public void Pruning_keeps_live_chats_and_the_new_chat()
    {
        var store = new DraftStore(_root, () => false);
        store.Save("alive", new ChatDraftContent("a", [], [], []));
        store.Save("gone", new ChatDraftContent("g", [], [], []));
        store.Save(DraftStore.NewChatKey, new ChatDraftContent("n", [], [], []));

        store.Prune(new HashSet<string> { "alive" });

        Assert.NotNull(store.TryLoad("alive"));
        Assert.Null(store.TryLoad("gone"));
        Assert.NotNull(store.TryLoad(DraftStore.NewChatKey));
    }

    [Fact]
    public void With_encrypted_chats_the_draft_is_not_readable_on_disk()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new DraftStore(_root, () => true);
        store.Save("chat1", new ChatDraftContent("topsecretdraftword", [], [], []));

        var bytes = File.ReadAllBytes(Path.Combine(_root, "drafts", "chat1.json"));
        Assert.True(AtRestCipher.IsEncrypted(bytes));
        Assert.DoesNotContain("topsecretdraftword", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("topsecretdraftword", store.TryLoad("chat1")!.Text);
    }

    [Fact]
    public void A_noted_draft_comes_back_before_the_disk_has_it()
    {
        // Открытие чата больше не ждёт очереди записи: отмеченное окном отдаётся из памяти.
        var store = new DraftStore(_root, () => false);
        var draft = new ChatDraftContent("ещё не на диске", [], [], []);

        store.Note("chat1", draft);

        Assert.False(File.Exists(Path.Combine(_root, "drafts", "chat1.json")));
        Assert.Same(draft, store.TryLoad("chat1"));
    }

    [Fact]
    public void A_late_write_of_an_older_draft_does_not_replace_the_noted_one()
    {
        // Очередь записи догоняет окно: старый черновик ложится на диск после того, как окно
        // отметило новый, — открыть обязан новый.
        var store = new DraftStore(_root, () => false);
        var older = new ChatDraftContent("старый", [], [], []);
        var newer = new ChatDraftContent("новый", [], [], []);

        store.Note("chat1", older);
        store.Note("chat1", newer);
        store.Save("chat1", older);

        Assert.Equal("новый", store.TryLoad("chat1")?.Text);
    }

    [Fact]
    public void A_noted_removal_and_a_deleted_chat_leave_no_draft()
    {
        var store = new DraftStore(_root, () => false);
        store.Save("chat1", new ChatDraftContent("на диске", [], [], []));

        store.Note("chat1", null);
        Assert.Null(store.TryLoad("chat1"));

        store.Note("chat2", new ChatDraftContent("в памяти", [], [], []));
        store.Forget("chat2");
        Assert.Null(store.TryLoad("chat2"));

        // Другой профиль — другие черновики: отметки прежнего не переезжают.
        store.Note("chat3", new ChatDraftContent("прежний профиль", [], [], []));
        store.UseRoot(Path.Combine(_root, "other"), () => false);
        Assert.Null(store.TryLoad("chat3"));
    }

    [Fact]
    public void Drafts_do_not_travel_in_the_data_archive()
    {
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("drafts/chat1.json"));
        Assert.Equal(DataCategory.None, DataBundle.CategoryOf("drafts/files/abc"));
    }
}

/// <summary>Черновик на живом окне: набранное остаётся со своим чатом.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DraftUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-drafts-ui-" + Guid.NewGuid().ToString("N"));

    public DraftUiTests(WpfFixture wpf) => _wpf = wpf;

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

    private static void Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    [Fact]
    public void Typed_text_stays_with_its_chat_when_switching()
    {
        var (inOther, back) = _wpf.Ui.Invoke(() =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            foreach (var id in new[] { "chatA", "chatB" })
            {
                services.ChatStore.Save(new ChatSession { Id = id, Title = id, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
            }

            services.ChatStore.Flush();
            var window = new MainWindow();
            window.AttachServices(services);
            try
            {
                var box = (TextBox)window.FindName("MessageTextBox");
                Call(window, "OpenChat", "chatA");
                box.Text = "unsent words for A";
                Call(window, "OpenChat", "chatB");
                var other = box.Text;
                Call(window, "OpenChat", "chatA");
                return (other, box.Text);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal("", inOther);
        Assert.Equal("unsent words for A", back);
    }
}
