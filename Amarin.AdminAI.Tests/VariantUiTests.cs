using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Варианты ответа в окне: «Повторить» и правка оставляют прежнее соседним вариантом,
/// переключатель листает их, не трогая лупы, и гаснет, пока чат отвечает.
/// </summary>
/// <remarks>
/// Окно своё, не общее для коллекции: ему нужны настоящие службы — по образцу
/// <see cref="ParallelChatUiTests"/>. Оно не показывается и закрывается в finally.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class VariantUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly List<string> _roots = [];

    public VariantUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        foreach (var root in _roots)
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

    // ───────────────────────── оснастка ─────────────────────────

    private sealed record Harness(MainWindow Window, AppServices Services);

    private Harness Build(string apiKey, Func<string, HttpResponseMessage> script)
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-variants-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        _roots.Add(root);

        var options = new AgentOptions
        {
            ApiKey = apiKey,
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        var http = new HttpClient(new ScriptedHandler(script)) { BaseAddress = new Uri("https://api.venice.ai/api/v1/") };
        var download = new HttpClient { BaseAddress = new Uri("https://example.invalid/") };
        var venice = new VeniceClient(http, options);
        var settingsStore = new AppSettingsStore(root);
        var settings = settingsStore.Load();

        var services = new AppServices
        {
            Options = options,
            SettingsStore = settingsStore,
            Settings = settings,
            ChatStore = new ChatStore(root),
            Prompts = new PromptLibrary(root),
            Instructions = new InstructionLibrary(root),
            KeyStore = new ApiKeyStore(root),
            Ledger = new SpendLedger(root),
            Keys = new ApiKeyProvider(),
            EnvironmentKey = "",
            Profiles = new ProfileStore(),
            ProfileRegistry = new ProfileRegistry(),
            Http = http,
            DownloadHttp = download,
            Venice = venice,
            Models = new VeniceModelListCache(venice),
            Balances = new BalanceBook(),
            Chat = new ChatEngine(venice, options, () => settings, new ToolRegistry([])),
            Titles = new ChatTitleGenerator(http, options, () => settings),
            Summaries = new ChatSummaryGenerator(http, options, () => settings),
            Confirmations = new ConfirmationQueue(() => settings)
        };

        var window = new MainWindow();
        window.AttachServices(services);
        return new Harness(window, services);
    }

    private T With<T>(string apiKey, Func<Harness, T> body) => _wpf.Ui.Invoke(() =>
    {
        var harness = Build(apiKey, _ => Sse("новый ответ"));
        try
        {
            return body(harness);
        }
        finally
        {
            harness.Window.Close();
        }
    });

    private static void Set(MainWindow window, string field, object? value) =>
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private static T Get<T>(MainWindow window, string field) =>
        (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static ChatSession Chat()
    {
        var session = new ChatSession
        {
            Id = "variants-" + Guid.NewGuid().ToString("N"),
            Title = "Варианты",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            SelectedModelId = "grok-4-6"
        };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "первый вопрос", CreatedAt = DateTime.Now });
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant", Id = "a1", Text = "первый ответ", Status = AssistantStatus.Complete, CreatedAt = DateTime.Now
        });
        session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("первый вопрос") });
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("первый ответ") });
        return session;
    }

    /// <summary>Чат, в котором у первого ответа уже есть второй вариант, и он показан.</summary>
    private static ChatSession ChatWithTwoAnswers(out ChatDisplayMessage second)
    {
        var session = Chat();
        second = new ChatDisplayMessage
        {
            Role = "assistant", Id = "a1b", Text = "второй ответ", Status = AssistantStatus.Complete, CreatedAt = DateTime.Now
        };
        ChatBranches.Fork(session, 1, second);
        session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("второй ответ") });
        return session;
    }

    private static void Open(MainWindow window, ChatSession session)
    {
        Set(window, "_session", session);
        Call(window, "RenderSession");
        window.UpdateLayout();
    }

    /// <summary>Крутит очередь потока интерфейса, пока условие не сбудется или не выйдет время.</summary>
    private static void WaitUntil(Func<bool> done, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!done() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(15),
                DispatcherPriority.Background,
                (sender, _) =>
                {
                    ((DispatcherTimer)sender!).Stop();
                    frame.Continue = false;
                },
                Dispatcher.CurrentDispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    /// <summary>Переключатель под сообщением: «‹», счётчик, «›».</summary>
    private static StackPanel Switcher(MainWindow window, string messageId)
    {
        var host = Get<Dictionary<string, ChatMessageHost>>(window, "_messageViews")[messageId];
        return Find<StackPanel>(host, panel => panel.Name == ChatMessageViews.VariantSwitcherName)
               ?? throw new InvalidOperationException("переключатель не найден");
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool> match) where T : class
    {
        if (root is T candidate && match(candidate))
        {
            return candidate;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (Find(child, match) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ───────────────────────── тесты ─────────────────────────

    [Fact]
    public void Regenerating_keeps_the_old_answer_as_a_variant_and_shows_the_switcher()
    {
        var (ids, text, variants, visible, label, previous, next) = With("test", harness =>
        {
            var session = Chat();
            Open(harness.Window, session);

            Call(harness.Window, "RegenerateAssistant", session.Messages[1]);
            WaitUntil(() => !harness.Window.IsBusy(session.Id));
            harness.Window.UpdateLayout();

            var anchor = session.Messages[^1];
            var switcher = Switcher(harness.Window, anchor.Id);
            return (
                session.Messages.Select(m => m.Id).ToList(),
                anchor.Text,
                anchor.VariantCount,
                switcher.Visibility,
                ((TextBlock)switcher.Children[1]).Text,
                switcher.Children[0].IsEnabled,
                switcher.Children[2].IsEnabled);
        });

        Assert.Equal(2, ids.Count);
        Assert.Equal("u1", ids[0]);
        Assert.NotEqual("a1", ids[1]);
        Assert.Equal("новый ответ", text);
        Assert.Equal(2, variants);
        Assert.Equal(Visibility.Visible, visible);
        Assert.Equal("2/2", label);
        Assert.True(previous, "к прежнему ответу вернуться нельзя");
        Assert.False(next, "стрелка вперёд на последнем варианте должна гаснуть");
    }

    [Fact]
    public void Regenerate_without_a_key_keeps_the_answer_and_says_where_to_add_one()
    {
        var (ids, variants, noticeOpen, title) = With("", harness =>
        {
            var session = Chat();
            Open(harness.Window, session);

            Call(harness.Window, "RegenerateAssistant", session.Messages[1]);
            WaitUntil(() => harness.Window.IsNoticeOpen, 2000);

            var result = (
                session.Messages.Select(m => m.Id).ToList(),
                session.Messages[1].VariantCount,
                harness.Window.IsNoticeOpen,
                ((TextBlock)harness.Window.FindName("NoticeTitle")!).Text);
            Call(harness.Window, "CloseNotice", false);
            return result;
        });

        Assert.Equal(["u1", "a1"], ids);
        Assert.Equal(1, variants);
        Assert.True(noticeOpen, "без ключа человек не узнал бы, почему ничего не случилось");
        Assert.Equal(Loc.Get("S.Notice.NoKeyTitle"), title);
    }

    [Fact]
    public void Switching_versions_keeps_the_zoom_and_shows_the_other_answer()
    {
        var (scale, lastId, label, previous, next) = With("test", harness =>
        {
            var session = ChatWithTwoAnswers(out var second);
            Open(harness.Window, session);
            var layer = (ChatZoomHost)harness.Window.FindName("ChatZoomLayer")!;
            layer.Scale = 2.0;

            Call(harness.Window, "SwitchVariant", second, 0);
            harness.Window.UpdateLayout();

            var panel = (Panel)harness.Window.FindName("MessagesPanel")!;
            var last = (ChatMessageHost)panel.Children[^1];
            var switcher = Switcher(harness.Window, "a1");
            return (
                layer.Scale,
                last.Id,
                ((TextBlock)switcher.Children[1]).Text,
                switcher.Children[0].IsEnabled,
                switcher.Children[2].IsEnabled);
        });

        Assert.Equal(2.0, scale, 3);
        Assert.Equal("a1", lastId);
        Assert.Equal("1/2", label);
        Assert.False(previous, "стрелка назад на первом варианте должна гаснуть");
        Assert.True(next);
    }

    [Fact]
    public void A_single_version_shows_no_switcher()
    {
        var visibility = With("test", harness =>
        {
            Open(harness.Window, Chat());
            return Switcher(harness.Window, "a1").Visibility;
        });

        Assert.Equal(Visibility.Collapsed, visibility);
    }

    [Fact]
    public void The_arrows_go_dark_while_the_chat_answers()
    {
        var (during, after) = With("test", harness =>
        {
            var session = ChatWithTwoAnswers(out _);
            Open(harness.Window, session);
            var switcher = Switcher(harness.Window, "a1b");

            var turns = Get<Dictionary<string, RunningTurn>>(harness.Window, "_turns");
            turns[session.Id] = new RunningTurn
            {
                Session = session,
                Cancellation = new CancellationTokenSource(),
                Kind = TurnKind.Send,
                StartedAt = DateTime.Now
            };
            Call(harness.Window, "UpdateComposerChrome");
            var enabledDuring = switcher.IsEnabled;

            turns.Remove(session.Id);
            Call(harness.Window, "UpdateComposerChrome");
            return (enabledDuring, switcher.IsEnabled);
        });

        Assert.False(during, "переключение посреди ответа вырезало бы его из-под движка");
        Assert.True(after);
    }

    [Fact]
    public void Deleting_a_version_that_later_turns_continued_asks_first()
    {
        var (askedIds, askedOpen, keptIds, variants) = With("test", harness =>
        {
            var session = ChatWithTwoAnswers(out var second);
            session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "дальше", CreatedAt = DateTime.Now });
            session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("дальше") });
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = "assistant", Id = "a2", Text = "продолжение", Status = AssistantStatus.Complete, CreatedAt = DateTime.Now
            });
            session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("продолжение") });
            Open(harness.Window, session);

            Call(harness.Window, "DeleteAssistant", second);
            WaitUntil(() => harness.Window.IsNoticeOpen, 2000);
            var idsWhileAsking = session.Messages.Select(m => m.Id).ToList();
            var open = harness.Window.IsNoticeOpen;

            Call(harness.Window, "CloseNotice", true);
            WaitUntil(() => session.Messages.Count == 2, 2000);

            return (idsWhileAsking, open, session.Messages.Select(m => m.Id).ToList(), session.Messages[1].VariantCount);
        });

        Assert.Equal(["u1", "a1b", "u2", "a2"], askedIds);
        Assert.True(askedOpen, "вариант унёс бы с собой следующий ход без вопроса");
        Assert.Equal(["u1", "a1"], keptIds);
        Assert.Equal(1, variants);
    }

    [Fact]
    public void Editing_a_question_keeps_the_old_one_as_a_version_with_its_attachments()
    {
        var file = new FileAttachment("JVBERg==", "application/pdf", "договор.pdf", 4);
        var (ids, text, files, oldIds) = With("test", harness =>
        {
            var session = Chat();
            session.Messages[0].Files = [file];
            Open(harness.Window, session);

            Call(harness.Window, "CommitUserEdit", session.Messages[0], "исправленный вопрос");
            WaitUntil(() => !harness.Window.IsBusy(session.Id) && session.Messages.Count == 2 &&
                            session.Messages[1].Status == AssistantStatus.Complete);

            var anchor = session.Messages[0];
            return (
                session.Messages.Select(m => m.Role).ToList(),
                anchor.Text,
                anchor.Files.Select(f => f.FileName).ToList(),
                anchor.Variants?.Single().Messages.Select(m => m.Id).ToList());
        });

        Assert.Equal(["user", "assistant"], ids);
        Assert.Equal("исправленный вопрос", text);
        Assert.Equal(["договор.pdf"], files);
        Assert.Equal(["u1", "a1"], oldIds);
    }

    [Fact]
    public void A_message_added_ready_made_leaves_nothing_counted_as_unbuilt()
    {
        // Новый ответ и якорь варианта встают в ленту готовыми. Прежде счётчик недостроенных
        // от них рос и не убывал, и фоновая дорисовка крутилась вхолостую до смены чата.
        var (before, after) = With("test", harness =>
        {
            var session = Chat();
            Open(harness.Window, session);
            WaitUntil(() => Get<int>(harness.Window, "_unbuiltMessages") == 0, 3000);
            var counted = Get<int>(harness.Window, "_unbuiltMessages");

            var extra = new ChatDisplayMessage { Role = "user", Id = "u9", Text = "ещё", CreatedAt = DateTime.Now };
            session.Messages.Add(extra);
            Call(harness.Window, "AppendMessage", extra, new TextBlock { Text = "ещё" });
            return (counted, Get<int>(harness.Window, "_unbuiltMessages"));
        });

        Assert.Equal(0, before);
        Assert.Equal(0, after);
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
}
