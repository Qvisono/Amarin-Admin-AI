using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Пока шёл ответ, программа была заперта: переключение чата отменяло ход, а список чатов и
/// кнопка «Новый чат» гасли. Эти тесты держат новое поведение — несколько чатов отвечают
/// одновременно, и возврат в отвечающий чат подхватывает его ответ с того места, где он идёт.
/// </summary>
/// <remarks>
/// Окно тут своё, не общее для коллекции: ему нужны настоящие службы, а подменять их у окна,
/// с которым работают соседние тесты, нельзя. Оно не показывается и закрывается в finally —
/// иначе <c>Windows.OfType&lt;MainWindow&gt;().Single()</c> в соседних тестах увидел бы два.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ParallelChatUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly List<string> _roots = [];

    public ParallelChatUiTests(WpfFixture wpf) => _wpf = wpf;

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

    private Harness Build(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> script)
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-turns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "chats"));
        _roots.Add(root);

        var options = new AgentOptions
        {
            ApiKey = "test",
            BaseUrl = "https://api.venice.ai/api/v1",
            Model = "grok-4-6",
            MaxToolRounds = 2
        };

        var http = new HttpClient(new AsyncHandler(script))
        {
            BaseAddress = new Uri("https://api.venice.ai/api/v1/")
        };
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

    private T With<T>(
        Func<HttpRequestMessage, string, Task<HttpResponseMessage>> script,
        Func<Harness, T> body) => _wpf.Ui.Invoke(() =>
    {
        var harness = Build(script);
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
        typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, value);

    private static T Get<T>(MainWindow window, string field) =>
        (T)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static Dictionary<string, RunningTurn> Turns(MainWindow window) =>
        Get<Dictionary<string, RunningTurn>>(window, "_turns");

    private static ChatSession Session(string id, string title = "Чат")
    {
        var session = new ChatSession
        {
            Id = id,
            Title = title,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            SelectedModelId = "grok-4-6"
        };
        return session;
    }

    /// <summary>Регистрирует ход вручную — так проверяется UI, не поднимая движок.</summary>
    private static RunningTurn Register(MainWindow window, ChatSession session, DateTime? startedAt = null)
    {
        var turn = new RunningTurn
        {
            Session = session,
            Cancellation = new CancellationTokenSource(),
            Kind = TurnKind.Send,
            StartedAt = startedAt ?? DateTime.Now
        };
        Turns(window)[session.Id] = turn;
        return turn;
    }

    // ───────────────────────── тесты ─────────────────────────

    [Fact]
    public void A_turn_is_registered_while_it_runs_and_gone_after()
    {
        var release = new TaskCompletionSource();
        var (busyDuring, busyAfter) = With(
            async (_, body) =>
            {
                await release.Task;
                return Sse("ответ", ModelOf(body));
            },
            harness =>
            {
                var session = Session("s1");
                Set(harness.Window, "_session", session);

                var running = (Task)Call(harness.Window, "RunTurnAsync", session, TurnKind.Send,
                    (Func<ChatSession, IChatTurnObserver, CancellationToken, Task>)((chat, observer, token) =>
                        harness.Services.Chat.RunTurnAsync(chat, "привет", observer, token)))!;

                var during = harness.Window.IsBusy("s1");
                release.SetResult();
                Pump(running);
                return (during, harness.Window.IsBusy("s1"));
            });

        Assert.True(busyDuring, "ход не зарегистрирован, пока идёт");
        Assert.False(busyAfter, "ход остался в реестре после завершения");
    }

    [Fact]
    public void Switching_away_does_not_cancel_the_turn()
    {
        var cancelled = With(
            (_, body) => Task.FromResult(Sse("ответ", ModelOf(body))),
            harness =>
            {
                var background = Session("s1", "Фоновый");
                var opened = Session("s2", "Открытый");
                harness.Services.ChatStore.Save(background);
                harness.Services.ChatStore.Save(opened);

                Set(harness.Window, "_session", background);
                var turn = Register(harness.Window, background);

                // Уходим в другой чат — прежде это звало CancelTurn() и обрывало ответ.
                Call(harness.Window, "OpenChat", "s2");

                return (turn.Cancellation.IsCancellationRequested, harness.Window.IsBusy("s1"));
            });

        Assert.False(cancelled.IsCancellationRequested, "переключение чата отменило ход");
        Assert.True(cancelled.Item2, "ход пропал из реестра после переключения");
    }

    [Fact]
    public void Reopening_a_live_chat_does_not_reload_it_from_disk()
    {
        // Самая опасная точка: копия с диска — другой объект, и движок продолжил бы писать
        // в «сироту», пока на экране показывают застывший снимок.
        var same = With(
            (_, body) => Task.FromResult(Sse("ответ", ModelOf(body))),
            harness =>
            {
                var live = Session("s1", "Живой");
                live.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
                harness.Services.ChatStore.Save(live);

                var other = Session("s2", "Другой");
                harness.Services.ChatStore.Save(other);

                Set(harness.Window, "_session", other);
                var turn = Register(harness.Window, live);

                Call(harness.Window, "OpenChat", "s1");
                return ReferenceEquals(Get<ChatSession>(harness.Window, "_session"), turn.Session);
            });

        Assert.True(same, "открытый чат - копия с диска, а не сессия идущего хода");
    }

    [Fact]
    public void Returning_to_a_streaming_chat_rebinds_the_live_view()
    {
        var (body, cancelVisible) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var live = Session("s1", "Живой");
                var assistant = new ChatDisplayMessage
                {
                    Role = "assistant",
                    Id = "a1",
                    Status = AssistantStatus.Streaming,
                    Text = "первый кусок и второй кусок"
                };
                live.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
                live.Messages.Add(assistant);

                var turn = Register(harness.Window, live);
                turn.Assistant = assistant;
                turn.AssistantId = assistant.Id;
                turn.PendingText = assistant.Text;

                Set(harness.Window, "_session", live);
                Call(harness.Window, "RenderSession");

                var view = Get<AssistantMessageView>(harness.Window, "_liveAssistant");
                Assert.NotNull(view);
                return (turn.RenderedText, view.CancelButton.Visibility);
            });

        // Текст перерисован сразу: у хода, который уже дописал ответ и крутит инструменты,
        // новых дельт не будет, и пузырь остался бы пустым.
        Assert.Equal("первый кусок и второй кусок", body);
        Assert.Equal(Visibility.Visible, cancelVisible);
    }

    [Fact]
    public void Returning_shows_the_whole_elapsed_time_not_a_restarted_clock()
    {
        var startedAt = DateTime.Now.AddSeconds(-30);
        var workingStarted = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var live = Session("s1");
                var assistant = new ChatDisplayMessage
                {
                    Role = "assistant",
                    Id = "a1",
                    Status = AssistantStatus.Streaming,
                    Text = "думаю"
                };
                live.Messages.Add(assistant);

                var turn = Register(harness.Window, live, startedAt);
                turn.Assistant = assistant;
                turn.AssistantId = assistant.Id;

                Set(harness.Window, "_session", live);
                Call(harness.Window, "RenderSession");
                return Get<DateTime>(harness.Window, "_workingStarted");
            });

        Assert.Equal(startedAt, workingStarted);
    }

    [Fact]
    public void The_stop_button_cancels_the_turn_of_its_own_chat()
    {
        var (mine, neighbour) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var first = Session("s1", "Первый");
                var second = Session("s2", "Второй");
                var firstTurn = Register(harness.Window, first);
                var secondTurn = Register(harness.Window, second);

                Set(harness.Window, "_session", first);
                var actions = (MessageActions)Call(harness.Window, "CreateMessageActions", first)!;

                // Действия пережили перерисовку и переключение — «стоп» обязан помнить свой чат.
                Set(harness.Window, "_session", second);
                actions.Cancel!(new ChatDisplayMessage { Role = "assistant", Id = "a1" });

                return (firstTurn.Cancellation.IsCancellationRequested,
                    secondTurn.Cancellation.IsCancellationRequested);
            });

        Assert.True(mine, "кнопка не остановила ход своего чата");
        Assert.False(neighbour, "кнопка остановила чужой ход");
    }

    [Fact]
    public void A_background_turn_finishing_does_not_steal_the_caret()
    {
        var focused = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var background = Session("s1", "Фоновый");
                var opened = Session("s2", "Открытый");
                Set(harness.Window, "_session", opened);
                var turn = Register(harness.Window, background);

                var before = Get<AssistantMessageView?>(harness.Window, "_liveAssistant");
                Call(harness.Window, "FinishTurn", turn);

                // Ход снят, но вьюшка открытого чата не тронута и каретку никто не дёргал.
                return (harness.Window.IsBusy("s1"), ReferenceEquals(
                    before, Get<AssistantMessageView?>(harness.Window, "_liveAssistant")));
            });

        Assert.False(focused.Item1);
        Assert.True(focused.Item2);
    }

    [Fact]
    public void The_sidebar_marks_a_chat_that_is_thinking()
    {
        var (busyDot, idleDot) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var busy = Session("s1", "Отвечает");
                busy.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
                var idle = Session("s2", "Молчит");
                idle.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u2", Text = "привет" });
                harness.Services.ChatStore.Save(busy);
                harness.Services.ChatStore.Save(idle);

                Set(harness.Window, "_session", idle);
                Register(harness.Window, busy);
                Call(harness.Window, "RefreshChatList");

                return (DotVisibility(harness.Window, "s1"), DotVisibility(harness.Window, "s2"));
            });

        Assert.Equal(Visibility.Visible, busyDot);
        Assert.Equal(Visibility.Collapsed, idleDot);
    }

    [Fact]
    public void The_chat_list_is_not_rebuilt_when_nothing_changed()
    {
        // Список перерисовывают теперь и фоновые ходы, по нескольку раз в секунду, а каждая
        // перерисовка — чтение index.json с диска и полная пересборка панели.
        var same = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var chat = Session("s1", "Чат");
                chat.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет" });
                harness.Services.ChatStore.Save(chat);
                Set(harness.Window, "_session", chat);

                Call(harness.Window, "RefreshChatList");
                var panel = (Panel)harness.Window.FindName("ChatListPanel")!;
                var first = panel.Children.Cast<UIElement>().ToArray();

                Call(harness.Window, "RefreshChatList");
                return first.SequenceEqual(panel.Children.Cast<UIElement>());
            });

        Assert.True(same, "панель пересобрана, хотя в списке ничего не изменилось");
    }

    [Fact]
    public void A_second_turn_in_the_same_chat_is_refused()
    {
        // Один чат — один ход: ApiMessages это линейная стенограмма, два хода вперемешку дают
        // историю, которую ни человек, ни модель не прочитают. Дописать в идущий ход теперь можно,
        // но через очередь самого хода (RunningTurn.Enqueue) — второй RunningTurn всё так же не
        // заводится, и проверка ниже сторожит именно это.
        var grew = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var chat = Session("s1");
                Set(harness.Window, "_session", chat);
                Register(harness.Window, chat);

                var before = chat.Messages.Count;
                var task = (Task)Call(harness.Window, "RunTurnAsync", chat, TurnKind.Send,
                    (Func<ChatSession, IChatTurnObserver, CancellationToken, Task>)((session, observer, token) =>
                        harness.Services.Chat.RunTurnAsync(session, "ещё", observer, token)))!;
                Pump(task);

                return chat.Messages.Count - before;
            });

        Assert.Equal(0, grew);
    }

    [Fact]
    public void A_turn_past_the_limit_is_refused_with_a_line_not_a_modal()
    {
        var (started, notice) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                for (var i = 0; i < MainWindow.MaxParallelTurns; i++)
                {
                    Register(harness.Window, Session("busy" + i));
                }

                var extra = Session("extra");
                Set(harness.Window, "_session", extra);
                var task = (Task)Call(harness.Window, "RunTurnAsync", extra, TurnKind.Send,
                    (Func<ChatSession, IChatTurnObserver, CancellationToken, Task>)((session, observer, token) =>
                        harness.Services.Chat.RunTurnAsync(session, "привет", observer, token)))!;
                Pump(task);

                var warning = (TextBlock)harness.Window.FindName("AttachmentsWarning")!;
                return (harness.Window.IsBusy("extra"), warning.Text);
            });

        Assert.False(started, "четвёртый ход всё-таки стартовал");
        Assert.Contains("дождитесь", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_promise_to_take_a_line_in_belongs_to_the_chat_it_was_typed_in()
    {
        // Строка под композером одна на окно, а чатов много: «отправлено, учту» из одного
        // разговора висела над всеми остальными.
        var (typed, elsewhere, returned) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var mine = Session("s1", "Первый");
                var other = Session("s2", "Второй");
                harness.Services.ChatStore.Save(mine);
                harness.Services.ChatStore.Save(other);

                Set(harness.Window, "_session", mine);
                Register(harness.Window, mine);
                Call(harness.Window, "QueueFollowUp", "и ещё про диск D", false);

                var warning = (TextBlock)harness.Window.FindName("AttachmentsWarning")!;
                var here = warning.Text;

                Set(harness.Window, "_session", other);
                Call(harness.Window, "RenderSession");
                var away = (warning.Text, warning.Visibility);

                Set(harness.Window, "_session", mine);
                Call(harness.Window, "RenderSession");
                return (here, away, warning.Text);
            });

        Assert.NotEqual("", typed);
        Assert.Equal("", elsewhere.Item1);
        Assert.Equal(Visibility.Collapsed, elsewhere.Item2);

        // И возвращается вместе с чатом: сообщение всё ещё ждёт своей очереди.
        Assert.Equal(typed, returned);
    }

    [Fact]
    public void The_promise_comes_down_when_the_turn_actually_takes_the_line()
    {
        // Прежде подпись висела до конца ответа, хотя обещание «учту» исполнялось раньше —
        // в момент, когда движок забирал строку с очереди.
        var (before, after) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var chat = Session("s1");
                harness.Services.ChatStore.Save(chat);
                Set(harness.Window, "_session", chat);
                var turn = Register(harness.Window, chat);
                Call(harness.Window, "QueueFollowUp", "посмотри ещё диск D", false);

                var warning = (TextBlock)harness.Window.FindName("AttachmentsWarning")!;
                var shown = warning.Text;

                var router = new ChatTurnRouter(turn, _ => { }, _ => { }, harness.Window);
                Assert.True(((IChatTurnObserver)router).TryTakeQueuedMessage(out _));

                return (shown, warning.Text);
            });

        Assert.NotEqual("", before);
        Assert.Equal("", after);
    }

    [Fact]
    public void A_line_the_turn_never_got_to_is_still_put_into_the_transcript()
    {
        // Пузырь такого сообщения человек уже видит — он рисуется в момент отправки. Пропав из
        // стенограммы, оно осталось бы на экране непрочитанным, и следующий ответ выглядел бы
        // так, будто модель прочитала его и не ответила.
        var (api, notice) = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var chat = Session("s1");
                harness.Services.ChatStore.Save(chat);
                Set(harness.Window, "_session", chat);
                var turn = Register(harness.Window, chat);
                Call(harness.Window, "QueueFollowUp", "и ещё про диск D", false);

                // Ход обрывается раньше, чем движок дошёл до границы раунда.
                Call(harness.Window, "FinishTurn", turn);

                var warning = (TextBlock)harness.Window.FindName("AttachmentsWarning")!;
                return (chat.ApiMessages.Select(m => ChatContent.ReadText(m.Content)).ToList(), warning.Text);
            });

        Assert.Equal(["и ещё про диск D"], api);

        // И обещание «учту» снимается вместе с ходом: учитывать его больше некому.
        Assert.Equal("", notice);
    }

    [Fact]
    public void Switching_a_profile_is_refused_while_a_background_turn_runs()
    {
        // UseProfile перекореняет ChatStore — фоновый ход сохранил бы чат в папку нового профиля.
        var sameStore = With(
            (_, sent) => Task.FromResult(Sse("ответ", ModelOf(sent))),
            harness =>
            {
                var background = Session("s1");
                var opened = Session("s2");
                Set(harness.Window, "_session", opened);
                Register(harness.Window, background);

                var before = harness.Services.ChatStore;
                Call(harness.Window, "SwitchToProfile", "whatever");
                return ReferenceEquals(before, harness.Services.ChatStore);
            });

        Assert.True(sameStore, "профиль переключился, пока в фоне шёл ответ");
    }

    private static Visibility DotVisibility(MainWindow window, string sessionId)
    {
        var panel = (Panel)window.FindName("ChatListPanel")!;
        var row = panel.Children.OfType<Button>().Single(b => (string)b.Tag == sessionId);
        row.ApplyTemplate();
        return ((FrameworkElement)row.Template.FindName("Working", row)!).Visibility;
    }

    /// <summary>Крутит очередь диспетчера, пока ход не закончится.</summary>
    private static void Pump(Task task)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = task.ContinueWith(
            _ => frame.Continue = false,
            TaskScheduler.FromCurrentSynchronizationContext());
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static string ModelOf(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("model").GetString() ?? "";

    private static HttpResponseMessage Sse(string text, string model)
    {
        var encoded = JsonSerializer.Serialize(text);
        var payload =
            "data: {\"model\":\"" + model + "\",\"choices\":[{\"delta\":{\"content\":" + encoded + "}}]}\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n" +
            "data: [DONE]\n";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> script)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await script(request, body);
        }
    }
}
