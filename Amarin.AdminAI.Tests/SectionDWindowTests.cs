using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Раздел D, 1.38.0-beta.4, на живом окне со службами: панель сеанса и пустой чат, место кнопки
/// «В начало», цена чата при переходе между чатами, значки сжатых сообщений, профиль по аватару.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SectionDWindowTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-section-d-" + Guid.NewGuid().ToString("N"));

    public SectionDWindowTests(WpfFixture wpf) => _wpf = wpf;

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

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow).GetMethods(Hidden)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);

    private static List<ChatMessageHost> Hosts(MainWindow window) =>
        (List<ChatMessageHost>)typeof(MainWindow).GetField("_messageHosts", Hidden)!.GetValue(window)!;

    private static T Named<T>(MainWindow window, string name) where T : class => (T)window.FindName(name)!;

    private static ChatSession Chat(string id, int turns, decimal cost)
    {
        var session = new ChatSession { Id = id, Title = id, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        for (var i = 1; i <= turns; i++)
        {
            session.Messages.Add(new ChatDisplayMessage { Id = "u" + i, Role = "user", Text = "q" + i, CreatedAt = DateTime.Now });
            session.Messages.Add(new ChatDisplayMessage
            {
                Id = "a" + i, Role = "assistant", Text = "a" + i, Status = AssistantStatus.Complete, CreatedAt = DateTime.Now,
                Cost = cost > 0m ? new VeniceCost { Usd = cost, HasData = true } : null
            });
            session.ApiMessages.Add(new ChatMessage { Role = "user", Content = ChatContent.Text("q" + i) });
            session.ApiMessages.Add(new ChatMessage { Role = "assistant", Content = ChatContent.Text("a" + i) });
        }

        return session;
    }

    private T With<T>(Func<MainWindow, AppServices, T> body, double width = 1100) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        var window = new MainWindow
        {
            Width = width,
            Height = 760,
            Left = -32000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        window.AttachServices(services);
        window.Show();
        try
        {
            return body(window, services);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void The_chat_cost_chip_follows_the_open_chat()
    {
        var (first, second, empty) = With((window, services) =>
        {
            services.ChatStore.Save(Chat("costly", 2, 0.25m));
            services.ChatStore.Save(Chat("cheap", 1, 0.01m));
            services.ChatStore.Save(Chat("free", 1, 0m));
            services.ChatStore.Flush();
            var chip = Named<Border>(window, "ChatCostChip");
            var text = Named<TextBlock>(window, "ChatCostText");

            Call(window, "OpenChat", "costly");
            var costly = (chip.Visibility, text.Text);
            Call(window, "OpenChat", "cheap");
            var cheap = (chip.Visibility, text.Text);
            Call(window, "OpenChat", "free");
            return (costly, cheap, chip.Visibility);
        });

        Assert.Equal(Visibility.Visible, first.Visibility);
        Assert.Equal(Visibility.Visible, second.Visibility);
        Assert.Equal(ChatFormat.Cost(new VeniceCost { Usd = 0.5m, HasData = true }), first.Text);
        Assert.Equal(ChatFormat.Cost(new VeniceCost { Usd = 0.01m, HasData = true }), second.Text);
        Assert.Equal(Visibility.Collapsed, empty);
    }

    [Fact]
    public void The_rail_hides_in_an_empty_chat_and_takes_over_the_context_badge_and_the_scroll_button()
    {
        var (empty, open, back) = With((window, services) =>
        {
            services.ChatStore.Save(Chat("talk", 2, 0m));
            services.ChatStore.Flush();
            var rail = Named<SessionRailPanel>(window, "SessionRail");
            var badge = Named<Border>(window, "ContextBadge");
            var top = Named<Button>(window, "ScrollTopButton");
            var zoom = Named<Border>(window, "ChatZoomBadge");
            (Visibility Rail, Visibility Badge, double Top, double Zoom) State()
            {
                window.UpdateLayout();
                Call(window, "UpdateSessionRail");
                return (rail.Visibility, badge.Visibility, top.Margin.Right, zoom.Margin.Right);
            }

            var before = State();
            Call(window, "OpenChat", "talk");
            var opened = State();
            Call(window, "StartNewChatFromUi");
            return (before, opened, State());
        }, width: 2400);

        // В пустом чате нет ни панели, ни плашки контекста в поле ввода.
        Assert.Equal((Visibility.Collapsed, Visibility.Collapsed, 28d, 66d), empty);
        Assert.Equal(Visibility.Visible, open.Rail);
        Assert.Equal(Visibility.Collapsed, open.Badge);

        // Кнопка и плашка лупы стоят левее панели: 250 ширины, 20 поля и 8 зазора.
        Assert.Equal(278d, open.Top);
        Assert.Equal(316d, open.Zoom);
        Assert.Equal(empty, back);
    }

    [Fact]
    public void A_narrow_window_keeps_the_context_badge_only_for_a_chat_with_messages()
    {
        var (empty, open) = With((window, services) =>
        {
            services.ChatStore.Save(Chat("talk", 2, 0m));
            services.ChatStore.Flush();
            var rail = Named<SessionRailPanel>(window, "SessionRail");
            var badge = Named<Border>(window, "ContextBadge");
            (Visibility Rail, Visibility Badge) State()
            {
                window.UpdateLayout();
                Call(window, "UpdateSessionRail");
                return (rail.Visibility, badge.Visibility);
            }

            var before = State();
            Call(window, "OpenChat", "talk");
            return (before, State());
        });

        // Панель в узкое окно не помещается: контекст чата с сообщениями показывает плашка.
        Assert.Equal((Visibility.Collapsed, Visibility.Collapsed), empty);
        Assert.Equal((Visibility.Collapsed, Visibility.Visible), open);
    }

    [Fact]
    public void Every_compacted_message_carries_an_icon_that_brings_the_original_back()
    {
        var (marked, kept, after, active) = With((window, services) =>
        {
            var session = Chat("compacted", 6, 0m);
            ContextCompaction.Apply(session, ContextCompaction.Plan(session, new CompactionOptions(4, CompactFirst: false))!, "summary");
            services.ChatStore.Save(session);
            services.ChatStore.Flush();
            Call(window, "OpenChat", "compacted");
            foreach (var host in Hosts(window))
            {
                Call(window, "MaterializeHost", host);
            }

            window.UpdateLayout();
            SaveShot(Named<ScrollViewer>(window, "ChatScrollViewer"), "compacted-feed.png");
            var tip = Loc.Get("S.Compact.MessageTip");
            bool Marked(string id) => Hosts(window).Single(host => host.Id == id).Child is Panel panel &&
                                      Descendants(panel).OfType<Border>().Any(border => Equals(border.ToolTip, tip));
            var compacted = new[] { "u2", "a2", "u3", "a3", "u4", "a4" }.All(Marked);
            var untouched = new[] { "u1", "a1", "u5", "a5", "u6", "a6" }.Any(Marked);

            Call(window, "UndoCompaction");
            window.UpdateLayout();
            var open = (ChatSession)typeof(MainWindow).GetField("_session", Hidden)!.GetValue(window)!;
            return (compacted, untouched, Hosts(window).Any(host => Marked(host.Id)), ContextCompaction.IsActive(open));
        });

        Assert.True(marked);
        Assert.False(kept);
        Assert.False(after);
        Assert.False(active);
    }

    [Fact]
    public void A_click_on_the_avatar_or_the_name_opens_the_profile_settings()
    {
        var (avatar, name) = With((window, _) =>
        {
            bool Opens(string element)
            {
                var general = (RadioButton)window.FindSetting("NavGeneral")!;
                general.IsChecked = true;
                var target = Named<FrameworkElement>(window, element);

                // Нажатие обязано быть обработано здесь: иначе окно начинает перетаскивание и
                // отпускание кнопки до аватара не доходит.
                var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
                target.RaiseEvent(press);
                target.RaiseEvent(
                    new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                var overlay = (FrameworkElement)window.FindSetting("SettingsOverlay")!;
                var opened = press.Handled && overlay.Visibility == Visibility.Visible &&
                             ((RadioButton)window.FindSetting("NavProfile")!).IsChecked == true;
                overlay.Visibility = Visibility.Collapsed;
                return opened;
            }

            return (Opens("AccountAvatar"), Opens("AccountLabels"));
        });

        Assert.True(avatar);
        Assert.True(name);
    }

    [Fact]
    public void The_compaction_price_is_booked_even_when_no_answer_has_a_price()
    {
        var session = Chat("unpriced", 2, 0m);
        var book = typeof(MainWindow).GetMethod("BookCompactCost", BindingFlags.Static | BindingFlags.NonPublic)!;

        var id = (string?)book.Invoke(null, [session, new VeniceCost { Usd = 0.03m, HasData = true }]);

        Assert.Equal("a2", id);
        Assert.Equal(0.03m, session.Messages[^1].Cost!.Usd);
        Assert.Equal(0.03m, session.Messages[^1].CompactCost!.Usd);
        Assert.Equal(0.03m, ChatCost.Total(session));
    }

    /// <summary>Снимок для просмотра глазами: только при <c>AMARIN_RAIL_SHOTS=&lt;папка&gt;</c>.</summary>
    private static void SaveShot(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("AMARIN_RAIL_SHOTS");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
