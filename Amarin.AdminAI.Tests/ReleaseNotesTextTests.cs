using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Заметки к выпуску в окне «Что нового» и в вопросе об обновлении (1.37.0): версия не
/// повторяется заголовком, разделы — размером текста.
/// </summary>
public sealed class ReleaseNotesTextTests
{
    [Fact]
    public void The_version_heading_is_dropped_and_sections_come_down_to_text_size()
    {
        const string notes = "# 1.37.0-beta.5\n\nFifth beta.\n\n## Palette\n\n- One.\n\n### Detail\n\n- Two.";

        var compact = ReleaseNotesText.Compact(notes);

        Assert.StartsWith("Fifth beta.", compact, StringComparison.Ordinal);
        Assert.Contains("\n##### Palette\n", compact, StringComparison.Ordinal);
        Assert.Contains("\n###### Detail\n", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("1.37.0-beta.5", compact, StringComparison.Ordinal);
    }

    [Fact]
    public void Code_blocks_and_notes_without_a_version_heading_stay_as_they_are()
    {
        const string notes = "Intro.\n\n```\n## not a heading\n```\n";

        Assert.Equal(notes, ReleaseNotesText.Compact(notes));
    }
}
