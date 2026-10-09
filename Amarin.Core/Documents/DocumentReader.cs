using System.Globalization;
using System.Text;

namespace Amarin.Core;

/// <summary>
/// Чтение файла для модели: текст, Word, Excel, PowerPoint и PDF — нумерованными единицами, с
/// заголовком «что это и сколько всего» и подсказкой, откуда читать дальше.
/// </summary>
internal static class DocumentReader
{
    /// <summary>Читает окно файла. Не бросает ничего, кроме <see cref="DocumentException"/>.</summary>
    public static DocumentContent Read(string path, DocumentWindow window) => Read(DocumentSource.File(path), window);

    /// <summary>Читает окно документа — файла или вложения из памяти.</summary>
    public static DocumentContent Read(DocumentSource source, DocumentWindow window)
    {
        var kind = DocumentKinds.Of(source);
        try
        {
            return kind switch
            {
                DocumentKind.Word => WordReader.Read(source, window),
                DocumentKind.Excel => ExcelReader.Read(source, window),
                DocumentKind.Slides => SlidesReader.Read(source, window),
                DocumentKind.Pdf => PdfTextReader.Read(source, window),
                DocumentKind.Text => TextFileReader.Read(source, window),
                DocumentKind.Image => throw new DocumentException("This is a picture, not text."),
                _ => throw new DocumentException(BinaryRefusal(source.Path))
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not FileNotFoundException)
        {
            throw new DocumentException($"The file could not be read: {ex.Message}");
        }
    }

    /// <summary>Отказ для двоичного файла: что это за файл и как с ним быть.</summary>
    public static string BinaryRefusal(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".doc" or ".xls" or ".ppt" =>
            $"{Path.GetFileName(path)} is an old binary Office file that cannot be read here. Ask the user to save it as .docx/.xlsx/.pptx, or hand the job to the agent.",
        _ => $"{Path.GetFileName(path)} is a binary file ({Path.GetExtension(path)}), not text. Do not retry reading it; describe it from its name and size, or hand the job to the agent."
    };

    /// <summary>
    /// Прочитанное — текстом для модели: заголовок, единицы с номерами, заметки и где продолжить.
    /// </summary>
    public static string Format(string path, DocumentContent content, DocumentWindow window)
    {
        var text = new StringBuilder();
        text.Append(path).Append(" - ").Append(DocumentKinds.Label(content.Kind));
        if (content.Summary.Length > 0)
        {
            text.Append(", ").Append(content.Summary);
        }

        text.Append('\n');
        foreach (var section in content.Sections)
        {
            if (section.Name is not null)
            {
                text.Append('\n').Append("Sheet \"").Append(section.Name).Append('"');
                if (section.Extent is not null)
                {
                    text.Append(" (").Append(section.Extent).Append(')');
                }

                text.Append(": ").Append(section.Total.ToString(CultureInfo.InvariantCulture)).Append(" rows with data\n");
            }

            AppendUnits(text, content, section);
            AppendContinuation(text, content, section, window);
        }

        foreach (var note in content.Notes)
        {
            text.Append('\n').Append(note).Append('\n');
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendUnits(StringBuilder text, DocumentContent content, DocumentSection section)
    {
        if (section.Units.Count == 0)
        {
            text.Append(section.Total == 0 ? "(empty)\n" : "(nothing in the requested window)\n");
            return;
        }

        switch (content.Kind)
        {
            case DocumentKind.Excel:
                AppendSheet(text, section.Units);
                break;
            case DocumentKind.Text:
                foreach (var unit in section.Units)
                {
                    text.Append(unit.Number.ToString(CultureInfo.InvariantCulture).PadLeft(6)).Append('\t').Append(unit.Text).Append('\n');
                }

                break;
            case DocumentKind.Pdf or DocumentKind.Slides:
                var label = content.Kind == DocumentKind.Pdf ? "Page" : "Slide";
                foreach (var unit in section.Units)
                {
                    text.Append("--- ").Append(label).Append(' ').Append(unit.Number.ToString(CultureInfo.InvariantCulture)).Append(" ---\n")
                        .Append(unit.Text).Append('\n');
                }

                break;
            default:
                foreach (var unit in section.Units)
                {
                    text.Append('[').Append(unit.Number.ToString(CultureInfo.InvariantCulture)).Append("] ").Append(unit.Text).Append('\n');
                }

                break;
        }
    }

    /// <summary>Строки листа — таблицей: номер строки и буквы только тех столбцов, где что-то есть.</summary>
    private static void AppendSheet(StringBuilder text, IReadOnlyList<DocumentUnit> rows)
    {
        var columns = rows.SelectMany(row => row.Cells ?? []).Select(cell => cell.Column).Distinct().Order().ToList();
        text.Append("| # | ").Append(string.Join(" | ", columns.Select(CellAddress.ColumnName))).Append(" |\n");
        text.Append("|---|").Append(string.Concat(columns.Select(_ => "---|"))).Append('\n');
        foreach (var row in rows)
        {
            var byColumn = (row.Cells ?? []).ToDictionary(cell => cell.Column, cell => cell.Value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal));
            text.Append("| ").Append(row.Number.ToString(CultureInfo.InvariantCulture)).Append(" | ")
                .Append(string.Join(" | ", columns.Select(column => byColumn.GetValueOrDefault(column, ""))))
                .Append(" |\n");
        }
    }

    /// <summary>Подсказка «показано a–b из N, дальше — offset=…»: без неё модель решила бы, что прочла всё.</summary>
    private static void AppendContinuation(StringBuilder text, DocumentContent content, DocumentSection section, DocumentWindow window)
    {
        if (section.Units.Count == 0)
        {
            return;
        }

        if (!section.More)
        {
            return;
        }

        var first = section.Units[0].Number;
        var last = section.Units[^1].Number;
        var sheet = section.Name is null ? "" : $"sheet=\"{section.Name}\", ";
        var shown = content.Kind == DocumentKind.Excel
            ? $"Rows {first}-{last} shown"
            : $"Shown {content.Unit}s {first}-{last} of {section.Total}";
        var next = window.Range is null ? $"offset={last + 1}" : "a range further down";
        text.Append('[').Append(shown).Append(". More: read_file with ").Append(sheet).Append(next).Append("]\n");
    }
}
