using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Блок кода в ленте не таскает ленту за собой: щелчок по коду и по пустому месту блока
/// оставляют её на месте, а выделение за край — листает за кареткой.
/// </summary>
/// <remarks>
/// До 1.32.0 щелчок по коду ставил каретку, редактор просил показать её, и прокрутка документа
/// ответа (FlowDocumentView) переводила прямоугольник вложенного блока в верх сообщения: лента
/// уезжала к началу ответа прямо под мышью, а выделение тянулось вверх по всему блоку. Щелчок
/// правее строк ловил редактор самого ответа и уводил ленту к концу блока. Проверено настоящей
/// мышью; здесь те же просьбы поднимаются так, как их поднимает редактор.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class CodeBlockScrollTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-codescroll-" + Guid.NewGuid().ToString("N"));

    public CodeBlockScrollTests(WpfFixture wpf) => _wpf = wpf;

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
    public void The_caret_of_a_click_in_the_code_keeps_the_chat_where_it_is()
    {
        // Блок наполовину над краем ленты, щелчок — в его середине: каретка видна, листать нечего.
        var (before, after) = With(lines: 20, (window, scroller, code) =>
        {
            ScrollCodeTopAbove(window, scroller, code, 100);
            var before = scroller.VerticalOffset;
            code.BringIntoView(new Rect(60, code.ActualHeight / 2, 11, 15));
            Settle(window);
            return (before, scroller.VerticalOffset);
        });

        Assert.Equal(before, after);
    }

    [Fact]
    public void A_caret_below_the_chat_still_scrolls_it_down_to_the_caret()
    {
        // Выделение мышью за нижний край: лента листается за кареткой, а не к началу ответа.
        var (before, after, caretVisible) = With(lines: 80, (window, scroller, code) =>
        {
            ScrollCodeTopAbove(window, scroller, code, 50);
            var before = scroller.VerticalOffset;
            var caret = new Rect(60, code.ActualHeight - 200, 11, 15);
            code.BringIntoView(caret);
            Settle(window);
            var top = code.TranslatePoint(caret.TopLeft, scroller).Y;
            return (before, scroller.VerticalOffset, top >= 0 && top + caret.Height <= scroller.ViewportHeight);
        });

        Assert.True(after > before, $"лента не пошла за кареткой: {before} → {after}");
        Assert.True(caretVisible);
    }

    [Fact]
    public void A_press_on_the_empty_part_of_a_code_block_stays_out_of_the_answer_text()
    {
        // Правее строк — не код: нажатие гасится у блока, и редактор ответа его не видит — не
        // ставит каретку за блоком и не берёт мышь под выделение.
        var (handled, reachedAnswer) = With(lines: 20, (_, _, code) =>
        {
            var block = Ancestors(code).OfType<Border>().First(border => border.Child is StackPanel);
            var body = ((StackPanel)block.Child).Children[1];
            var answer = Ancestors(code).OfType<RichTextBox>().First();

            // Документ ответа стоит на пути нажатия раньше своего редактора: дошло до него —
            // дошло бы и до редактора.
            var reached = 0;
            answer.Document.AddHandler(UIElement.MouseDownEvent, new MouseButtonEventHandler((_, _) => reached++));
            var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseDownEvent };
            body.RaiseEvent(press);
            return (press.Handled, reached);
        });

        Assert.True(handled);
        Assert.Equal(0, reachedAnswer);
    }

    // ───────────────────────── оснастка ─────────────────────────

    private T With<T>(int lines, Func<MainWindow, ScrollViewer, RichTextBox, T> body) => _wpf.Ui.Invoke(() =>
    {
        var services = UiServices.Build(Path.Combine(_root, Guid.NewGuid().ToString("N")[..8]), "k", new HttpClientHandler());
        var code = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            code.Append("Write-Host 'line ").Append(i).Append("'\n");
        }

        var session = new ChatSession { Id = "code", Title = "Code", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        session.Messages.Add(new ChatDisplayMessage { Role = "user", Id = "u1", CreatedAt = DateTime.Now, Text = "Give me code" });
        session.Messages.Add(new ChatDisplayMessage
        {
            Role = "assistant",
            Id = "a1",
            CreatedAt = DateTime.Now,
            Text = "Here:\n\n```powershell\n" + code + "```\n\n" + string.Join("\n\n", Enumerable.Range(0, 12).Select(n => "Paragraph " + n)),
            Status = AssistantStatus.Complete
        });
        services.ChatStore.Save(session);
        services.ChatStore.Flush();

        var window = new MainWindow
        {
            Width = 1100,
            Height = 700,
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
            typeof(MainWindow).GetMethod("OpenChat", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["code"]);
            Settle(window);
            var scroller = (ScrollViewer)window.FindName("ChatScrollViewer")!;
            var box = Descendants<RichTextBox>(scroller).First(CodeBlockView.IsCode);
            return body(window, scroller, box);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Просьбу показать прямоугольник ScrollViewer исполняет на следующей раскладке, а
    /// переданную дальше — ещё через одну: несколько проходов доводят её до ленты.
    /// </summary>
    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            window.UpdateLayout();
        }
    }

    /// <summary>Верх блока кода — на <paramref name="hidden"/> точек выше края ленты.</summary>
    private static void ScrollCodeTopAbove(MainWindow window, ScrollViewer scroller, FrameworkElement code, double hidden)
    {
        var top = code.TranslatePoint(default, scroller).Y + scroller.VerticalOffset;
        scroller.ScrollToVerticalOffset(top + hidden);
        Settle(window);
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            yield return current;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
