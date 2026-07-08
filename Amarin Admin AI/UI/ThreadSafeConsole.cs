using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Amarin.UI;

internal static partial class ThreadSafeConsole
{
    private static readonly object Gate = new();

    public static void WriteLine(string? text = null)
    {
        lock (Gate)
        {
            ConsoleInputRestore.Restore();
            if (text is null)
            {
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine(text);
            }

            Console.Out.Flush();
        }
    }

    public static void Write(string text)
    {
        lock (Gate)
        {
            ConsoleInputRestore.Restore();
            Console.Write(text);
            Console.Out.Flush();
        }
    }

    public static void WriteColoredLine(string text, ConsoleColor color)
    {
        lock (Gate)
        {
            ConsoleInputRestore.Restore();
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = previous;
            Console.Out.Flush();
        }
    }

    public static void WriteAssistantPanel(IRenderable body)
    {
        lock (Gate)
        {
            ConsoleInputRestore.Restore();
            AnsiConsole.WriteLine();
            try
            {
                AnsiConsole.Write(UiTheme.CreatePanel("[cyan]Amarin[/]", body, UiTheme.Border, UiTheme.Primary));
            }
            catch
            {
                if (body is Text textRenderable)
                {
                    Console.WriteLine();
                    Console.Write(textRenderable.ToString() ?? string.Empty);
                }
                else
                {
                    AnsiConsole.Write(UiTheme.CreatePanel(
                        "[cyan]Amarin[/]",
                        new Text("Не удалось отобразить ответ."),
                        UiTheme.Border,
                        UiTheme.Primary));
                }
            }

            AnsiConsole.WriteLine();
            Console.Out.Flush();
        }
    }

    public static void WriteFramed(string title, string body)
    {
        lock (Gate)
        {
            ConsoleInputRestore.Restore();

            var originalLines = body.Replace("\r\n", "\n").Split('\n');

            // Определяем оптимальную ширину рамки на основе самой длинной строки, но не более 100
            var maxVisibleOriginal = originalLines.DefaultIfEmpty(string.Empty).Max(l => VisibleLength(l));
            var minWidth = Math.Max(40, $" {title} ".Length + 2);
            var totalInnerWidth = Math.Max(minWidth, Math.Min(100, maxVisibleOriginal + 1));
            var usableWidth = totalInnerWidth - 1; // Учитываем отступ в 1 пробел слева

            // Разбиваем слишком длинные строки на подстроки (перенос по словам)
            var wrappedLines = new List<string>();
            foreach (var line in originalLines)
            {
                wrappedLines.AddRange(WrapLine(line, usableWidth));
            }

            WriteBorderTop(title, totalInnerWidth);
            foreach (var line in wrappedLines)
            {
                WriteBorderLine(line, totalInnerWidth);
            }

            WriteBorderBottom(totalInnerWidth);
            Console.WriteLine();
            Console.Out.Flush();
        }
    }

    public static void WriteFramedPlain(string title, string body)
    {
        lock (Gate)
        {
            ConsoleInputRestore.Restore();

            var originalLines = body.Replace("\r\n", "\n").Split('\n');

            // Для Plain-версии лимит ширины чуть больше — 120
            var maxLenOriginal = originalLines.DefaultIfEmpty(string.Empty).Max(l => l.Length);
            var minWidth = Math.Max(40, $" {title} ".Length + 2);
            var totalInnerWidth = Math.Max(minWidth, Math.Min(120, maxLenOriginal + 1));
            var usableWidth = totalInnerWidth - 1;

            var wrappedLines = new List<string>();
            foreach (var line in originalLines)
            {
                wrappedLines.AddRange(WrapLinePlain(line, usableWidth));
            }

            WriteBorderTop(title, totalInnerWidth);
            foreach (var line in wrappedLines)
            {
                WriteBorderLinePlain(line, totalInnerWidth);
            }

            WriteBorderBottom(totalInnerWidth);
            Console.WriteLine();
            Console.Out.Flush();
        }
    }

    private static void WriteBorderTop(string title, int totalInnerWidth)
    {
        var label = $" {title} ";
        var dashCount = Math.Max(0, totalInnerWidth - label.Length);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write('╭');
        Console.Write(label);
        Console.Write(new string('─', dashCount));
        Console.WriteLine('╮');
        Console.ResetColor();
    }

