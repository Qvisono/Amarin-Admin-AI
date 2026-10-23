using System.Drawing.Imaging;
using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Презентации, которые собирает и правит программа, открываются в PowerPoint без «восстановления»
/// (схема OpenXml), читаются обратно тем же чтением, что видит модель, а правка по номерам слайдов
/// попадает в те слайды, что модель прочла.
/// </summary>
public sealed class PowerPointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amarin-pptx-" + Guid.NewGuid().ToString("N"));

    public PowerPointTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static void AssertValid(string path)
    {
        using var document = PresentationDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2016).Validate(document)
            .Select(error => $"{error.Path?.XPath}: {error.Description}")
            .ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    private static List<string> Slides(string path) =>
        [.. SlidesReader.Read(new DocumentSource(path, null), new DocumentWindow()).Sections.SelectMany(section => section.Units).Select(unit => unit.Text)];

    private const string Deck = """
        # Квартальный отчёт

        Итоги третьего квартала

        ## Выручка

        - Июль выше плана
        - Август на уровне
          1. вложенный пункт

        | Месяц | Сумма |
        |---|---|
        | Июль | 120 |
        | Август | 140 |

        Notes: сказать про сезонность

        ## Планы

        Абзац с **жирным**, `кодом` и [ссылкой](https://example.com).

        ---

        Слайд без заголовка.
        """;

    [Fact]
    public void A_new_presentation_passes_the_schema_and_reads_back_slide_by_slide()
    {
        var path = PathOf("deck.pptx");
        SlidesWriter.Create(path, Deck, _ => null);

        AssertValid(path);
        var slides = Slides(path);
        Assert.Equal(4, slides.Count);
        Assert.StartsWith("## Квартальный отчёт", slides[0], StringComparison.Ordinal);
        Assert.Contains("Итоги третьего квартала", slides[0], StringComparison.Ordinal);
        Assert.Contains("Июль выше плана", slides[1], StringComparison.Ordinal);
        Assert.Contains("| Август | 140 |", slides[1], StringComparison.Ordinal);
        Assert.Contains("Notes: сказать про сезонность", slides[1], StringComparison.Ordinal);
        Assert.DoesNotContain("Notes:", slides[2], StringComparison.Ordinal);
        Assert.Equal("Слайд без заголовка.", slides[3]);
    }

    [Fact]
    public void The_first_heading_with_a_subtitle_takes_the_title_layout_and_the_rest_title_and_content()
    {
        var path = PathOf("layouts.pptx");
        SlidesWriter.Create(path, Deck, _ => null);

        using var document = PresentationDocument.Open(path, false);
        var layouts = SlideList.Parts(document.PresentationPart!).Select(slide => slide.SlideLayoutPart?.SlideLayout?.Type?.Value).ToList();
        Assert.Equal(P.SlideLayoutValues.Title, layouts[0]);
        Assert.All(layouts.Skip(1), type => Assert.Equal(P.SlideLayoutValues.Object, type));
    }

    [Fact]
    public void A_crowded_slide_gets_a_smaller_font_than_a_short_one()
    {
        var path = PathOf("sizes.pptx");
        var crowded = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"- Пункт номер {i} с довольно длинным пояснением, которое занимает почти всю строку слайда"));
        SlidesWriter.Create(path, $"## Коротко\n\nОдна строка.\n\n## Много\n\n{crowded}\n", _ => null);

        using var document = PresentationDocument.Open(path, false);
        var sizes = SlideList.Parts(document.PresentationPart!)
            .Select(slide => slide.Slide!.Descendants<A.RunProperties>().Where(run => run.Parent?.Parent?.Parent is P.TextBody body && body.Parent is P.Shape shape &&
                shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape?.Index?.Value == 1U).Max(run => run.FontSize!.Value))
            .ToList();
        Assert.True(sizes[1] < sizes[0], $"crowded {sizes[1]} vs short {sizes[0]}");
    }

    [Fact]
    public void A_picture_lands_on_its_slide_inside_the_slide()
    {
        using var bitmap = new System.Drawing.Bitmap(400, 200);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var png = stream.ToArray();
        var path = PathOf("picture.pptx");
        var report = SlidesWriter.Create(path, "## Схема\n\n![Схема сети](net.png)\n", source => source == "net.png" ? png : null);

        Assert.Empty(report.Skipped);
        AssertValid(path);
        using var document = PresentationDocument.Open(path, false);
        var picture = Assert.Single(SlideList.Parts(document.PresentationPart!).Single().Slide!.Descendants<P.Picture>());
        var transform = picture.ShapeProperties!.Transform2D!;
        Assert.True(transform.Offset!.X!.Value + transform.Extents!.Cx!.Value <= SlidesTemplate.Width);
        Assert.True(transform.Offset!.Y!.Value + transform.Extents!.Cy!.Value <= SlidesTemplate.Height);
    }

    [Fact]
    public void Editing_by_slide_numbers_refers_to_the_slides_as_read()
    {
        var path = PathOf("edit.pptx");
        SlidesWriter.Create(path, "## Один\n\nтекст 1\n\n## Два\n\nтекст 2\n\n## Три\n\nтекст 3\n", _ => null);

        DocumentFiles.EditCopy(path, path, overwrite: true, copy =>
        {
            using var editor = SlidesEditor.Open(copy, _ => null);
            editor.Insert(1, before: false, "## Вставка\n\nновый слайд");
            editor.Delete(3, 3);
            editor.Set(2, "## Второй заново\n\nзаменён\n\nNotes: заметка");
            editor.Replace("текст 1", "первый текст", all: false);
            editor.Append("## Конец\n\nпоследний");
            editor.Save();
        });

        AssertValid(path);
        var slides = Slides(path);
        Assert.Equal(["## Один", "## Вставка", "## Второй заново", "## Конец"], slides.Select(slide => slide.Split('\n')[0]));
        Assert.Contains("первый текст", slides[0], StringComparison.Ordinal);
        Assert.Contains("Notes: заметка", slides[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_slide_removed_earlier_in_the_call_is_refused_not_silently_mistaken()
    {
        var path = PathOf("twice.pptx");
        SlidesWriter.Create(path, "## Один\n\n## Два\n\n## Три\n", _ => null);

        using var editor = SlidesEditor.Open(path, _ => null);
        editor.Delete(2, 2);
        var error = Assert.Throws<DocumentException>(() => editor.Set(2, "## Снова"));
        Assert.Contains("already removed", error.Message, StringComparison.Ordinal);
        Assert.Throws<DocumentException>(() => editor.Delete(1, 3));
    }

    [Fact]
    public void A_new_slide_joins_the_section_of_its_neighbour()
    {
        var path = PathOf("sections.pptx");
        SlidesWriter.Create(path, "## Один\n\n## Два\n", _ => null);
        using (var document = PresentationDocument.Open(path, true))
        {
            var root = document.PresentationPart!.Presentation!;
            var ids = root.SlideIdList!.Elements<P.SlideId>().Select(id => id.Id!.Value).ToList();
            var sections = new DocumentFormat.OpenXml.Office2010.PowerPoint.SectionList(
                new DocumentFormat.OpenXml.Office2010.PowerPoint.Section(
                    new DocumentFormat.OpenXml.Office2010.PowerPoint.SectionSlideIdList(
                        ids.Select(id => new DocumentFormat.OpenXml.Office2010.PowerPoint.SectionSlideIdListEntry { Id = id })))
                { Name = "Main", Id = "{6A3B1E2C-1111-4C1D-9E7F-000000000001}" });
            root.PresentationExtensionList = new P.PresentationExtensionList(
                new P.PresentationExtension(sections) { Uri = "{521415D9-36F7-43E2-AB2F-B90AF26B5E84}" });
            root.Save();
        }

        DocumentFiles.EditCopy(path, path, overwrite: true, copy =>
        {
            using var editor = SlidesEditor.Open(copy, _ => null);
            editor.Insert(1, before: false, "## Между");
            editor.Delete(2, 2);
            editor.Save();
        });

        AssertValid(path);
        using var check = PresentationDocument.Open(path, false);
        var presentation = check.PresentationPart!.Presentation!;
        var slideIds = presentation.SlideIdList!.Elements<P.SlideId>().Select(id => id.Id!.Value).ToList();
        var sectionIds = presentation.Descendants<DocumentFormat.OpenXml.Office2010.PowerPoint.SectionSlideIdListEntry>().Select(entry => entry.Id!.Value).ToList();
        Assert.Equal(slideIds, sectionIds);
    }

    [Fact]
    public async Task The_tools_create_and_change_a_presentation_end_to_end()
    {
        var path = PathOf("tool.pptx");
        var state = new FileToolState();
        var created = await new CreateDocumentTool(state).ExecuteAsync(Args(new { path, content = "## Первый\n\nабзац\n\n## Второй\n\nещё" }));
        Assert.True(created.Success, created.Output);

        Assert.True((await new ReadFileTool(state).ExecuteAsync(Args(new { path }))).Success);
        var edited = await new EditDocumentTool(state).ExecuteAsync(Args(new
        {
            path,
            operations = new object[]
            {
                new { op = "replace", find = "абзац", replace = "строка" },
                new { op = "delete", slide = 2 },
                new { op = "append", content = "## Третий\n\nконец" }
            }
        }));
        Assert.True(edited.Success, edited.Output);

        AssertValid(path);
        var slides = Slides(path);
        Assert.Equal(2, slides.Count);
        Assert.Contains("строка", slides[0], StringComparison.Ordinal);
        Assert.StartsWith("## Третий", slides[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_operation_names_what_PowerPoint_takes()
    {
        var path = PathOf("ops.pptx");
        SlidesWriter.Create(path, "## Один", _ => null);

        var result = await new EditDocumentTool().ExecuteAsync(Args(new { path, operations = new object[] { new { op = "set_cells", sheet = "x" } } }));

        Assert.False(result.Success);
        Assert.Contains("not a PowerPoint operation", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_presentation_counts_as_a_document_for_the_quiet_write()
    {
        Assert.True(DocumentZone.IsDocumentFile(@"C:\Users\someone\Downloads\deck.pptx"));
        Assert.Contains("create_document", WriteFileTool.BinaryRefusal("deck.pptx"), StringComparison.Ordinal);
    }
}
