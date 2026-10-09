using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.Core;

/// <summary>
/// Стили, которыми пишутся документы: встроенные стили Word (заголовки, абзац списка, ссылка,
/// сетка таблицы) и два своих — код и цитата.
/// </summary>
/// <remarks>
/// У заголовков встроенные имена (<c>heading 1</c>): только по ним Word показывает их в навигации
/// и собирает оглавление. В чужом документе недостающие стили дописываются, а свои не трогаются —
/// вставленный абзац выглядит как остальной документ, а не как наш.
/// </remarks>
/// <summary>Под каким идентификатором стиль лежит в этом документе: наш — или такой же по имени, но свой.</summary>
internal sealed class WordStyleMap(IReadOnlyDictionary<string, string> ids)
{
    public string this[string logical] => ids.TryGetValue(logical, out var id) ? id : logical;
}

internal static class WordStyles
{
    public const string ListParagraph = "ListParagraph";
    public const string Code = "AmarinCode";
    public const string Quote = "Quote";
    public const string Hyperlink = "Hyperlink";
    public const string TableGrid = "TableGrid";

    /// <summary>
    /// Дописывает недостающие стили и отдаёт, под какими идентификаторами они лежат в этом документе.
    /// </summary>
    /// <remarks>
    /// Стиль ищется и по идентификатору, и по имени: в документе из русского Word «heading 1» лежит
    /// под идентификатором «1», а не «Heading1», и второй стиль с тем же именем Word показал бы
    /// двумя «Заголовками 1», а оглавление собрал бы только по одному из них.
    /// </remarks>
    public static WordStyleMap Ensure(MainDocumentPart main)
    {
        var part = main.StyleDefinitionsPart;
        var fresh = part is null;
        part ??= main.AddNewPart<StyleDefinitionsPart>();
        part.Styles ??= new W.Styles();
        var styles = part.Styles;

        if (fresh)
        {
            styles.Append(new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(
                    new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri", EastAsia = "Calibri" },
                    new W.FontSize { Val = "22" },
                    new W.FontSizeComplexScript { Val = "22" },
                    new W.Languages { Val = "ru-RU" })),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(
                    new W.SpacingBetweenLines { After = "160", Line = "276", LineRule = W.LineSpacingRuleValues.Auto }))));
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in styles.Elements<W.Style>())
        {
            if (existing.StyleId?.Value is not { } id)
            {
                continue;
            }

            ids.Add(id);
            if (existing.StyleName?.Val?.Value is { } name)
            {
                byName.TryAdd(name, id);
            }
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var style in Definitions())
        {
            var id = style.StyleId?.Value ?? "";
            var name = style.StyleName?.Val?.Value ?? id;
            if (ids.Contains(id))
            {
                map[id] = id;
            }
            else if (byName.TryGetValue(name, out var local))
            {
                map[id] = local;
            }
            else
            {
                // «Обычный» в документе мог называться иначе: дописанный стиль наследует его, а не пустоту.
                if (style.BasedOn is { } basedOn && basedOn.Val?.Value is { } parent && map.TryGetValue(parent, out var localParent))
                {
                    basedOn.Val = localParent;
                }

                if (style.NextParagraphStyle is { } next && next.Val?.Value is { } following && map.TryGetValue(following, out var localNext))
                {
                    next.Val = localNext;
                }

                styles.Append(style);
                ids.Add(id);
                map[id] = id;
            }
        }

        return new WordStyleMap(map);
    }

    private static IEnumerable<W.Style> Definitions()
    {
        yield return new W.Style(
            new W.StyleName { Val = "Normal" },
            new W.PrimaryStyle())
        { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true };

        yield return Heading(1, "32", "2F5496", before: "360", after: "120");
        yield return Heading(2, "26", "2F5496", before: "240", after: "80");
        yield return Heading(3, "24", "1F3763", before: "200", after: "80");

        yield return new W.Style(
            new W.StyleName { Val = "List Paragraph" },
            new W.BasedOn { Val = "Normal" },
            new W.UIPriority { Val = 34 },
            new W.PrimaryStyle(),
            new W.StyleParagraphProperties(
                new W.SpacingBetweenLines { After = "60" },
                new W.Indentation { Left = "720" },
                new W.ContextualSpacing()))
        { Type = W.StyleValues.Paragraph, StyleId = ListParagraph };

        yield return new W.Style(
            new W.StyleName { Val = "Amarin Code" },
            new W.BasedOn { Val = "Normal" },
            // Порядок свойств — по схеме OpenXML (заливка раньше интервалов): иначе Word находит
            // «содержимое, которое не удалось прочитать» и предлагает восстановить документ.
            new W.StyleParagraphProperties(
                new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F2F2F2" },
                new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.StyleRunProperties(
                new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas", ComplexScript = "Consolas" },
                new W.FontSize { Val = "19" }))
        { Type = W.StyleValues.Paragraph, StyleId = Code };

        yield return new W.Style(
            new W.StyleName { Val = "Quote" },
            new W.BasedOn { Val = "Normal" },
            new W.UIPriority { Val = 29 },
            new W.StyleParagraphProperties(
                new W.ParagraphBorders(new W.LeftBorder { Val = W.BorderValues.Single, Size = 18U, Space = 8U, Color = "BFBFBF" }),
                new W.Indentation { Left = "284" }),
            new W.StyleRunProperties(new W.Italic(), new W.Color { Val = "595959" }))
        { Type = W.StyleValues.Paragraph, StyleId = Quote };

        yield return new W.Style(
            new W.StyleName { Val = "Hyperlink" },
            new W.UIPriority { Val = 99 },
            new W.StyleRunProperties(new W.Color { Val = "0563C1" }, new W.Underline { Val = W.UnderlineValues.Single }))
        { Type = W.StyleValues.Character, StyleId = Hyperlink };

        yield return new W.Style(
            new W.StyleName { Val = "Table Grid" },
            new W.UIPriority { Val = 39 },
            new W.StyleParagraphProperties(new W.SpacingBetweenLines { After = "0", Line = "240", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.StyleTableProperties(
                new W.TableBorders(
                    new W.TopBorder { Val = W.BorderValues.Single, Size = 4U, Color = "BFBFBF" },
                    new W.LeftBorder { Val = W.BorderValues.Single, Size = 4U, Color = "BFBFBF" },
                    new W.BottomBorder { Val = W.BorderValues.Single, Size = 4U, Color = "BFBFBF" },
                    new W.RightBorder { Val = W.BorderValues.Single, Size = 4U, Color = "BFBFBF" },
                    new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "BFBFBF" },
                    new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4U, Color = "BFBFBF" }),
                new W.TableCellMarginDefault(
                    new W.TopMargin { Width = "60", Type = W.TableWidthUnitValues.Dxa },
                    new W.TableCellLeftMargin { Width = 108, Type = W.TableWidthValues.Dxa },
                    new W.BottomMargin { Width = "60", Type = W.TableWidthUnitValues.Dxa },
                    new W.TableCellRightMargin { Width = 108, Type = W.TableWidthValues.Dxa })))
        { Type = W.StyleValues.Table, StyleId = TableGrid };
    }

    private static W.Style Heading(int level, string size, string color, string before, string after) =>
        new(
            new W.StyleName { Val = "heading " + level },
            new W.BasedOn { Val = "Normal" },
            new W.NextParagraphStyle { Val = "Normal" },
            new W.UIPriority { Val = 9 },
            new W.PrimaryStyle(),
            new W.StyleParagraphProperties(
                new W.KeepNext(),
                new W.KeepLines(),
                new W.SpacingBetweenLines { Before = before, After = after },
                new W.OutlineLevel { Val = level - 1 }),
            new W.StyleRunProperties(
                new W.Bold(),
                new W.Color { Val = color },
                new W.FontSize { Val = size },
                new W.FontSizeComplexScript { Val = size }))
        { Type = W.StyleValues.Paragraph, StyleId = "Heading" + level };
}
