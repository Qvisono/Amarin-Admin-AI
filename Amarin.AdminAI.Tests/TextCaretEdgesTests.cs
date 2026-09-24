using System.Windows;
using System.Windows.Controls;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Стрелка вверх на первой строке поля уводит каретку в начало текста, вниз на последней —
/// в конец. Как в Discord.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TextCaretEdgesTests
{
    private const string Text = "длыоуфкрпадлыфуоркп\nывакпдлывокапщдшыувп\nшлрваыпдлырвкпадло";

    private readonly WpfFixture _wpf;

    public TextCaretEdgesTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Up_on_the_first_line_goes_to_the_very_start()
    {
        var (jumped, caret) = WithBox(box =>
        {
            box.CaretIndex = 7;
            return (TextCaretEdges.TryJump(box, up: true), box.CaretIndex);
        });

        Assert.True(jumped);
        Assert.Equal(0, caret);
    }

    [Fact]
    public void Down_on_the_last_line_goes_to_the_very_end()
    {
        var (jumped, caret) = WithBox(box =>
        {
            box.CaretIndex = Text.Length - 6;
            return (TextCaretEdges.TryJump(box, up: false), box.CaretIndex);
        });

        Assert.True(jumped);
        Assert.Equal(Text.Length, caret);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void On_a_middle_line_the_arrow_moves_between_lines_as_usual(bool up)
    {
        var (jumped, caret) = WithBox(box =>
        {
            box.CaretIndex = 25;
            return (TextCaretEdges.TryJump(box, up), box.CaretIndex);
        });

        Assert.False(jumped);
        Assert.Equal(25, caret);
    }

    [Fact]
    public void A_wrapped_line_counts_as_its_own_line()
    {
        // Строка — та, что на экране: по ней водят каретку сами стрелки. Длинная первая строка,
        // перенесённая в две, на своей второй половине ещё не «первая».
        var jumped = WithBox(box =>
        {
            box.Text = string.Join(' ', Enumerable.Repeat("слово", 40));
            box.UpdateLayout();
            box.CaretIndex = box.Text.Length - 3;
            return box.LineCount > 1 && !TextCaretEdges.TryJump(box, up: true);
        });

        Assert.True(jumped);
    }

    [Fact]
    public void At_the_edge_already_the_key_is_left_alone()
    {
        var jumped = WithBox(box =>
        {
            box.CaretIndex = 0;
            return TextCaretEdges.TryJump(box, up: true);
        });

        Assert.False(jumped);
    }

    [Fact]
    public void The_composer_answers_a_real_key_press()
    {
        // Подключено в двух местах; здесь — что его получило поле ввода окна, по настоящему
        // событию клавиши, а не прямым вызовом.
        var jumped = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var box = (TextBox)window.FindName("MessageTextBox")!;
            var saved = box.Text;
            try
            {
                box.Text = "одна строка";
                box.UpdateLayout();
                box.CaretIndex = 4;
                var args = new System.Windows.Input.KeyEventArgs(
                    System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(box)!,
                    Environment.TickCount,
                    System.Windows.Input.Key.Up)
                {
                    RoutedEvent = UIElement.PreviewKeyDownEvent
                };
                box.RaiseEvent(args);
                return box.CaretIndex == 0;
            }
            finally
            {
                box.Text = saved;
            }
        });

        Assert.True(jumped);
    }

    private T WithBox<T>(Func<TextBox, T> body) =>
        _wpf.Ui.Invoke(() =>
        {
            var box = new TextBox { Text = Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            var host = new Window
            {
                Width = 420,
                Height = 200,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                Content = box
            };

            try
            {
                host.Show();
                host.UpdateLayout();
                return body(box);
            }
            finally
            {
                host.Close();
            }
        });
}
