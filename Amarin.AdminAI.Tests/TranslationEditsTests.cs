using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Правка машинного перевода (I4): пометки, плейсхолдеры, новый перевод поверх правок.</summary>
public sealed class TranslationEditsTests
{
    private static readonly Dictionary<string, string> Source = new(StringComparer.Ordinal)
    {
        ["S.A"] = "Готово",
        ["S.B"] = "Потрачено {0} из {1}",
        ["S.C"] = "Закрыть"
    };

    [Fact]
    public void Edits_are_marked_and_the_mark_is_a_plain_string_old_versions_ignore()
    {
        var map = new Dictionary<string, string> { ["S.A"] = "Fertig", ["$name"] = "Deutsch" };

        var saved = TranslationEdits.Apply(map, new Dictionary<string, string> { ["S.C"] = " Schließen ", ["S.A"] = "" });

        Assert.Equal("Schließen", saved["S.C"]);
        Assert.Equal("Fertig", saved["S.A"]);
        Assert.Equal("S.C", saved[TranslationEdits.EditedKey]);
        Assert.Equal(["S.C"], TranslationEdits.Edited(saved));
        Assert.False(TranslationEdits.EditedKey.StartsWith("S.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lost_placeholder_is_a_problem_and_a_moved_one_is_not()
    {
        var problems = TranslationEdits.PlaceholderProblems(Source, new Dictionary<string, string>
        {
            ["S.B"] = "Ausgegeben {0}",
            ["S.A"] = "Fertig"
        });
        Assert.Equal(["S.B"], problems);

        Assert.Empty(TranslationEdits.PlaceholderProblems(Source, new Dictionary<string, string> { ["S.B"] = "{1}: {0} ausgegeben" }));
    }

    [Fact]
    public void A_new_translation_skips_and_keeps_what_was_edited_by_hand()
    {
        var existing = TranslationEdits.Apply(
            new Dictionary<string, string> { ["S.A"] = "Fertig", ["S.C"] = "Zu" },
            new Dictionary<string, string> { ["S.C"] = "Schließen" });

        var toTranslate = TranslationEdits.ToTranslate(Source, existing);
        Assert.DoesNotContain("S.C", toTranslate.Keys);
        Assert.Contains("S.A", toTranslate.Keys);

        var merged = TranslationEdits.Merge(existing, new Dictionary<string, string> { ["S.A"] = "Erledigt", ["S.B"] = "{0} von {1}" });
        Assert.Equal("Erledigt", merged["S.A"]);
        Assert.Equal("Schließen", merged["S.C"]);
        Assert.Equal("S.C", merged[TranslationEdits.EditedKey]);

        // Снятая пометка — строка снова доступна переводу.
        var unmarked = TranslationEdits.Unmark(merged, "S.C");
        Assert.False(unmarked.ContainsKey(TranslationEdits.EditedKey));
        Assert.Equal(Source.Count, TranslationEdits.ToTranslate(Source, unmarked).Count);
    }

    [Fact]
    public void Rows_cover_every_key_of_the_original_and_search_looks_everywhere()
    {
        var rows = TranslationEdits.Rows(Source, new Dictionary<string, string> { ["S.A"] = "Fertig", [TranslationEdits.EditedKey] = "S.A" });

        Assert.Equal(["S.A", "S.B", "S.C"], rows.Select(row => row.Key));
        Assert.True(rows[0].Edited);
        Assert.Equal("", rows[2].Translation);
        Assert.True(TranslationEdits.Matches(rows[0], "fert"));
        Assert.True(TranslationEdits.Matches(rows[1], "потрачено"));
        Assert.False(TranslationEdits.Matches(rows[2], "fert"));
    }
}
