namespace Amarin.Core;

/// <summary>
/// Заметки к выпуску в том виде, в каком их читают в программе (1.37.0): «Что нового» и вопрос
/// «Установить обновление?».
/// </summary>
/// <remarks>
/// Файл заметок начинается с «# версия» — её уже показывает плашка версии в шапке, и второй раз
/// крупным заголовком она только отнимала место. Разделы «##» общий рендер Markdown рисует как в
/// ответе модели — 17,5 точки с чертой, — и короткие списки заметок тонули между ними. Поэтому
/// разделы опускаются на уровень, который рендер пишет размером текста, а сам рендер ленты не
/// трогается.
/// </remarks>
internal static class ReleaseNotesText
{
    public static string Compact(string notes)
    {
        ArgumentNullException.ThrowIfNull(notes);
        var lines = notes.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        var first = lines.FindIndex(line => line.Trim().Length > 0);
        if (first >= 0 && lines[first].StartsWith("# ", StringComparison.Ordinal))
        {
            lines.RemoveRange(0, first + 1);
            while (lines.Count > 0 && lines[0].Trim().Length == 0)
            {
                lines.RemoveAt(0);
            }
        }

        var inCode = false;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                continue;
            }

            if (lines[i].StartsWith("### ", StringComparison.Ordinal))
            {
                lines[i] = "######" + lines[i][3..];
            }
            else if (lines[i].StartsWith("## ", StringComparison.Ordinal))
            {
                lines[i] = "#####" + lines[i][2..];
            }
        }

        return string.Join("\n", lines);
    }
}
