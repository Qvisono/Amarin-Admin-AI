using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Время у сообщений человека (D17): часы под пузырём, полная дата в подсказке.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class UserMessageTimeTests
{
    private readonly WpfFixture _wpf;

    public UserMessageTimeTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void A_sent_message_shows_its_time_and_the_full_stamp_on_hover()
    {
        var sent = new DateTime(2026, 9, 30, 14, 5, 9);
        var (clock, tip) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var view = ChatMessageViews.CreateUser(window,
                new ChatDisplayMessage { Role = "user", Id = "u1", Text = "привет", CreatedAt = sent },
                dateFormat: DateFormat.DayMonthShort);

            var chip = Descendants(view.Root).OfType<Border>()
                .First(border => border.Child is TextBlock text && text.Text == "14:05");
            return (((TextBlock)chip.Child).Text, chip.ToolTip as string);
        });

        Assert.Equal("14:05", clock);
        Assert.Equal(ChatFormat.Stamp(sent, DateFormat.DayMonthShort), tip);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                queue.Enqueue(child);
            }

            if (node is Visual or System.Windows.Media.Media3D.Visual3D)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                {
                    queue.Enqueue(VisualTreeHelper.GetChild(node, i));
                }
            }
        }
    }
}
