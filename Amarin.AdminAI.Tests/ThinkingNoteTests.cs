using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The line the model writes before reaching for a tool. Asked for one or two sentences, it
/// regularly answers with a bulleted recap of what it has already done — which pushes the calls
/// and their results, the reason the block is open at all, off the screen.
/// </summary>
public sealed class ThinkingNoteTests
{
    [Fact]
    public void A_remark_that_is_already_short_is_left_exactly_as_it_was()
    {
        Assert.Equal("Хм, посмотрю, что в реестре.", ThinkingNote.Shorten("Хм, посмотрю, что в реестре."));
        Assert.Equal("Сначала гляну службы.", ThinkingNote.Shorten("  Сначала гляну службы.  "));
        Assert.Equal("", ThinkingNote.Shorten("   "));
        Assert.Equal("", ThinkingNote.Shorten(null));
    }

    [Fact]
    public void Two_sentences_of_thought_survive_and_the_essay_after_them_does_not()
    {
        var note = ThinkingNote.Shorten(
            "Хочу понять, кто держит порт 445. Начну со списка слушателей. " +
            "Затем проверю службы, потом брандмауэр, потом запланированные задачи, " +
            "потом автозагрузку, а после сведу всё это в таблицу.");

        Assert.Equal("Хочу понять, кто держит порт 445. Начну со списка слушателей.", note);
    }

    [Fact]
    public void A_recap_written_as_a_list_comes_out_as_one_line()
    {
        var note = ThinkingNote.Shorten(
            "Итоги проверки:\n- диск C: 12 ГБ свободно\n- диск D: 40 ГБ свободно\n- служба spooler остановлена");

        Assert.DoesNotContain("\n", note, StringComparison.Ordinal);
        Assert.StartsWith("Итоги проверки: - диск C", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_endless_sentence_is_cut_on_a_word_and_says_so()
    {
        var note = ThinkingNote.Shorten(string.Join(" ", Enumerable.Repeat("подробность", 120)));

        Assert.True(note.Length <= 322, $"реплика осталась длиной {note.Length}");
        Assert.EndsWith("…", note, StringComparison.Ordinal);

        // Обрыв на середине слова читается как сбой программы, а не как краткость.
        Assert.EndsWith("подробность…", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_number_is_not_mistaken_for_the_end_of_a_sentence()
    {
        var note = ThinkingNote.Shorten(
            "Ставлю 1.2.3 из кэша. Проверю, что служба поднялась. И ещё раз перечитаю журнал.");

        Assert.Equal("Ставлю 1.2.3 из кэша. Проверю, что служба поднялась.", note);
    }
}
