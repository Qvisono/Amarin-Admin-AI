using System.Windows;
using System.Windows.Controls;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Поле ввода не должно съедать чат. До этой проверки длинный промпт раздувал композер на всё
/// окно: лента сообщений исчезала с экрана, каретка уезжала за нижний край, и дописать сообщение
/// было нечем.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ComposerHeightTests
{
    private readonly WpfFixture _wpf;

    public ComposerHeightTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>Текст на несколько тысяч знаков — примерно такой промпт и ломал разметку.</summary>
    private static string LongPrompt() =>
        string.Join(" ", Enumerable.Repeat("длинная строка системного промпта", 200));

    [Fact]
    public void The_composer_gets_a_share_of_the_conversation_area()
    {
        // 45 % от тысячи пикселей за вычетом обвязки композера (рамка, отступы, тулбар —
        // в разметке это 82) — поле всё ещё крупное, а больше половины остаётся ленте.
        Assert.Equal(368, ComposerHeightLimiter.Limit(1000, 82));
        Assert.Equal(183.5, ComposerHeightLimiter.Limit(590, 82));
    }

    [Fact]
    public void Attachments_come_out_of_the_same_budget()
    {
        // Полоса миниатюр растит композер сама по себе: не вычти её — и композер снова закрыл бы
        // больше отведённой ему доли.
        Assert.Equal(278, ComposerHeightLimiter.Limit(1000, 82 + 90));
    }

    [Fact]
    public void A_tiny_window_still_leaves_room_to_type() =>
        Assert.Equal(ComposerHeightLimiter.MinimumInput, ComposerHeightLimiter.Limit(200, 150));

    [Fact]
    public void A_long_prompt_stops_growing_and_leaves_the_conversation_on_screen()
    {
        var (boxHeight, maxHeight, composer, lane, chat) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var box = (TextBox)window.FindName("MessageTextBox")!;
            var chatArea = (Grid)window.FindName("Chat")!;
            var border = (Border)window.FindName("ComposerBorder")!;
            var scroller = (ScrollViewer)window.FindName("ChatScrollViewer")!;

            try
            {
                box.Text = LongPrompt();
                window.UpdateLayout();

                return (box.ActualHeight, box.MaxHeight, border.ActualHeight,
                    scroller.ActualHeight, chatArea.ActualHeight);
            }
            finally
            {
                // Непустое поле не даёт компактному режиму свернуть композер, а на это
                // рассчитывают соседние тесты в этой же коллекции.
                box.Clear();
                window.UpdateLayout();
            }
        });

        Assert.True(boxHeight <= maxHeight + 0.5, $"поле выросло до {boxHeight} при пределе {maxHeight}");
        Assert.True(composer <= chat * ComposerHeightLimiter.ChatShare + 0.5,
            $"композер занял {composer} из {chat}");
        Assert.True(lane > chat / 2, $"ленте осталось {lane} из {chat}");

        // Предел не должен оказаться крошечным: в окне по умолчанию это около десяти строк.
        Assert.True(maxHeight > 150, $"поле вышло слишком низким: {maxHeight}");
    }

    [Fact]
    public void A_taller_window_gets_a_taller_box()
    {
        var (small, large) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var box = (TextBox)window.FindName("MessageTextBox")!;
            var height = window.Height;

            try
            {
                window.Height = 600;
                window.UpdateLayout();
                var before = box.MaxHeight;

                window.Height = 900;
                window.UpdateLayout();
                return (before, box.MaxHeight);
            }
            finally
            {
                window.Height = height;
                window.UpdateLayout();
            }
        });

        // Фиксированное число пикселей в разметке вело бы себя иначе — и при масштабе интерфейса
        // (поддельный DPI) поле в полтора раза выше относительно окна.
        Assert.True(large > small, $"предел не вырос вместе с окном: {small} → {large}");
    }
}
