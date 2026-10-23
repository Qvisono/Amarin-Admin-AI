using DocumentFormat.OpenXml.Packaging;

namespace Amarin.Core;

/// <summary>PowerPoint (.pptx) из Markdown: слайд на каждый заголовок H1/H2, тема и раскладки PowerPoint.</summary>
internal static class SlidesWriter
{
    /// <summary>Создаёт презентацию. Файл пишется целиком во временный рядом и только потом встаёт на место.</summary>
    public static SlidesBuildReport Create(string path, string markdown, Func<string, byte[]?> images, bool overwrite = false)
    {
        var blocks = MarkdownBlocks.Parse(markdown);
        if (SlideBuilder.Split(blocks).Count == 0)
        {
            throw new DocumentException("The presentation is empty: give content with a heading for each slide.");
        }

        var report = new SlidesBuildReport();
        DocumentFiles.WriteAtomically(path, stream =>
        {
            using var document = PresentationDocument.Create(stream, DocumentFormat.OpenXml.PresentationDocumentType.Presentation);
            var presentation = SlidesTemplate.Create(document);
            SlideList.Insert(presentation, new SlideBuilder(presentation, images, report).Build(blocks), index: 0);
            presentation.Presentation?.Save();
        }, overwrite);
        return report;
    }
}
