using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Открытие чата строит только видимую часть переписки; остальное достраивается в фоне и по
/// прокрутке.
/// </summary>
/// <remarks>
/// Лента — обычный <c>StackPanel</c> без виртуализации, и раньше открытие чата строило все
/// сообщения разом: на каждый ответ несколько десятков объектов, <c>RichTextBox</c> с целым
/// <c>FlowDocument</c> и ряд кнопок с иконками. На длинной переписке переключение подвисало
/// именно здесь.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class LazyMessageLoadTests
{
    private readonly WpfFixture _wpf;

    public LazyMessageLoadTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, Hidden)!.SetValue(target, value);

    private static T Get<T>(object target, string field) =>
        (T)target.GetType().GetField(field, Hidden)!.GetValue(target)!;

    private static void Call(object target, string method) =>
        target.GetType().GetMethod(method, Hidden)!.Invoke(target, null);

    private static ChatSession LongChat(int messages)
    {
        var session = new ChatSession { Id = "lazy", Title = "Долгий разговор" };
        for (var i = 0; i < messages; i++)
        {
            session.Messages.Add(new ChatDisplayMessage
            {
                Role = i % 2 == 0 ? "user" : "assistant",
                Id = "m" + i,
                Status = AssistantStatus.Complete,
                Text = string.Join(' ', Enumerable.Repeat("слово", 60)) + " №" + i
            });
        }

        return session;
    }

    /// <summary>Открывает чат в общем окне и возвращает окно в прежнее состояние после проверки.</summary>
    private T WithChat<T>(ChatSession session, Func<MainWindow, List<ChatMessageHost>, T> probe)
    {
        return _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var original = Get<ChatSession>(window, "_session");
            try
            {
                Set(window, "_session", session);
                Call(window, "RenderSession");
                return probe(window, Get<List<ChatMessageHost>>(window, "_messageHosts"));
            }
            finally
            {
                Set(window, "_session", original);
                Call(window, "RenderSession");
            }
        });
    }

    /// <summary>Даёт фоновой дорисовке доработать.</summary>
    private static void DrainBackgroundFill(MainWindow window, List<ChatMessageHost> hosts)
    {
        for (var i = 0; i < 500 && hosts.Any(host => !host.IsMaterialized); i++)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
        }
    }

    [Fact]
    public void Only_the_tail_of_a_long_chat_is_built_at_once()
    {
        var (total, built, lastBuilt, firstBuilt) = WithChat(LongChat(80), (_, hosts) => (
            hosts.Count,
            hosts.Count(host => host.IsMaterialized),
            hosts[^1].IsMaterialized,
            hosts[0].IsMaterialized));

        Assert.Equal(80, total);
        Assert.True(lastBuilt, "последнее сообщение человек видит сразу");
        Assert.False(firstBuilt, "самое старое сообщение строить незачем");
        Assert.True(built < total, $"построено {built} из {total} — это вся лента");
    }

    [Fact]
    public void Every_message_keeps_its_place_in_the_feed()
    {
        // Хост занимает место сообщения сразу, поэтому полоса прокрутки честна с первого кадра,
        // а достроенное сообщение не сдвигает соседей.
        var order = WithChat(LongChat(20), (window, hosts) =>
        {
            var panel = (Panel)window.FindName("MessagesPanel")!;
            return panel.Children.Cast<UIElement>().Select(child => ((ChatMessageHost)child).Id).ToList();
        });

        Assert.Equal(Enumerable.Range(0, 20).Select(i => "m" + i), order);
    }

    [Fact]
    public void The_background_fill_finishes_the_rest()
    {
        var left = WithChat(LongChat(40), (window, hosts) =>
        {
            DrainBackgroundFill(window, hosts);
            return hosts.Count(host => !host.IsMaterialized);
        });

        Assert.Equal(0, left);
    }

    [Fact]
    public void Reopening_a_chat_reserves_the_heights_it_measured()
    {
        var chat = LongChat(30);

        var (remembered, reserved) = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            var original = Get<ChatSession>(window, "_session");
            try
            {
                Set(window, "_session", chat);
                Call(window, "RenderSession");
                DrainBackgroundFill(window, Get<List<ChatMessageHost>>(window, "_messageHosts"));

                var heights = Get<Dictionary<string, double>>(window, "_messageHeights");
                var known = heights.Count;

                // Второй заход в тот же чат: резерв берётся из памяти, а не из прикидки.
                Call(window, "RenderSession");
                var hosts = Get<List<ChatMessageHost>>(window, "_messageHosts");
                var first = hosts.First(host => !host.IsMaterialized);

                return (known, Math.Abs(first.Height - heights[first.Id]));
            }
            finally
            {
                Set(window, "_session", original);
                Call(window, "RenderSession");
            }
        });

        Assert.True(remembered > 0, "высоты должны запоминаться");
        Assert.True(reserved < 0.001, "резерв обязан совпасть с измеренной высотой");
    }

    [Fact]
    public void Jumping_to_a_message_builds_it_first()
    {
        // Поиск и уведомление прыгают к сообщению по идентификатору. Непостроенное сообщение
        // означало бы прыжок в пустое место нужной высоты.
        var built = WithChat(LongChat(60), (window, hosts) =>
        {
            var target = hosts.First(host => !host.IsMaterialized);
            window.GetType()
                .GetMethod("ScrollToMessage", Hidden)!
                .Invoke(window, [target.Id]);
            return target.IsMaterialized;
        });

        Assert.True(built);
    }
}
