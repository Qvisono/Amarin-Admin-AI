using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using P14 = DocumentFormat.OpenXml.Office2010.PowerPoint;

namespace Amarin.Core;

/// <summary>
/// Правка открытого .pptx: замена текста с сохранением оформления, вставка, замена и удаление
/// слайдов по номерам из чтения.
/// </summary>
/// <remarks>
/// Номера слайдов — те, что модель видела в <c>read_file</c>, и сопоставляются слайдам один раз, при
/// открытии: «вставь после 2 и удали 5» удаляет пятый слайд из прочитанного, а не тот, что стал
/// пятым после вставки. Так же устроен <see cref="WordEditor"/>.
/// </remarks>
internal sealed class SlidesEditor : IDisposable
{
    private readonly PresentationDocument _document;
    private readonly PresentationPart _presentation;
    private readonly List<SlidePart> _units;
    private readonly SlidesBuildReport _report = new();
    private readonly SlideBuilder _builder;

    private SlidesEditor(PresentationDocument document, Func<string, byte[]?> images)
    {
        _document = document;
        _presentation = document.PresentationPart ?? throw new DocumentException("The PowerPoint file has no slides.");
        _units = [.. SlideList.Parts(_presentation)];
        _builder = new SlideBuilder(_presentation, images, _report);
    }

    /// <summary>Что при вставке пришлось пропустить (картинки, которых нет).</summary>
    public IReadOnlyList<string> Skipped => _report.Skipped;

    public static SlidesEditor Open(string path, Func<string, byte[]?> images) =>
        new(PresentationDocument.Open(path, true), images);

    /// <summary>Заменяет текст во всех слайдах и заметках.</summary>
    /// <returns>Сколько совпадений нашлось и номера слайдов, где была замена.</returns>
    public (int Count, IReadOnlyList<int> Units) Replace(string find, string replacement, bool all)
    {
        if (string.IsNullOrEmpty(find))
        {
            throw new DocumentException("The text to find is empty.");
        }

        // Чтение показывает заголовок слайда с «## »: скопированный оттуда фрагмент ищется второй раз без разметки.
        replacement = WordEditor.WithoutReadMarkup(replacement);
        var matches = Matches(find);
        if (matches.Count == 0 && WordEditor.WithoutReadMarkup(find) is { Length: > 0 } plain && plain != find)
        {
            find = plain;
            matches = Matches(find);
        }

        if (matches.Count == 0)
        {
            throw new DocumentException(
                "The text was not found on any slide. Copy it from read_file without Markdown marks; a match cannot span two paragraphs.");
        }

        if (matches.Count > 1 && !all)
        {
            throw new DocumentException(
                $"The text occurs {matches.Count} times. Give a longer fragment that occurs once, or set all=true to replace every occurrence.");
        }

        foreach (var group in matches.GroupBy(match => match.Paragraph))
        {
            foreach (var (_, paragraph, index) in group.OrderByDescending(match => match.Index))
            {
                ReplaceAt(paragraph, index, find.Length, replacement);
            }
        }

        return (matches.Count, [.. matches.Select(match => match.Slide).Distinct().Order()]);
    }

    private List<(int Slide, A.Paragraph Paragraph, int Index)> Matches(string find)
    {
        var matches = new List<(int Slide, A.Paragraph Paragraph, int Index)>();
        var live = SlideList.Parts(_presentation).ToHashSet();
        for (var number = 1; number <= _units.Count; number++)
        {
            // Удалённый этим же вызовом слайд уже не в пакете: читать его нельзя, искать в нём нечего.
            var slide = _units[number - 1];
            if (!live.Contains(slide))
            {
                continue;
            }

            var paragraphs = (slide.Slide?.Descendants<A.Paragraph>() ?? [])
                .Concat(slide.NotesSlidePart?.NotesSlide?.Descendants<A.Paragraph>() ?? []);
            foreach (var paragraph in paragraphs)
            {
                var text = Texts(paragraph).Text;
                for (var at = text.IndexOf(find, StringComparison.Ordinal); at >= 0; at = text.IndexOf(find, at + find.Length, StringComparison.Ordinal))
                {
                    matches.Add((number, paragraph, at));
                }
            }
        }

        return matches;
    }

    public void Insert(int slide, bool before, string markdown)
    {
        var parts = Build(markdown);
        if (slide == 0 && before)
        {
            SlideList.Insert(_presentation, parts, 0);
            return;
        }

        var index = SlideList.IndexOf(_presentation, Unit(slide));
        SlideList.Insert(_presentation, parts, before ? index : index + 1);
    }

    public void Set(int slide, string markdown)
    {
        var old = Unit(slide);
        SlideList.Insert(_presentation, Build(markdown), SlideList.IndexOf(_presentation, old));
        SlideList.Remove(_presentation, old);
    }

    public void Delete(int from, int to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        var doomed = Enumerable.Range(from, to - from + 1).Select(Unit).ToList();
        if (doomed.Count >= SlideList.Parts(_presentation).Count())
        {
            throw new DocumentException("These are all the slides. A presentation needs at least one; replace the last slide with set instead.");
        }

        foreach (var part in doomed)
        {
            SlideList.Remove(_presentation, part);
        }
    }

    public void Append(string markdown) =>
        SlideList.Insert(_presentation, Build(markdown), SlideList.Parts(_presentation).Count());

    public void Save() => _presentation.Presentation?.Save();

    public void Dispose() => _document.Dispose();

