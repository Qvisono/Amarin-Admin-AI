using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Раздел E 1.37.0 на живом окне: палитра команд и быстрый вопрос.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SectionEWindowTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-section-e-" + Guid.NewGuid().ToString("N"));

    public SectionEWindowTests(WpfFixture wpf) => _wpf = wpf;

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

    private T WithWindow<T>(Func<MainWindow, AppServices, T> body) => _wpf.Ui.Invoke(() =>
    {
        // Без сети: «Открыть в чате» заказывает заголовок, и тест ушёл бы с ключом «k» к провайдеру.
        var services = UiServices.Build(_root, "k", new NoNetwork());
        var window = new MainWindow();
        window.AttachServices(services);
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Pump() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private static CommandPalette Palette(MainWindow window) =>
        ((Grid)window.FindName("PaletteHost")).Children.OfType<CommandPalette>().Single();

    // ───────────────────────── палитра ─────────────────────────

    [Fact]
    public void The_palette_is_not_built_until_it_is_first_opened()
    {
        var (before, after, visible) = WithWindow((window, _) =>
        {
            var host = (Grid)window.FindName("PaletteHost");
            var empty = host.Children.Count;
            window.OpenCommandPalette();
            return (empty, host.Children.Count, host.Visibility);
        });

        Assert.Equal(0, before);
        Assert.Equal(1, after);
        Assert.Equal(Visibility.Visible, visible);
    }

    [Fact]
    public void Typing_finds_a_setting_on_a_subpage_and_enter_opens_it_there()
    {
        var (found, settingsOpen, subOpen) = WithWindow((window, _) =>
        {
            // Все страницы построены, как после прогрева: палитра ищет по их подписям.
            window.CompleteSettingsBuild();
            window.OpenCommandPalette();
            var palette = Palette(window);
            var search = (TextBox)palette.FindName("Search");

            // Подпись строки на подстранице «Приватность» страницы «Безопасность».
            var label = Loc.Get("S.Security.Encrypt");
            search.Text = label;
            var selected = palette.SelectedEntry;
            palette.RunSelected();
            Pump();

            var security = window.SettingsUi.Built<SettingsSecurityPage>();
            var privacy = security?.FindName("PrivacySub") as FrameworkElement;
            return (
                selected?.Item.Title,
                ((FrameworkElement)window.FindName("SettingsOverlay")).Visibility,
                privacy?.Visibility);
        });

        Assert.Equal(Loc.Get("S.Security.Encrypt"), found);
        Assert.Equal(Visibility.Visible, settingsOpen);
        Assert.Equal(Visibility.Visible, subOpen);
    }

    [Fact]
    public void Escape_closes_the_palette_and_an_empty_query_offers_actions_first()
    {
        var (first, closed) = WithWindow((window, _) =>
        {
            window.OpenCommandPalette();
            var palette = Palette(window);
            var kind = palette.SelectedEntry?.Item.Kind;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, new FakeSource(), 0, Key.Escape)
            {
                RoutedEvent = UIElement.PreviewKeyDownEvent
            };
            palette.RaiseEvent(args);
            return (kind, ((Grid)window.FindName("PaletteHost")).Visibility);
        });

        Assert.Equal(PaletteKind.Action, first);
        Assert.Equal(Visibility.Collapsed, closed);
    }

    [Fact]
    public void The_palette_has_its_own_shortcut_and_does_not_take_ctrl_k()
    {
        var palette = HotkeyMap.All.Single(action => action.Id == HotkeyMap.CommandPalette);
        var search = HotkeyMap.All.Single(action => action.Id == HotkeyMap.SearchChats);

        Assert.Equal("Ctrl+Shift+P", palette.DefaultGesture);
        Assert.Equal("Ctrl+K", search.DefaultGesture);
    }

    // ───────────────────────── быстрый вопрос ─────────────────────────

    [Fact]
    public void A_quick_answer_shows_in_its_window_without_a_toast_or_a_mark_and_opens_as_a_saved_chat()
    {
        var state = WithWindow((window, services) =>
        {
            var session = services.ChatStore.CreateNew();
            session.IsTemporary = true;
            session.Messages.Add(new ChatDisplayMessage { Id = "u1", Role = "user", Text = "Как узнать порт?", CreatedAt = DateTime.Now });
            var assistant = new ChatDisplayMessage { Id = "a1", Role = "assistant", Text = "Командой netstat.", CreatedAt = DateTime.Now };
            session.Messages.Add(assistant);

            // Окно быстрого вопроса создаётся, но не показывается: тест не трогает экран.
            typeof(MainWindow).GetMethod("EnsureQuickAsk", Hidden)!.Invoke(window, null);
            typeof(MainWindow).GetField("_quickSession", Hidden)!.SetValue(window, session);

            var turn = window.Turns.TryStart(session, TurnKind.QuickAsk, DateTime.Now).Turn!;
            ((IChatTurnUi)window).TurnAssistantCompleted(turn, assistant);
            Pump();
            var shown = window.QuickAsk!.ShownText;
            var marked = window.Turns.NeedsAttention(session.Id);
            var listedBefore = services.ChatStore.List().Any(entry => entry.Id == session.Id);
            window.FinishTurn(turn);

            window.OpenQuickInChat();
            services.ChatStore.Flush();
            var listedAfter = services.ChatStore.List().Any(entry => entry.Id == session.Id);
            var current = (ChatSession)typeof(MainWindow).GetField("_session", Hidden)!.GetValue(window)!;
            return (shown, marked, listedBefore, listedAfter, current.Id == session.Id, session.IsTemporary);
        });

        Assert.Equal("Командой netstat.", state.shown);
        Assert.False(state.marked, "быстрый вопрос оставил метку в списке");
        Assert.False(state.listedBefore, "временный быстрый вопрос попал в список до «Открыть в чате»");
        Assert.True(state.listedAfter, "«Открыть в чате» не записало чат");
        Assert.True(state.Item5, "открылся не тот чат");
        Assert.False(state.IsTemporary);
    }

    [Fact]
    public void Hiding_the_window_drops_a_question_that_was_not_opened_in_chat()
    {
        var (left, listed) = WithWindow((window, services) =>
        {
            var session = services.ChatStore.CreateNew();
            session.IsTemporary = true;
            typeof(MainWindow).GetMethod("EnsureQuickAsk", Hidden)!.Invoke(window, null);
            typeof(MainWindow).GetField("_quickSession", Hidden)!.SetValue(window, session);

            window.HideQuickAsk();
            services.ChatStore.Flush();
            return (typeof(MainWindow).GetField("_quickSession", Hidden)!.GetValue(window),
                    services.ChatStore.List().Any(entry => entry.Id == session.Id));
        });

        Assert.Null(left);
        Assert.False(listed);
    }

    [Fact]
    public void A_quick_question_opened_in_chat_and_left_in_the_background_gets_the_usual_mark()
    {
        // Открытый в чате быстрый вопрос — уже обычный чат: ответ, законченный, пока человек в
        // другом чате, оставляет метку, как у всех. Прежде ветка быстрого вопроса глушила её.
        var marked = WithWindow((window, services) =>
        {
            // Карточка «ответ готов» вылезла бы в угол настоящего экрана.
            services.Settings.NotifyOnResponseComplete = false;
            var session = services.ChatStore.CreateNew();
            session.IsTemporary = true;
            session.Messages.Add(new ChatDisplayMessage { Id = "u1", Role = "user", Text = "Как узнать порт?", CreatedAt = DateTime.Now });
            var assistant = new ChatDisplayMessage { Id = "a1", Role = "assistant", Text = "Командой netstat.", CreatedAt = DateTime.Now };
            session.Messages.Add(assistant);
            typeof(MainWindow).GetMethod("EnsureQuickAsk", Hidden)!.Invoke(window, null);
            typeof(MainWindow).GetField("_quickSession", Hidden)!.SetValue(window, session);
            var turn = window.Turns.TryStart(session, TurnKind.QuickAsk, DateTime.Now).Turn!;

            window.OpenQuickInChat();
            window.StartNewChatFromUi();
            ((IChatTurnUi)window).TurnAssistantCompleted(turn, assistant);
            Pump();
            var result = window.Turns.NeedsAttention(session.Id);
            window.FinishTurn(turn);
            return result;
        });

        Assert.True(marked, "ответ открытого в чате быстрого вопроса не оставил метки");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class FakeSource : PresentationSource
    {
        public override Visual? RootVisual { get; set; }

        public override bool IsDisposed => false;

        protected override CompositionTarget? GetCompositionTargetCore() => null;
    }
}
