using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Сколько места занять в ленте под ещё не построенное сообщение.
/// </summary>
/// <remarks>
/// <para>
/// Нужна только при первом показе чата: потом лента помнит настоящие высоты. Чем точнее
/// прикидка, тем меньше всё сдвигается, пока фоновая дорисовка строит сообщения выше
/// видимого, и тем ровнее ведёт себя полоса прокрутки. Прежняя прикидка считала строки по
/// длине текста и ошибалась до двух раз: блок кода, таблица и абзацы стоят разной высоты.
/// </para>
/// <para>
/// Постоянные замерены на построенных сообщениях при заводском шрифте; строки текста и кода
/// растут вместе с выбранным размером (<see cref="ChatFonts"/>), обвязка — нет.
/// </para>
/// </remarks>
internal static class MessageHeightEstimate
{
    /// <summary>У ответа: строка модели над текстом, ряд кнопок под ним и поля.</summary>
    private const double AssistantChrome = 81;

    /// <summary>У вопроса: поля пузыря и ряд кнопок под ним.</summary>
    private const double UserChrome = 66;

    private const double ParagraphGap = 8;
    private const double CodeChrome = 59;
    private const double TableChrome = 22;
    private const double TableRow = 30;
    private const double HeadingExtra = 12;
    private const double DisplayMath = 48;
    private const double ToolsHeader = 36;
    private const double FileStrip = 60;
    private const double UserImages = 120;
    private const double InlineImage = 260;

    /// <summary>Колонка логотипа модели слева от ответа.</summary>
    private const double LogoColumn = 40;

    /// <summary>Пузырь вопроса не шире этого, сколько бы места ни было.</summary>
    private const double UserBubbleInner = 530;

    /// <summary>
    /// Сколько знаков строки теряет перенос по словам: слово, не влезшее в конец строки,
    /// целиком уходит на следующую.
    /// </summary>
    private const int WrapLoss = 4;

    public static double For(ChatDisplayMessage message, double transcriptWidth)
    {
        var width = transcriptWidth > 100 ? transcriptWidth : 900;
        var text = message.Text ?? "";
        var quotes = message.Quotes.Count * QuoteViews.CardHeightEstimate;
        double height;
        if (message.Role == "user")
        {
            var bubble = Math.Min(UserBubbleInner, Math.Max(120, width - 120));
            height = UserChrome + Body(text, bubble, ChatFonts.UserLine) + (message.Images.Count > 0 ? UserImages : 0);
        }
        else
        {
            height = AssistantChrome + Body(text, width - LogoColumn, ChatFonts.AssistantLine) +
                     (message.ToolRounds.Count > 0 ? ToolsHeader : 0);
        }

        if (message.Files.Count > 0)
        {
            height += FileStrip;
        }

        return Math.Clamp(height + quotes, 40, 6000);
    }

    /// <summary>Высота разметки: абзацы, заголовки, блоки кода, таблицы и формулы.</summary>
    internal static double Body(string text, double width, double lineHeight)
    {
        var perLine = Math.Max(10, (int)(width / (ChatFonts.BodySize * 0.5)) - WrapLoss);
        var height = 0.0;
        var blocks = 0;
        var codeLines = -1;
        var tableRows = -1;
        var paragraphOpen = false;

        void CloseParagraph() => paragraphOpen = false;

        void StartBlock()
        {
            if (blocks++ > 0)
            {
                height += ParagraphGap;
            }
        }

        void CloseTable()
        {
            if (tableRows >= 0)
            {
                height += TableChrome + (tableRows * TableRow);
                tableRows = -1;
            }
        }

        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (codeLines >= 0)
            {
                if (line.StartsWith("```"))
                {
                    height += CodeChrome + (codeLines * ChatFonts.CodeLine);
                    codeLines = -1;
                }
                else
                {
                    codeLines++;
                }

                continue;
            }

            if (line.StartsWith("```"))
            {
                CloseTable();
                CloseParagraph();
                StartBlock();
                codeLines = 0;
                continue;
            }

            if (line.StartsWith("|"))
            {
                if (tableRows < 0)
                {
                    CloseParagraph();
                    StartBlock();
                    tableRows = 0;
                }

                // Строка-разделитель «|---|» места не занимает.
                if (line.Trim("|-: ").Length > 0)
                {
                    tableRows++;
                }

                continue;
            }

            CloseTable();
            if (line.IsEmpty)
            {
                CloseParagraph();
                continue;
            }

            if (line.StartsWith("$$") && line.EndsWith("$$") && line.Length > 4)
            {
                CloseParagraph();
                StartBlock();
                height += DisplayMath;
                continue;
            }

            // Заголовок и пункт списка — свой блок; строки одного абзаца идут подряд.
            var heading = line.StartsWith("#");
            var item = line.StartsWith("- ") || line.StartsWith("* ") || (line.Length > 2 && char.IsAsciiDigit(line[0]) && line.IndexOf(". ") is > 0 and < 4);
            if (!paragraphOpen || heading || item)
            {
                StartBlock();
                paragraphOpen = !heading;
            }

            height += lineHeight * Math.Max(1, (line.Length + perLine - 1) / perLine);
            if (heading)
            {
                height += HeadingExtra;
            }

            height += InlineImage * Count(line, "![");
        }

        // Незакрытый блок кода (ответ оборвался) и таблица в самом конце.
        if (codeLines >= 0)
        {
            height += CodeChrome + (codeLines * ChatFonts.CodeLine);
        }

        CloseTable();
        return Math.Max(lineHeight, height);
    }

    private static int Count(ReadOnlySpan<char> line, string needle)
    {
        var count = 0;
        for (var at = line.IndexOf(needle); at >= 0; at = line.IndexOf(needle))
        {
            count++;
            line = line[(at + needle.Length)..];
        }

        return count;
    }
}
