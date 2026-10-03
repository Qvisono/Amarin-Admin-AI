using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Оверлей поверх чата заслоняет его одной рамкой. До 1.30.0 переключали
/// <c>Chat.IsHitTestVisible</c> — свойство наследуется, и WPF обходил всё дерево ленты: на чате
/// в 1200 сообщений журнал и «Состояние ПК» открывались на 45 мс дольше и закрывались так же.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatShieldTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-shield-" + Guid.NewGuid().ToString("N"));

    public ChatShieldTests(WpfFixture wpf) => _wpf = wpf;

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

    [Fact]
    public async Task An_overlay_blocks_the_chat_without_touching_its_messages()
    {
        var outcome = await _wpf.Ui.Invoke(async () =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            services.Settings.AutoCheckUpdates = false;
            var session = new ChatSession { Id = "shield", Title = "Shield", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
            for (var i = 0; i < 60; i++)
            {
                session.Messages.Add(new ChatDisplayMessage
                {
                    Id = "m" + i.ToString(CultureInfo.InvariantCulture),
                    Role = i % 2 == 0 ? "user" : "assistant",
                    Text = "Сообщение " + i.ToString(CultureInfo.InvariantCulture) + " с парой слов, чтобы было что разложить.",
                    Status = AssistantStatus.Complete
                });
            }

            services.ChatStore.Save(session);
            services.ChatStore.Flush();
            var window = new MainWindow
            {
                Width = 1100,
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
                typeof(MainWindow).GetMethod("OpenChat", Hidden)!.Invoke(window, [session.Id]);
                var hosts = (List<ChatMessageHost>)typeof(MainWindow).GetField("_messageHosts", Hidden)!.GetValue(window)!;
                var wait = Stopwatch.StartNew();
                while (hosts.Any(host => !host.IsMaterialized) && wait.Elapsed < TimeSpan.FromSeconds(20))
                {
                    await Task.Delay(15);
                }

                window.UpdateLayout();
                var touched = 0;
                var boxes = hosts.SelectMany(host => Descendants<RichTextBox>(host)).ToList();
                foreach (var box in boxes)
                {
                    box.IsHitTestVisibleChanged += (_, _) => touched++;
                }

                var viewer = (ScrollViewer)window.FindName("ChatScrollViewer")!;
                var center = viewer.TranslatePoint(new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2), (UIElement)window.Content);

                typeof(MainWindow).GetMethod("OpenJournal", Hidden)!.Invoke(window, null);
                window.UpdateLayout();
                var blocked = window.ChatBlocked;
                var shieldHit = HitsShield((UIElement)window.FindName("Chat")!, viewer, window);
                typeof(MainWindow).GetMethod("CloseJournal", Hidden)!.Invoke(window, null);

                window.OpenHealth();
                typeof(MainWindow).GetMethod("CloseHealth", Hidden)!.Invoke(window, null);
                window.UpdateLayout();
                return (boxes.Count, touched, blocked, shieldHit, released: !window.ChatBlocked);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(outcome.Count > 20, "лента не построилась");
        Assert.Equal(0, outcome.touched);
        Assert.True(outcome.blocked);
        Assert.True(outcome.shieldHit, "мышь над лентой попадает не в заслонку");
        Assert.True(outcome.released);
    }

    /// <summary>Что под курсором над серединой ленты, если смотреть изнутри <c>Chat</c>.</summary>
    private static bool HitsShield(UIElement chat, ScrollViewer viewer, Window window)
    {
        var point = viewer.TranslatePoint(new Point(viewer.ActualWidth / 2, viewer.ActualHeight / 2), chat);
        var hit = chat.InputHitTest(point) as DependencyObject;
        return hit is FrameworkElement { Name: "ChatShield" } && window.FindName("ChatShield") == hit;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match)
            {
                yield return match;
            }

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                stack.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
