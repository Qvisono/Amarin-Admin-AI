using System.Text;
using System.Text.RegularExpressions;

namespace Amarin.UI;

internal sealed record ParsedAssistantResponse(
    string Brief,
    IReadOnlyList<string> DoneItems,
    string Technical,
    bool HasStructuredSections)
{
    public bool HasDoneItems => DoneItems.Count > 0;
    public bool HasTechnical => !string.IsNullOrWhiteSpace(Technical);
}

internal static partial class AssistantResponseParser
{
    private static readonly string[] DoneSectionLabels =
    [
        "Что сделано", "Действия", "Actions", "Done"
    ];

    private static readonly string[] ObservationSectionLabels =
    [
        "На изображении", "На скриншоте", "На экране", "Вижу", "Детали"
    ];

    private static readonly string[] TechnicalSectionLabels =
    [
        "Технические детали", "Подробности", "Details", "Technical"
    ];

    private static readonly string[] BriefSectionLabels =
    [
        "Кратко", "Итог", "Вывод", "Summary", "Brief"
    ];

    public static bool IsDisplayEmpty(ParsedAssistantResponse parsed) =>
        string.IsNullOrWhiteSpace(parsed.Brief) &&
        !parsed.HasDoneItems &&
        !parsed.HasTechnical;

    public static ParsedAssistantResponse Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ParsedAssistantResponse(string.Empty, [], string.Empty, false);
        }

        var normalized = content.Replace("\r\n", "\n").Trim();
        if (!HasExplicitSections(normalized))
        {
            return new ParsedAssistantResponse(normalized, [], string.Empty, false);
        }

        var sections = SplitIntoSections(normalized);
        if (sections.Count == 0)
        {
            return new ParsedAssistantResponse(normalized, [], string.Empty, false);
        }

        var briefParts = new List<string>();
        var doneItems = new List<string>();
        var observationParts = new List<string>();
        var technicalParts = new List<string>();

        foreach (var (label, body) in sections)
        {
            if (MatchesAny(label, BriefSectionLabels) ||
                label.Equals("Вступление", StringComparison.OrdinalIgnoreCase))
            {
                briefParts.Add(body);
                continue;
            }

            if (MatchesAny(label, DoneSectionLabels))
            {
                doneItems.AddRange(ExtractListItems(body));
                if (doneItems.Count == 0 && !string.IsNullOrWhiteSpace(body))
                {
                    briefParts.Add(body);
                }

                continue;
            }

            if (MatchesAny(label, ObservationSectionLabels))
            {
                observationParts.Add(body);
                continue;
            }

            if (MatchesAny(label, TechnicalSectionLabels))
            {
                technicalParts.Add(body);
            }
        }

        var brief = string.Join("\n\n", briefParts.Concat(observationParts).Where(p => !string.IsNullOrWhiteSpace(p)))
            .Trim();

        var technical = string.Join("\n\n", technicalParts).Trim();

        var hasStructure = !string.IsNullOrWhiteSpace(brief) ||
                           doneItems.Count > 0 ||
                           !string.IsNullOrWhiteSpace(technical);

        if (!hasStructure)
        {
            return new ParsedAssistantResponse(normalized, [], string.Empty, false);
        }

        return new ParsedAssistantResponse(brief, doneItems, technical, true);
    }

    private static bool HasExplicitSections(string content)
    {
        foreach (var line in content.Split('\n'))
        {
            if (TryParseSectionHeader(line, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static List<(string Label, string Body)> SplitIntoSections(string content)
    {
        var sections = new List<(string Label, string Body)>();
        string? currentLabel = null;
        var body = new StringBuilder();
        var preamble = new StringBuilder();

        foreach (var line in content.Split('\n'))
        {
            if (TryParseSectionHeader(line, out var label))
            {
                if (currentLabel is not null)
                {
                    sections.Add((currentLabel, body.ToString().Trim()));
                    body.Clear();
                }
                else if (preamble.Length > 0)
                {
                    sections.Add(("Вступление", preamble.ToString().Trim()));
                    preamble.Clear();
                }

                currentLabel = label;
                continue;
            }

            if (currentLabel is not null)
            {
                if (body.Length > 0)
                {
                    body.Append('\n');
                }

                body.Append(line);
                continue;
            }

            if (preamble.Length > 0)
            {
                preamble.Append('\n');
            }

            preamble.Append(line);
        }

        if (currentLabel is not null)
        {
            sections.Add((currentLabel, body.ToString().Trim()));
        }
        else if (preamble.Length > 0)
        {
            sections.Add(("Вступление", preamble.ToString().Trim()));
        }

        return sections;
    }

    private static bool TryParseSectionHeader(string line, out string label)
    {
        label = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var withoutMd = trimmed.Trim('*', '#', ' ');
        var colon = withoutMd.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        label = withoutMd[..colon].Trim();
        if (label.Length == 0 || label.Length > 40)
        {
            return false;
        }

        return MatchesAny(label, BriefSectionLabels) ||
               MatchesAny(label, DoneSectionLabels) ||
               MatchesAny(label, ObservationSectionLabels) ||
               MatchesAny(label, TechnicalSectionLabels);
    }

    private static List<string> ExtractListItems(string body)
    {
        var items = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (BulletRegex().IsMatch(trimmed) || NumberedRegex().IsMatch(trimmed))
            {
                items.Add(CleanListItem(trimmed));
            }
        }

        return items;
    }

    private static bool MatchesAny(string label, IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (label.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CleanListItem(string line)
    {
        var cleaned = BulletRegex().Replace(line, string.Empty);
        cleaned = NumberedRegex().Replace(cleaned, string.Empty);
        return cleaned.Trim();
    }

    [GeneratedRegex(@"^[-*•]\s+")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^\d+[.)]\s+")]
    private static partial Regex NumberedRegex();
}