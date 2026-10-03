using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Поиск по тексту всех чатов в боковой панели (D1) на живом окне. С 1.30.0 он ищет в фоне —
/// здесь проверяется то, что раньше было видно глазами: выдача приходит, не мигает от чужих
/// перерисовок списка и переводит свои подписи вместе с языком.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class TextSearchUiTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-text-search-ui-" + Guid.NewGuid().ToString("N"));

    public TextSearchUiTests(WpfFixture wpf) => _wpf = wpf;

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
    public async Task Hits_arrive_from_the_background_and_a_list_refresh_does_not_redraw_them()
    {
        var (found, sameCards) = await WithWindow(async (window, panel) =>
        {
            Search(window, "загадочныйтермин");
            var first = await Cards(panel);

            // Соседний чат отвечает — список перерисовывают несколько раз в секунду. Индекс
            // тот же, и выдача обязана остаться теми же карточками, а не собраться заново.
            Call(window, "RefreshChatList");
            Call(window, "RefreshChatList");
            await Pump();
            var second = Hits(panel);
            return (first.Count, first.SequenceEqual(second));
        });

        Assert.Equal(2, found);
        Assert.True(sameCards);
    }

    [Fact]
    public async Task The_empty_answer_follows_the_interface_language()
    {
        var (russian, english) = await WithWindow(async (window, panel) =>
        {
            Search(window, "словокотороговнетнигде");
            var before = await EmptyText(panel, "Ничего не найдено");
            try
            {
                LanguageManager.Apply("en");
                Call(window, "RelocalizeUi");
                return (before, await EmptyText(panel, "Nothing found"));
            }
            finally
            {
                LanguageManager.Apply(LanguageManager.DefaultCode);
            }
        });

        Assert.Equal("Ничего не найдено", russian);
        Assert.Equal("Nothing found", english);
    }

    private Task<T> WithWindow<T>(Func<MainWindow, Panel, Task<T>> body) => _wpf.Ui.Invoke(async () =>
    {
        var services = UiServices.Build(_root, "k", new HttpClientHandler());
        services.Settings.AutoCheckUpdates = false;
        Save(services, "a", "Первый", "Тут встречается загадочныйтермин посреди текста.");
        Save(services, "b", "Второй", "И здесь загадочныйтермин тоже есть.");
        Save(services, "c", "Третий", "А здесь ничего похожего.");
        services.TextIndex.Build(services.ChatStore.List(), services.ChatStore.TryLoad, CancellationToken.None);

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
            return await body(window, (Panel)window.FindName("ChatListPanel")!);
        }
        finally
        {
            window.Close();
        }
    });

    private static void Save(AppServices services, string id, string title, string text)
    {
        var session = new ChatSession { Id = id, Title = title, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        session.Messages.Add(new ChatDisplayMessage { Id = id + "-1", Role = "user", Text = text, CreatedAt = DateTime.Now });
        services.ChatStore.Save(session);
        services.ChatStore.Flush();
    }

    /// <summary>Режим «по тексту» и запрос в поле — тем же путём, что щелчок и набор.</summary>
    private static void Search(MainWindow window, string query)
    {
        Call(window, "SetSearchMode", false, true);
        ((TextBox)window.FindName("SearchBox")!).Text = query;
        Call(window, "RefreshChatList");
    }

    /// <summary>
    /// Карточки находок. Строки чатов — тоже кнопки, но с id в <c>Tag</c>: пока выдача не пришла,
    /// прежний список стоит на месте.
    /// </summary>
    private static List<Button> Hits(Panel panel) =>
        panel.Children.OfType<Button>().Where(button => button.Tag is null && button.Content is StackPanel).ToList();

    private static async Task<List<Button>> Cards(Panel panel)
    {
        for (var i = 0; i < 250; i++)
        {
            var cards = Hits(panel);
            if (cards.Count > 0)
            {
                return cards;
            }

            await Task.Delay(20);
        }

        return [];
    }

    private static async Task<string?> EmptyText(Panel panel, string expected)
    {
        string? text = null;
        for (var i = 0; i < 250; i++)
        {
            text = panel.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
            if (text == expected)
            {
                break;
            }

            await Task.Delay(20);
        }

        return text;
    }

    private static async Task Pump()
    {
        await Task.Delay(100);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(Hidden)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);
}
