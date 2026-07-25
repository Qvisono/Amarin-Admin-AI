namespace Amarin.UI;

/// <summary>
/// Thin facade over <see cref="MarkdownConsoleRenderer"/> for call sites that need
/// Spectre markup strings (short prompts, labels). Prefer
/// <see cref="MarkdownConsoleRenderer"/> for full document rendering.
/// </summary>
internal static class MarkdownFormatter
{
    public static string ToSpectreMarkup(string content) =>
        MarkdownConsoleRenderer.ToSpectreMarkup(content);
}
