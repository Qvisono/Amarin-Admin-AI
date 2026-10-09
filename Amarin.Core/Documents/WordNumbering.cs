using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Amarin.Core;

/// <summary>Нумерация списков Word: один шаблон маркеров и один нумерованный, экземпляр — на каждый список.</summary>
/// <remarks>
/// Шаблоны узнаются по своему имени и дописываются в чужой документ, только если их там нет: его
/// собственные списки остаются как были. В разметке нумерации все шаблоны идут раньше экземпляров
/// — Word строг к этому порядку и иначе открывает файл с ошибкой.
/// </remarks>
internal static class WordNumbering
{
    private const string BulletName = "AmarinBullets";
    private const string OrderedName = "AmarinNumbers";

    /// <summary>Новый экземпляр списка; нумерованный начинается с <paramref name="start"/>.</summary>
    public static int NewList(MainDocumentPart main, bool ordered, int start)
    {
        var part = main.NumberingDefinitionsPart ?? main.AddNewPart<NumberingDefinitionsPart>();
        part.Numbering ??= new W.Numbering();
        var numbering = part.Numbering;

        var abstractId = Template(numbering, ordered);
        var numId = numbering.Elements<W.NumberingInstance>().Select(n => n.NumberID?.Value ?? 0).DefaultIfEmpty(0).Max() + 1;
        var instance = new W.NumberingInstance(new W.AbstractNumId { Val = abstractId }) { NumberID = numId };
        if (ordered)
        {
            instance.Append(new W.LevelOverride(new W.StartOverrideNumberingValue { Val = Math.Max(1, start) }) { LevelIndex = 0 });
        }

        numbering.Append(instance);
        return numId;
    }

    private static int Template(W.Numbering numbering, bool ordered)
    {
        var name = ordered ? OrderedName : BulletName;
        var existing = numbering.Elements<W.AbstractNum>()
            .FirstOrDefault(item => item.GetFirstChild<W.AbstractNumDefinitionName>()?.Val?.Value == name);
        if (existing?.AbstractNumberId?.Value is { } id)
        {
            return id;
        }

        var next = numbering.Elements<W.AbstractNum>().Select(item => item.AbstractNumberId?.Value ?? 0).DefaultIfEmpty(-1).Max() + 1;
        var template = new W.AbstractNum(
            new W.MultiLevelType { Val = W.MultiLevelValues.HybridMultilevel },
            new W.AbstractNumDefinitionName { Val = name })
        { AbstractNumberId = next };

        for (var level = 0; level < 9; level++)
        {
            template.Append(Level(level, ordered));
        }

        // Шаблоны — раньше экземпляров: Word читает разметку нумерации в этом порядке.
        if (numbering.Elements<W.NumberingInstance>().FirstOrDefault() is { } firstInstance)
        {
            numbering.InsertBefore(template, firstInstance);
        }
        else
        {
            numbering.Append(template);
        }

        return next;
    }

    private static W.Level Level(int level, bool ordered)
    {
        var indent = (720 * (level + 1)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (format, text) = ordered
            ? (level % 3) switch
            {
                0 => (W.NumberFormatValues.Decimal, $"%{level + 1}."),
                1 => (W.NumberFormatValues.LowerLetter, $"%{level + 1}."),
                _ => (W.NumberFormatValues.LowerRoman, $"%{level + 1}.")
            }
            : (W.NumberFormatValues.Bullet, (level % 3) switch { 0 => "•", 1 => "◦", _ => "▪" });

        var result = new W.Level(
            new W.StartNumberingValue { Val = 1 },
            new W.NumberingFormat { Val = format },
            new W.LevelText { Val = text },
            new W.LevelJustification { Val = W.LevelJustificationValues.Left },
            new W.PreviousParagraphProperties(new W.Indentation { Left = indent, Hanging = "360" }))
        { LevelIndex = level };

        if (!ordered)
        {
            result.Append(new W.NumberingSymbolRunProperties(new W.RunFonts { Ascii = "Arial", HighAnsi = "Arial", Hint = W.FontTypeHintValues.Default }));
        }

        return result;
    }
}