    private static void WriteBorderLine(string line, int totalInnerWidth)
    {
        var usableWidth = totalInnerWidth - 1;
        var visible = VisibleLength(line);

        // Страховочное усечение (теперь практически никогда не сработает, так как текст уже разбит)
        if (visible > usableWidth)
        {
            line = line[..Math.Max(0, usableWidth - 1)] + "…";
            visible = VisibleLength(line);
        }

        var rendered = RenderSimpleMarkdown(line);
        var padding = Math.Max(0, usableWidth - visible);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write('│');
        Console.ResetColor();
        Console.Write(' ');
        Console.Write(rendered);
        Console.Write(new string(' ', padding));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine('│');
        Console.ResetColor();
    }

    private static void WriteBorderLinePlain(string line, int totalInnerWidth)
    {
        var usableWidth = totalInnerWidth - 1;
        var display = line.Length <= usableWidth ? line : line[..Math.Max(0, usableWidth - 1)] + "…";
        var padding = Math.Max(0, usableWidth - display.Length);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write('│');
        Console.ResetColor();
        Console.Write(' ');
        Console.Write(display);
        Console.Write(new string(' ', padding));
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine('│');
        Console.ResetColor();
    }

    private static void WriteBorderBottom(int totalInnerWidth)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write('╰');
        Console.Write(new string('─', totalInnerWidth));
        Console.WriteLine('╯');
        Console.ResetColor();
    }

    // Умный перенос строк с учетом "видимой" длины Markdown-тегов
    private static List<string> WrapLine(string line, int usableWidth)
    {
        var lines = new List<string>();
        if (VisibleLength(line) <= usableWidth)
        {
            lines.Add(line);
            return lines;
        }

        var words = line.Split(' ');
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word))
            {
                currentLine.Append(' ');
                continue;
            }

            var nextSegment = currentLine.Length == 0 ? word : " " + word;

            if (VisibleLength(currentLine.ToString() + nextSegment) <= usableWidth)
            {
                currentLine.Append(nextSegment);
            }
            else
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear().Append(word);
                }
                else
                {
                    // Если само слово длиннее всей строки (например, длинная ссылка или путь)
                    var remainingWord = word;
                    while (VisibleLength(remainingWord) > usableWidth)
                    {
                        int fitCount = 0;
                        for (int len = 1; len <= remainingWord.Length; len++)
                        {
                            if (VisibleLength(remainingWord[..len]) <= usableWidth)
                                fitCount = len;
                            else
                                break;
                        }

                        if (fitCount == 0) fitCount = 1;

                        lines.Add(remainingWord[..fitCount]);
                        remainingWord = remainingWord[fitCount..];
                    }
                    currentLine.Append(remainingWord);
                }
            }
        }

        if (currentLine.Length > 0)
        {
            lines.Add(currentLine.ToString());
        }

        return lines;
    }

    // Обычный перенос строк по словам для простого текста
    private static List<string> WrapLinePlain(string line, int usableWidth)
    {
        var lines = new List<string>();
        if (line.Length <= usableWidth)
        {
            lines.Add(line);
            return lines;
        }

        var words = line.Split(' ');
        var currentLine = new StringBuilder();

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word))
            {
                currentLine.Append(' ');
                continue;
            }

            var nextSegment = currentLine.Length == 0 ? word : " " + word;

            if (currentLine.Length + nextSegment.Length <= usableWidth)
            {
                currentLine.Append(nextSegment);
            }
            else
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear().Append(word);
                }
                else
                {
                    var remainingWord = word;
                    while (remainingWord.Length > usableWidth)
                    {
                        lines.Add(remainingWord[..usableWidth]);
                        remainingWord = remainingWord[usableWidth..];
                    }
                    currentLine.Append(remainingWord);
                }
            }
        }

        if (currentLine.Length > 0)
        {
            lines.Add(currentLine.ToString());
        }

        return lines;
    }

    private static string RenderSimpleMarkdown(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var i = 0;
        while (i < line.Length)
        {
            if (line.AsSpan(i).StartsWith("**"))
            {
                var end = line.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    sb.Append("\x1b[1m");
                    sb.Append(line.AsSpan(i + 2, end - i - 2));
                    sb.Append("\x1b[0m");
                    i = end + 2;
                    continue;
                }
            }

            if (line[i] == '`')
            {
                var end = line.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    sb.Append("\x1b[36m");
                    sb.Append(line.AsSpan(i + 1, end - i - 1));
                    sb.Append("\x1b[0m");
                    i = end + 1;
                    continue;
                }
            }

            sb.Append(line[i]);
            i++;
        }

        return sb.ToString();
    }

    private static int VisibleLength(string line)
    {
        var stripped = BoldRegex().Replace(line, "$1");
        stripped = CodeRegex().Replace(stripped, "$1");
        return stripped.Length;
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"`(.+?)`")]
    private static partial Regex CodeRegex();
}