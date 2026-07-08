using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Amarin.UI;

internal static partial class MarkdownFormatter
{
    public static string ToSpectreMarkup(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var normalized = content.Replace("\r\n", "\n").Trim();
        var lines = normalized.Split('\n');
        var sb = new StringBuilder();

        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(FormatLine(lines[i]));
        }

        return sb.ToString();
    }

    private static string FormatLine(string line)
    {
        var heading = HeadingRegex().Match(line);
        if (heading.Success)
        {
            return $"[bold cyan]{Markup.Escape(heading.Groups[1].Value.Trim())}[/]";
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
            trimmed.StartsWith("* ", StringComparison.Ordinal) ||
            trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            var indent = line[..(line.Length - trimmed.Length)];
            var text = trimmed[2..];
            return $"{Markup.Escape(indent)}[grey]•[/] {FormatInline(text)}";
        }

        return FormatInline(line);
    }

    private static string FormatInline(string text)
    {
        var sb = new StringBuilder();
        var i = 0;

        while (i < text.Length)
        {
            if (text.AsSpan(i).StartsWith("**"))
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    sb.Append("[bold]");
                    sb.Append(Markup.Escape(text[(i + 2)..end]));
                    sb.Append("[/]");
                    i = end + 2;
                    continue;
                }
            }

            if (text.AsSpan(i).StartsWith("__"))
            {
                var end = text.IndexOf("__", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    sb.Append("[bold]");
                    sb.Append(Markup.Escape(text[(i + 2)..end]));
                    sb.Append("[/]");
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    sb.Append("[cyan]");
                    sb.Append(Markup.Escape(text[(i + 1)..end]));
                    sb.Append("[/]");
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1 && (end + 1 >= text.Length || text[end + 1] != '*'))
                {
                    sb.Append("[italic]");
                    sb.Append(Markup.Escape(text[(i + 1)..end]));
                    sb.Append("[/]");
                    i = end + 1;
                    continue;
                }
            }

            var nextSpecial = FindNextSpecial(text, i);
            sb.Append(Markup.Escape(text[i..nextSpecial]));
            i = nextSpecial;
        }

        return sb.ToString();
    }

    private static int FindNextSpecial(string text, int start)
    {
        for (var j = start; j < text.Length; j++)
        {
            if (text[j] is '*' or '`' or '_' or '[')
            {
                return j;
            }
        }

        return text.Length;
    }

    [GeneratedRegex(@"^#{1,3}\s+(.+)$")]
    private static partial Regex HeadingRegex();
}