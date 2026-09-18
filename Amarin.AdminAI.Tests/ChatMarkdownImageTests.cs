using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Markdown images used to be downgraded to a plain text link, so an assistant could not put a
/// picture in its answer at all. They now render as a real image block between the paragraphs,
/// which is what lets the model choose where an illustration goes.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ChatMarkdownImageTests
{
    private readonly WpfFixture _wpf;

    public ChatMarkdownImageTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>A 1x1 transparent PNG.</summary>
    private const string PixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private IReadOnlyList<Block> Render(string markdown) => _wpf.Ui.Invoke(() =>
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
        return (IReadOnlyList<Block>)view.Body.Document.Blocks.ToList();
    });

    [Fact]
    public void An_image_paragraph_becomes_a_picture_between_the_text()
    {
        var blocks = Render($"""
            До картинки.

            ![схема](data:image/png;base64,{PixelPng})

            После картинки.
            """);

        var kinds = _wpf.Ui.Invoke(() => blocks.Select(b => b.GetType().Name).ToList());

        // Paragraph, image container, paragraph — in that order, so the picture really does sit
        // between the two pieces of prose rather than being appended at the end.
        Assert.Equal(3, kinds.Count);
        Assert.Equal(nameof(Paragraph), kinds[0]);
        Assert.Equal(nameof(BlockUIContainer), kinds[1]);
        Assert.Equal(nameof(Paragraph), kinds[2]);

        var hasImage = _wpf.Ui.Invoke(() =>
            ((BlockUIContainer)blocks[1]).Child is Border { Child: Image { Source: not null } });
        Assert.True(hasImage, "the block did not decode into an actual image");
    }

    [Fact]
    public void A_registered_handle_resolves_to_the_generated_picture()
    {
        var handle = ChatImageRegistry.Register(new ImageAttachment(PixelPng, "image/png"));

        var blocks = Render($"Вот результат:\n\n![инфографика]({handle})");
        var hasImage = _wpf.Ui.Invoke(() => blocks
            .OfType<BlockUIContainer>()
            .Any(container => container.Child is Border { Child: Image { Source: not null } }));

        Assert.True(hasImage, $"handle {handle} did not resolve to an image");
    }

    [Fact]
    public void An_unknown_handle_degrades_to_a_note_instead_of_throwing()
    {
        var blocks = Render($"![схема]({ChatImageRegistry.Scheme}deadbeef)");
        var text = _wpf.Ui.Invoke(() => blocks
            .OfType<BlockUIContainer>()
            .Select(container => (container.Child as Border)?.Child)
            .OfType<TextBlock>()
            .Select(block => block.Text)
            .FirstOrDefault());

        Assert.NotNull(text);
        Assert.Contains("недоступно", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_image_inside_a_sentence_still_becomes_a_picture()
    {
        // A picture used to appear only when it had a paragraph to itself; anything the model
        // wrote around it — even one word — silently turned the illustration into a blue link.
        // The paragraph is now cut into text, picture, text instead.
        var blocks = Render($"Смотри ![тут](data:image/png;base64,{PixelPng}) внимательно.");
        var kinds = _wpf.Ui.Invoke(() => blocks.Select(b => b.GetType().Name).ToList());

        Assert.Equal([nameof(Paragraph), nameof(BlockUIContainer), nameof(Paragraph)], kinds);

        var (before, after, hasImage) = _wpf.Ui.Invoke(() => (
            new TextRange(blocks[0].ContentStart, blocks[0].ContentEnd).Text.Trim(),
            new TextRange(blocks[2].ContentStart, blocks[2].ContentEnd).Text.Trim(),
            ((BlockUIContainer)blocks[1]).Child is Border { Child: Image { Source: not null } }));

        Assert.Equal("Смотри", before);
        Assert.Equal("внимательно.", after);
        Assert.True(hasImage, "the embedded image did not decode");
    }

    [Fact]
    public void A_picture_that_opens_a_paragraph_keeps_the_trailing_text()
    {
        var blocks = Render($"![тут](data:image/png;base64,{PixelPng}) - вот так.");
        var kinds = _wpf.Ui.Invoke(() => blocks.Select(b => b.GetType().Name).ToList());

        // No empty paragraph in front of the picture: blank runs are dropped.
        Assert.Equal([nameof(BlockUIContainer), nameof(Paragraph)], kinds);
    }

    [Fact]
    public void Registry_ignores_labels_that_are_not_handles()
    {
        ChatImageRegistry.Restore(new ImageAttachment(PixelPng, "image/png", "не-хендл"));
        Assert.Null(ChatImageRegistry.Find("не-хендл"));
    }
}
