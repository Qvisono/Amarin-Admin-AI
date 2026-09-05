using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Сборка FlowDocument. Paragraph, Table и Run — DispatcherObject, поэтому каждый тест
/// целиком считает результат на UI-потоке и наружу отдаёт уже обычные значения.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ChatMarkdownRenderTests
{
    private const string Everything = """
        # Заголовок

        Абзац с **жирным**, *курсивом*, ~~зачёркнутым~~, `кодом` и [ссылкой](https://example.com).

        ## Подзаголовок

        - один
          - вложенный
        - два

        1. первый
        2. второй

        - [x] сделано
        - [ ] не сделано

        > цитата

        | Ключ | Значение |
        |------|---------:|
        | a    | 1        |

        ---

        ```powershell
        $svc = Get-Service -Name 'Spooler'
        ```
        """;

    private readonly WpfFixture _wpf;

    public ChatMarkdownRenderTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Every_construct_lands_in_the_document()
    {
        var found = OnUi(blocks => new
        {
            BigHeading = blocks.Any(block => block is Paragraph { FontSize: > 15 }),
            Bullets = blocks.Any(block => block is List { MarkerStyle: TextMarkerStyle.Disc }),
            Numbers = blocks.Any(block => block is List { MarkerStyle: TextMarkerStyle.Decimal }),
            Tasks = blocks.Any(block => block is List { MarkerStyle: TextMarkerStyle.None }),
            Quote = blocks.Any(block => block is Section),
            Table = blocks.Any(block => block is Table),
            Code = blocks.Any(block => block is BlockUIContainer),
            Rule = blocks.Any(block => block is Paragraph { FontSize: 1 })
        });

        Assert.True(found.BigHeading, "заголовок не получил свой кегль");
        Assert.True(found.Bullets, "маркированный список");
        Assert.True(found.Numbers, "нумерованный список");
        Assert.True(found.Tasks, "чек-лист");
        Assert.True(found.Quote, "цитата");
        Assert.True(found.Table, "таблица");
        Assert.True(found.Code, "блок кода");
        Assert.True(found.Rule, "горизонтальная линия");
    }

    [Fact]
    public void Inline_markup_becomes_real_runs_instead_of_flattened_text()
    {
        var found = OnUi(blocks =>
        {
            var inlines = blocks
                .OfType<Paragraph>()
                .SelectMany(paragraph => Descendants(paragraph.Inlines))
                .ToList();

            return new
            {
                Bold = inlines.Any(i => Text(i) == "жирным" && Weight(i) == FontWeights.SemiBold),
                Italic = inlines.Any(i => Text(i) == "курсивом" && Style(i) == FontStyles.Italic),
                Strike = inlines.Any(i => Text(i) == "зачёркнутым" && Decorated(i)),
                Code = inlines.Any(i => Text(i)?.Trim() == "кодом" && Mono(i)),
                Link = inlines.OfType<Hyperlink>()
                    .Any(link => link.NavigateUri?.AbsoluteUri == "https://example.com/")
            };
        });

        Assert.True(found.Bold, "жирный");
        Assert.True(found.Italic, "курсив");
        Assert.True(found.Strike, "зачёркнутый");
        Assert.True(found.Code, "inline-код");
        Assert.True(found.Link, "ссылка");
    }

    [Fact]
    public void Table_keeps_header_row_and_column_alignment()
    {
        var table = OnUi(blocks =>
        {
            var rows = blocks.OfType<Table>().Single().RowGroups.Single().Rows;
            return new
            {
                Rows = rows.Count,
                Columns = blocks.OfType<Table>().Single().Columns.Count,
                HeaderWeight = rows[0].FontWeight,
                LastColumn = rows[1].Cells[1].TextAlignment
            };
        });

        Assert.Equal(2, table.Rows);
        Assert.Equal(2, table.Columns);
        Assert.Equal(FontWeights.SemiBold, table.HeaderWeight);

        // Вторая колонка объявлена как ---: значит по правому краю.
        Assert.Equal(TextAlignment.Right, table.LastColumn);
    }

    [Fact]
    public void Code_block_shows_its_language_and_copies_the_source()
    {
        var result = OnUi(blocks =>
        {
            var root = (FrameworkElement)blocks.OfType<BlockUIContainer>().Single().Child;
            root.Measure(new Size(600, 600));
            root.Arrange(new Rect(0, 0, 600, 600));

            var label = Find<TextBlock>(root).First();
            var before = label.Text;

            Find<Button>(root).First().RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            string? clipboard;
            try
            {
                clipboard = Clipboard.GetText();
            }
            catch
            {
                // Буфер обмена бывает занят другим процессом — тогда проверять нечего.
                clipboard = null;
            }

            return new { Language = before, AfterClick = label.Text, Clipboard = clipboard };
        });

        Assert.Equal("powershell", result.Language);
        Assert.Equal("Скопировано", result.AfterClick);
        if (result.Clipboard is not null)
        {
            Assert.Equal("$svc = Get-Service -Name 'Spooler'", result.Clipboard);
        }
    }

    [Fact]
    public void Code_block_is_syntax_coloured_rather_than_one_flat_run()
    {
        var distinctBrushes = OnUi(blocks =>
        {
            var root = (FrameworkElement)blocks.OfType<BlockUIContainer>().Single().Child;
            root.Measure(new Size(600, 600));
            root.Arrange(new Rect(0, 0, 600, 600));

            var code = Find<RichTextBox>(root).First();
            return code.Document.Blocks
                .OfType<Paragraph>()
                .SelectMany(paragraph => paragraph.Inlines)
                .Select(run => (run.Foreground as SolidColorBrush)?.Color)
                .Distinct()
                .Count();
        });

        Assert.True(distinctBrushes > 1, $"весь код покрашен одним цветом ({distinctBrushes})");
    }

    [Fact]
    public void Javascript_links_are_not_navigable()
    {
        var uris = OnUi(
            "[жми](javascript:alert(1)) и [ok](https://example.com)",
            blocks => blocks
                .OfType<Paragraph>()
                .SelectMany(paragraph => Descendants(paragraph.Inlines))
                .OfType<Hyperlink>()
                .Select(link => link.NavigateUri?.AbsoluteUri)
                .ToList());

        Assert.Equal(2, uris.Count);
        Assert.Null(uris[0]);
        Assert.Equal("https://example.com/", uris[1]);
    }

    [Fact]
    public void User_bubble_with_a_code_block_stretches_instead_of_hugging_the_text()
    {
        var (narrow, wide) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var plain = ChatMessageViews.CreateUser(window, Message("короткий вопрос"));
            var block = ChatMessageViews.CreateUser(window, Message("вот скрипт:\n\n```\nls\n```"));
            return (plain.Display.Width, block.Display.Width);
        });

        Assert.True(narrow is > 1 and < 400, $"обычный пузырь получил ширину {narrow}");
        Assert.True(double.IsNaN(wide), $"пузырь с блоком кода зафиксировал ширину {wide}");
    }

    [Theory]
    [InlineData("# Заго")]
    [InlineData("Текст с **недописанным")]
    [InlineData("```powershell\nGet-Serv")]
    [InlineData("| a | b |\n|---|")]
    [InlineData("- пункт\n  - вложен")]
    [InlineData("[ссылка](htt")]
    public void Half_typed_markdown_renders_without_throwing(string partial)
    {
        // Живой рендер зовут на каждом тике, то есть посреди любой конструкции.
        var blocks = OnUi(partial, list => list.Count);
        Assert.True(blocks > 0, partial);
    }

    [Fact]
    public void Unterminated_fence_is_already_a_code_block()
    {
        // Иначе блок кода «прыгал» бы в конце ответа, когда закрывающие ``` наконец придут.
        var language = OnUi("```powershell\n$svc = Get-Service", blocks =>
        {
            var root = (FrameworkElement)blocks.OfType<BlockUIContainer>().Single().Child;
            root.Measure(new Size(600, 600));
            root.Arrange(new Rect(0, 0, 600, 600));
            return Find<TextBlock>(root).First().Text;
        });

        Assert.Equal("powershell", language);
    }

    [Fact]
    public void Mouse_wheel_skips_the_non_scrolling_viewer_of_the_message_body()
    {
        // Ближайший ScrollViewer над блоком кода — внутренний, из шаблона тела сообщения,
        // и прокрутка в нём выключена. Колесо должно уходить дальше, к списку сообщений.
        var picked = _wpf.Ui.Invoke(() =>
        {
            var chat = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var codeBlock = new Border { Width = 100, Height = 40 };

            body.Content = codeBlock;
            chat.Content = body;

            chat.Measure(new Size(400, 400));
            chat.Arrange(new Rect(0, 0, 400, 400));
            chat.UpdateLayout();

            return ReferenceEquals(CodeBlockView.OuterScroller(codeBlock), chat);
        });

        Assert.True(picked, "колесо ушло бы в ScrollViewer, который не прокручивается");
    }

    [Fact]
    public void Streaming_body_is_hidden_only_while_it_is_still_empty()
    {
        var (empty, filled) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var view = ChatMessageViews.CreateAssistant(
                window,
                new ChatDisplayMessage
                {
                    Role = "assistant",
                    Id = "a1",
                    CreatedAt = DateTime.Now,
                    Status = AssistantStatus.Streaming
                });

            var hidden = view.Body.Visibility;
            view.SetBody("## уже что-то есть", streaming: true);
            return (hidden, view.Body.Visibility);
        });

        Assert.Equal(Visibility.Collapsed, empty);
        Assert.Equal(Visibility.Visible, filled);
    }

    private static ChatDisplayMessage Message(string text) =>
        new() { Role = "user", Id = "u1", Text = text, CreatedAt = DateTime.Now };

    private T OnUi<T>(Func<IReadOnlyList<Block>, T> inspect) => OnUi(Everything, inspect);

    private T OnUi<T>(string markdown, Func<IReadOnlyList<Block>, T> inspect) =>
        _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var view = ChatMessageViews.CreateAssistant(
                window,
                new ChatDisplayMessage
                {
                    Role = "assistant",
                    Id = "a1",
                    Text = markdown,
                    CreatedAt = DateTime.Now,
                    Status = AssistantStatus.Complete
                });
            return inspect(view.Body.Document.Blocks.ToList());
        });

    private static IEnumerable<Inline> Descendants(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is not Span span)
            {
                continue;
            }

            foreach (var child in Descendants(span.Inlines))
            {
                yield return child;
            }
        }
    }

    private static string? Text(Inline inline) => inline is Run run ? run.Text : null;

    private static FontWeight Weight(Inline inline) =>
        (FontWeight)inline.GetValue(TextElement.FontWeightProperty);

    private static FontStyle Style(Inline inline) =>
        (FontStyle)inline.GetValue(TextElement.FontStyleProperty);

    private static bool Mono(Inline inline) =>
        inline.FontFamily.Source.StartsWith("Consolas", StringComparison.Ordinal);

    private static bool Decorated(Inline inline) =>
        inline.TextDecorations is { Count: > 0 }
        || (inline.Parent as Span)?.TextDecorations is { Count: > 0 };

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Find<T>(child))
            {
                yield return nested;
            }
        }
    }
}
