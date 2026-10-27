namespace Amarin.Core;

/// <summary>Раздел заметок к выпуску: заголовок «##» и Markdown под ним.</summary>
internal sealed record ReleaseNotesSection(string Title, string Body);

/// <summary>
/// Заметки к выпуску, разобранные на вступление и разделы, — по ним «Что нового» строит колонку
/// разделов (1.37.0).
/// </summary>
/// <remarks>
/// Разделы — это заголовки «##» файла <c>.github/release-notes/v&lt;версия&gt;.md</c>: тот же файл
/// становится страницей релиза, и второй разметки ради окна в нём быть не должно. «###» остаётся
/// внутри раздела. Заметки без «##» целиком ложатся во вступление, и колонки у них нет.
/// </remarks>
internal sealed record ReleaseNotesOutline(string Lead, IReadOnlyList<ReleaseNotesSection> Sections)
{
    public static ReleaseNotesOutline Parse(string notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        // «# версия» уже показывает плашка версии в шапке — как и у ReleaseNotesText.Compact.
        var start = Array.FindIndex(lines, line => line.Trim().Length > 0);
        if (start >= 0 && lines[start].StartsWith("# ", StringComparison.Ordinal))
        {
            start++;
        }
        else
        {
            start = 0;
        }

        var lead = new List<string>();
        var sections = new List<ReleaseNotesSection>();
        string? title = null;
        var body = new List<string>();
        var inCode = false;

        for (var i = start; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }
            else if (!inCode && line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (title is not null)
                {
                    sections.Add(new ReleaseNotesSection(title, Join(body)));
                }

                title = line[3..].Trim();
                body.Clear();
                continue;
            }

            (title is null ? lead : body).Add(line);
        }

        if (title is not null)
        {
            sections.Add(new ReleaseNotesSection(title, Join(body)));
        }

        return new ReleaseNotesOutline(Join(lead), sections);
    }

    private static string Join(List<string> lines) => string.Join("\n", lines).Trim('\n', ' ', '\t');
}
