using System.Text;

namespace Amarin.Core;

/// <summary>
/// Приводит реплику модели, сказанную перед вызовом инструмента, к тому размеру, ради которого
/// её и показывают.
/// </summary>
/// <remarks>
/// Просьба «одна-две фразы» доходит до модели через раз: вместо мысли она нередко пишет конспект
/// уже сделанного — со списками и заголовками. В раскрытом блоке инструментов это вытесняет то,
/// ради чего его открыли, — сами вызовы и их результаты.
/// <para>
/// Режется только показ. Полный текст модели уходит в стенограмму как обычное сообщение
/// ассистента, так что ни контекст, ни следующий ход ничего не теряют.
/// </para>
/// </remarks>
internal static class ThinkingNote
{
    /// <summary>Дальше это уже не реплика, а абзац.</summary>
    private const int MaxChars = 320;

    private const int MaxSentences = 2;

    public static string Shorten(string? text)
    {
        var note = Collapse(text ?? "");
        if (note.Length == 0)
        {
            return "";
        }

        note = FirstSentences(note);
        return note.Length <= MaxChars ? note : Clip(note);
    }

    /// <summary>Переносы и отступы — в пробелы: список из реплики всё равно превращается в строку.</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var symbol in text)
        {
            if (char.IsWhiteSpace(symbol))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(symbol);
        }

        return builder.ToString();
    }

    private static string FirstSentences(string note)
    {
        var sentences = 0;
        for (var i = 0; i < note.Length; i++)
        {
            if (note[i] is not ('.' or '!' or '?' or '…'))
            {
                continue;
            }

            // «1.5 ГБ» и «C:\Users\...» точкой предложение не заканчивают.
            if (i + 1 < note.Length && note[i + 1] != ' ')
            {
                continue;
            }

            if (++sentences == MaxSentences)
            {
                return note[..(i + 1)];
            }
        }

        return note;
    }

    /// <summary>Обрезает по границе слова — половина слова читается как сбой, а не как краткость.</summary>
    private static string Clip(string note)
    {
        var cut = note.LastIndexOf(' ', MaxChars);
        return (cut > MaxChars / 2 ? note[..cut] : note[..MaxChars]).TrimEnd() + "…";
    }
}