    private List<SlidePart> Build(string markdown)
    {
        var parts = _builder.Build(MarkdownBlocks.Parse(markdown));
        return parts.Count > 0 ? parts : throw new DocumentException("Nothing to add: the Markdown is empty.");
    }

    /// <summary>Слайд по номеру из чтения; удалённый этим же вызовом — отказ, а не тихая правка не того.</summary>
    private SlidePart Unit(int number)
    {
        if (number < 1 || number > _units.Count)
        {
            throw new DocumentException($"There is no slide {number}: the presentation has {_units.Count}. Read it again to get current numbers.");
        }

        var part = _units[number - 1];
        return SlideList.IndexOf(_presentation, part) < 0
            ? throw new DocumentException($"Slide {number} was already removed by an earlier operation in this call.")
            : part;
    }

    private static (string Text, List<A.Text> Parts) Texts(A.Paragraph paragraph)
    {
        var parts = paragraph.Descendants<A.Text>().ToList();
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            text.Append(part.Text);
        }

        return (text.ToString(), parts);
    }

    /// <summary>Заменяет совпадение: новый текст — в куске, где оно начиналось, остальное вырезается.</summary>
    private static void ReplaceAt(A.Paragraph paragraph, int index, int length, string replacement)
    {
        var (_, parts) = Texts(paragraph);
        var offset = 0;
        var remaining = length;
        var placed = false;
        foreach (var part in parts)
        {
            var text = part.Text;
            var start = offset;
            offset += text.Length;
            if (offset <= index || remaining == 0)
            {
                continue;
            }

            var from = Math.Max(0, index - start);
            var take = Math.Min(text.Length - from, remaining);
            part.Text = placed ? text[..from] + text[(from + take)..] : text[..from] + replacement + text[(from + take)..];
            placed = true;
            remaining -= take;
            index += take;
        }
    }
}

/// <summary>Список слайдов презентации: порядок, вставка и удаление вместе с разделами и произвольными показами.</summary>
/// <remarks>
/// Слайд упоминается не только в списке слайдов: разделы PowerPoint (<c>p14:sectionLst</c>) и
/// произвольные показы держат свои ссылки. Оставленная там ссылка на удалённый слайд или новый
/// слайд вне всех разделов — и PowerPoint предлагает «восстановить» файл.
/// </remarks>
internal static class SlideList
{
    public static IEnumerable<SlidePart> Parts(PresentationPart presentation)
    {
        foreach (var id in presentation.Presentation?.SlideIdList?.Elements<P.SlideId>() ?? [])
        {
            if (id.RelationshipId?.Value is { } relation && presentation.GetPartById(relation) is SlidePart slide)
            {
                yield return slide;
            }
        }
    }

    public static int IndexOf(PresentationPart presentation, SlidePart slide) => Parts(presentation).ToList().IndexOf(slide);

    /// <summary>Ставит слайды в список перед слайдом с номером <paramref name="index"/> (с нуля); за концом — в конец.</summary>
    public static void Insert(PresentationPart presentation, IReadOnlyList<SlidePart> slides, int index)
    {
        var root = presentation.Presentation ?? throw new DocumentException("The PowerPoint file has no presentation part.");
        var list = root.SlideIdList ??= new P.SlideIdList();
        var existing = list.Elements<P.SlideId>().ToList();
        var next = Math.Max(256U, existing.Select(item => item.Id?.Value ?? 0U).DefaultIfEmpty(255U).Max() + 1);
        var anchor = index >= 0 && index < existing.Count ? existing[index] : null;

        // Новый слайд встаёт в раздел соседа: перед первым — в раздел первого, иначе — в раздел предыдущего.
        var neighbour = index > 0 && index - 1 < existing.Count ? existing[index - 1] : existing.FirstOrDefault();
        var section = neighbour is null ? null : SectionEntry(root, neighbour.Id?.Value ?? 0U);
        var sectionAfter = index > 0 && section is not null;

        foreach (var slide in slides)
        {
            var id = new P.SlideId { Id = next++, RelationshipId = presentation.GetIdOfPart(slide) };
            if (anchor is null)
            {
                list.Append(id);
            }
            else
            {
                anchor.InsertBeforeSelf(id);
            }

            if (section is not null)
            {
                var entry = new P14.SectionSlideIdListEntry { Id = id.Id };
                if (sectionAfter)
                {
                    section.InsertAfterSelf(entry);
                    section = entry;
                }
                else
                {
                    section.InsertBeforeSelf(entry);
                }
            }
        }
    }

    /// <summary>Убирает слайд из списка, разделов и показов и удаляет его часть из пакета.</summary>
    public static void Remove(PresentationPart presentation, SlidePart slide)
    {
        var root = presentation.Presentation ?? throw new DocumentException("The PowerPoint file has no presentation part.");
        var relation = presentation.GetIdOfPart(slide);
        foreach (var id in root.SlideIdList?.Elements<P.SlideId>().Where(item => item.RelationshipId?.Value == relation).ToList() ?? [])
        {
            SectionEntry(root, id.Id?.Value ?? 0U)?.Remove();
            id.Remove();
        }

        foreach (var entry in root.Descendants<P.SlideListEntry>().Where(item => item.Id?.Value == relation).ToList())
        {
            entry.Remove();
        }

        presentation.DeletePart(slide);
    }

    private static P14.SectionSlideIdListEntry? SectionEntry(OpenXmlElement root, uint id) =>
        root.Descendants<P14.SectionSlideIdListEntry>().FirstOrDefault(entry => entry.Id?.Value == id);
}
