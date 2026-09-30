using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Печать и PDF (D6): документ на бумаге читается при любой теме окна.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class ChatPrintUiTests
{
    private readonly WpfFixture _wpf;

    public ChatPrintUiTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() => Application.Current.Windows.OfType<MainWindow>().Single();

    private static ChatExportDocument Export()
    {
        var session = new ChatSession { Id = "p", Title = "Print me", CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        session.Messages.Add(new ChatDisplayMessage { Id = "u", Role = "user", Text = "question", CreatedAt = DateTime.Now });
        session.Messages.Add(new ChatDisplayMessage
        {
            Id = "a",
            Role = "assistant",
            Text = "Answer with code:\n\n```powershell\nGet-Service Spooler\n```\n\nand $$x^2$$",
            CreatedAt = DateTime.Now
        });
        return ChatExport.Build(session, new ChatExportOptions(), DateTime.Now);
    }

    private static IEnumerable<object> Logical(DependencyObject root)
    {
        var stack = new Stack<object>([root]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (node is DependencyObject parent)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent))
                {
                    stack.Push(child);
                }
            }
        }
    }

    [Fact]
    public void Paper_gets_dark_text_whatever_the_theme_and_the_pages_are_laid_out()
    {
        var (luminance, pages, visibleButtons) = _wpf.Ui.Invoke(() =>
        {
            var document = ChatPrint.Build(Window(), Export(), new Size(816, 1056));
            var body = Logical(document).OfType<Run>().First(run => run.Text.Contains("Answer", StringComparison.Ordinal));
            var brush = (SolidColorBrush)body.Foreground;
            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.ComputePageCount();
            var buttons = Logical(document).OfType<System.Windows.Controls.Primitives.ButtonBase>()
                .Count(button => button.Visibility == Visibility.Visible);
            return ((0.2126 * brush.Color.R + 0.7152 * brush.Color.G + 0.0722 * brush.Color.B) / 255, paginator.PageCount, buttons);
        });

        Assert.True(luminance < 0.5, $"body text is too light for paper: {luminance}");
        Assert.True(pages >= 1);
        Assert.Equal(0, visibleButtons);
    }

    [Fact]
    public void A_formula_turns_into_a_png_data_address()
    {
        var picture = _wpf.Ui.Invoke(() => ChatPrint.FormulaPng(Window(), "\\frac{a}{b}", display: true));

        Assert.NotNull(picture);
        Assert.StartsWith("data:image/png;base64,", picture, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_window_keeps_its_buttons_on_the_card_with_a_long_report()
    {
        var inside = _wpf.Ui.Invoke(() =>
        {
            var overlay = new WorkReportOverlay { Width = 900, Height = 700 };
            var text = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"{i}. action number {i}"));
            overlay.Show(Window(), text, "status");
            overlay.Measure(new Size(900, 700));
            overlay.Arrange(new Rect(0, 0, 900, 700));
            overlay.UpdateLayout();
            var card = (FrameworkElement)overlay.FindName("Card");
            var copy = (FrameworkElement)overlay.FindName("CopyButton");
            var bottom = copy.TranslatePoint(new Point(0, copy.ActualHeight), card).Y;
            return bottom <= card.ActualHeight + 0.5;
        });

        Assert.True(inside);
    }
}
