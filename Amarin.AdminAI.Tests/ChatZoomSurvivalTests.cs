using System.Reflection;
using System.Windows;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Приближённая лупой лента переживает дописанный ценник, но не переход в соседний чат.
/// </summary>
/// <remarks>
/// Ход дописывает сводку и заголовок брошенными задачами, и, вернувшись, они приписывают свою
/// цену уже закрытому ответу. Перерисовать надо один пузырь; раньше ради него пересобиралась
/// вся лента — вместе со сбросом лупы. На экране это выглядело так, будто приближение слетает
/// само по себе через секунду после каждого ответа.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class ChatZoomSurvivalTests
{
    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;

    public ChatZoomSurvivalTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, Hidden)!.SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;

    private static void Call(object target, string method, params object?[] arguments) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, arguments);

    private static ChatSession Chat()
    {
        var session = new ChatSession { Id = "zoom-survival", Title = "Приближённый разговор" };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", Text = "вопрос" });
        session.Messages.Add(new ChatDisplayMessage { Role = "assistant", Id = "a1", Text = "ответ" });
        return session;
    }

    [Fact]
    public void Repricing_one_answer_leaves_the_magnifier_where_it_was()
    {
        var (scale, rebuilt) = WithChat(window =>
        {
            var layer = (ChatZoomHost)window.FindName("ChatZoomLayer")!;
            layer.Scale = 2.0;

            var before = Get<List<ChatMessageHost>>(window, "_messageHosts").Count;
            Call(window, "RefreshMessageView", "a1");

            return (layer.Scale, Get<List<ChatMessageHost>>(window, "_messageHosts").Count == before);
        });

        Assert.Equal(2.0, scale, 3);

        // И лента осталась той же: пересобирать её ради одного ценника незачем.
        Assert.True(rebuilt, "перерисовка одного сообщения пересобрала всю ленту");
    }

    [Fact]
    public void The_repriced_bubble_is_actually_rebuilt()
    {
        // Иначе тест выше проходил бы и на методе, который не делает ничего.
        var replaced = WithChat(window =>
        {
            var hosts = Get<List<ChatMessageHost>>(window, "_messageHosts");
            var host = hosts.Single(item => item.Id == "a1");
            var before = host.Child;

            Call(window, "RefreshMessageView", "a1");
            return !ReferenceEquals(before, host.Child) && host.Child is not null;
        });

        Assert.True(replaced, "пузырь не пересобрался, и новая цена на нём не появится");
    }

    [Fact]
    public void Opening_another_chat_still_returns_the_feed_to_normal_size()
    {
        // Обратная сторона: жест относится к тому, что человек читал, и соседний чат
        // приближённым он не просил.
        var scale = WithChat(window =>
        {
            var layer = (ChatZoomHost)window.FindName("ChatZoomLayer")!;
            layer.Scale = 2.0;
            Call(window, "RenderSession");
            return layer.Scale;
        });

        Assert.Equal(1.0, scale, 3);
    }

    [Fact]
    public void A_bubble_that_is_not_in_the_feed_is_simply_skipped()
    {
        // Цена сводки может приехать к ответу, который уже удалили или который лежит в чате,
        // с которого человек ушёл. Это обычное дело, а не повод для исключения.
        var survived = WithChat(window =>
        {
            Call(window, "RefreshMessageView", "no-such-message");
            Call(window, "RefreshMessageView", (string?)null);
            return true;
        });

        Assert.True(survived);
    }

    private T WithChat<T>(Func<MainWindow, T> probe) =>
        _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var original = Get<ChatSession>(window, "_session");
            try
            {
                Set(window, "_session", Chat());
                Call(window, "RenderSession");
                window.UpdateLayout();
                return probe(window);
            }
            finally
            {
                Set(window, "_session", original);
                Call(window, "RenderSession");
            }
        });
}
