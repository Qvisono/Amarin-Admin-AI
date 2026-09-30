using System.Net;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// «Удалить все данные»: что стирается у профиля, что остаётся, и кто вправе попросить.
/// </summary>
/// <remarks>Всё на временных папках: настоящий %APPDATA% тесты не трогают.</remarks>
public sealed class ProfileWipeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-wipe-" + Guid.NewGuid().ToString("N"));

    public ProfileWipeTests() => Directory.CreateDirectory(_root);

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

    private void Touch(params string[] relative)
    {
        foreach (var item in relative)
        {
            var path = Path.Combine(_root, item);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    private bool Exists(string relative)
    {
        var path = Path.Combine(_root, relative);
        return File.Exists(path) || Directory.Exists(path);
    }

    private void FillDefaultProfile() => Touch(
        "settings.json", "keys.json", "prompts.json", "avatar.png", "balance.json",
        "background.jpg", "background.0a1b.cache.png", "settings.json.1234abcd.tmp",
        "chats/c1.json", "chats/index.json", "usage/k.json", "instructions/i.md", "shared/chat.amrnchat",
        "audit/2026-09.jsonl",
        // Всё ниже — не профиля по умолчанию, хоть и лежит в его папке.
        "profiles.json", "profiles/abc123/settings.json", "profiles/abc123/chats/c.json",
        "languages/en.xaml");

    [Fact]
    public void The_default_profile_loses_its_own_data_and_nothing_else()
    {
        FillDefaultProfile();

        var result = ProfileDataWiper.Wipe(_root, isDefaultProfile: true, includeAudit: false);

        Assert.True(result.Ok, string.Join(", ", result.Failed));
        foreach (var gone in new[]
                 {
                     "settings.json", "keys.json", "prompts.json", "avatar.png", "balance.json",
                     "background.jpg", "background.0a1b.cache.png", "settings.json.1234abcd.tmp",
                     "chats", "usage", "instructions", "shared"
                 })
        {
            Assert.False(Exists(gone), gone + " пережил стирание");
        }

        // Корень общий: реестр профилей, чужие профили и переводы принадлежат не ему.
        Assert.True(Exists("profiles.json"));
        Assert.True(Exists("profiles/abc123/chats/c.json"));
        Assert.True(Exists("languages/en.xaml"));

        // Журнал аудита — только по галочке.
        Assert.True(Exists("audit/2026-09.jsonl"));
    }

    [Fact]
    public void The_audit_log_goes_only_when_asked()
    {
        FillDefaultProfile();

        ProfileDataWiper.Wipe(_root, isDefaultProfile: true, includeAudit: true);

        Assert.False(Exists("audit"));
        Assert.True(Exists("profiles.json"));
    }

    [Fact]
    public void An_extra_profile_loses_its_whole_folder_but_the_audit_and_its_neighbours()
    {
        Touch(
            "profiles/p1/settings.json", "profiles/p1/chats/c.json", "profiles/p1/whatever.bin",
            "profiles/p1/audit/2026-09.jsonl",
            "profiles/p2/chats/c.json", "settings.json");
        var folder = Path.Combine(_root, "profiles", "p1");

        var result = ProfileDataWiper.Wipe(folder, isDefaultProfile: false, includeAudit: false);

        Assert.True(result.Ok);
        Assert.Equal(["audit"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName));
        Assert.True(Exists("profiles/p2/chats/c.json"));
        Assert.True(Exists("settings.json"));
    }

    [Fact]
    public void A_link_inside_the_profile_does_not_lead_the_wipe_outside()
    {
        Touch("outside/precious.txt", "profiles/p1/chats/c.json");
        var folder = Path.Combine(_root, "profiles", "p1");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "chats", "escape"), Path.Combine(_root, "outside"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Символьные ссылки на Windows без режима разработчика недоступны — проверять нечего.
            return;
        }

        ProfileDataWiper.Wipe(folder, isDefaultProfile: false, includeAudit: true);

        Assert.True(Exists("outside/precious.txt"));
    }

    [Fact]
    public void A_request_with_the_matching_token_wipes_the_named_profile()
    {
        FillDefaultProfile();
        var store = new ProfileStore(_root);
        var registry = store.Load();
        registry.Profiles[0].AvatarFileName = "avatar.png";
        store.Save(registry);

        var token = PendingWipe.Request(_root, ProfileStore.DefaultProfileId, includeAudit: false);
        var result = PendingWipe.Run(_root, token, DateTime.UtcNow);

        Assert.NotNull(result);
        Assert.False(Exists("chats"));
        Assert.False(Exists(PendingWipe.FileName));

        // Ссылка на стёртый аватар из реестра тоже уходит, иначе экран входа искал бы файл.
        Assert.Null(new ProfileStore(_root).Load().Profiles[0].AvatarFileName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public void Without_the_right_token_nothing_is_wiped_and_the_request_is_dropped(string? token)
    {
        // Ярлык с одним ключом --wipe стёр бы данные без вопроса — поэтому нужна ещё и просьба
        // с той же меткой. Несовпавшая просьба убирается, чтобы не сработать позже.
        FillDefaultProfile();
        PendingWipe.Request(_root, ProfileStore.DefaultProfileId, includeAudit: true);

        Assert.Null(PendingWipe.Run(_root, token, DateTime.UtcNow));
        Assert.True(Exists("chats/c1.json"));
        Assert.False(Exists(PendingWipe.FileName));
    }

    [Fact]
    public void A_stale_request_is_not_carried_out()
    {
        FillDefaultProfile();
        var token = PendingWipe.Request(_root, ProfileStore.DefaultProfileId, includeAudit: false);

        Assert.Null(PendingWipe.Run(_root, token, DateTime.UtcNow + PendingWipe.MaxAge + TimeSpan.FromMinutes(1)));
        Assert.True(Exists("chats/c1.json"));
    }

    [Fact]
    public void A_token_without_a_request_does_nothing()
    {
        FillDefaultProfile();

        Assert.Null(PendingWipe.Run(_root, "0123456789ABCDEF0123456789ABCDEF", DateTime.UtcNow));
        Assert.True(Exists("chats/c1.json"));
    }

    [Fact]
    public void The_token_is_read_from_the_command_line()
    {
        Assert.Equal("abc", StartupArgs.Parse(["--await-exit", "42", "--wipe", "abc"]).WipeToken);
        Assert.Equal("abc", StartupArgs.Parse(["--wipe=abc"]).WipeToken);
        Assert.Null(StartupArgs.Parse(["--wipe", " "]).WipeToken);
        Assert.Null(StartupArgs.Parse([]).WipeToken);
    }

    [Theory]
    [InlineData("УДАЛИТЬ", "УДАЛИТЬ", true)]
    [InlineData("  удалить ", "УДАЛИТЬ", true)]
    [InlineData("удали", "УДАЛИТЬ", false)]
    [InlineData("", "УДАЛИТЬ", false)]
    [InlineData(null, "DELETE", false)]
    public void The_confirmation_word_is_matched_loosely_but_whole(string? typed, string word, bool matches) =>
        Assert.Equal(matches, ProfileDataWiper.WordMatches(typed, word));
}

/// <summary>Два вопроса перед стиранием — на живом окне со своими службами.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ProfileWipeUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-wipe-ui-" + Guid.NewGuid().ToString("N"));

    public ProfileWipeUiTests(WpfFixture wpf) => _wpf = wpf;

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

    private T With<T>(Func<MainWindow, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "test", new NoNetwork());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window);
        }
        finally
        {
            window.Close();
        }
    });

    private static void CloseNotice(MainWindow window, bool confirmed) =>
        typeof(MainWindow).GetMethod("CloseNotice", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [confirmed]);

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void The_second_step_is_locked_until_the_word_is_typed_and_the_audit_box_is_honoured()
    {
        var (lockedAtFirst, unlocked, answer) = With(window =>
        {
            var asked = window.AskWipeAsync();

            // Шаг первый: галочка журнала аудита снята по умолчанию — ставим и идём дальше.
            var extra = (ContentControl)window.FindName("NoticeExtra")!;
            var audit = Assert.IsType<CheckBox>(extra.Content);
            Assert.False(audit.IsChecked);
            audit.IsChecked = true;
            CloseNotice(window, true);
            Pump();

            // Шаг второй: пока слово не набрано, основная кнопка неактивна.
            var primary = (Button)window.FindName("NoticePrimaryButton")!;
            var locked = !primary.IsEnabled;
            var input = (TextBox)((Border)((ContentControl)window.FindName("NoticeExtra")!).Content).Child;
            input.Text = Loc.Get("S.Wipe.Word").ToLowerInvariant();
            var open = primary.IsEnabled;

            CloseNotice(window, true);
            Pump();
            Assert.True(asked.IsCompleted);
            return (locked, open, asked.Result);
        });

        Assert.True(lockedAtFirst, "необратимое стирание нажималось бы по инерции");
        Assert.True(unlocked);
        Assert.True(answer);
    }

    [Fact]
    public void Backing_out_at_either_step_wipes_nothing()
    {
        var (first, second) = With(window =>
        {
            var cancelled = window.AskWipeAsync();
            CloseNotice(window, false);
            Pump();

            var atWord = window.AskWipeAsync();
            CloseNotice(window, true);
            Pump();
            CloseNotice(window, false);
            Pump();
            return (cancelled.Result, atWord.Result);
        });

        Assert.Null(first);
        Assert.Null(second);
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
