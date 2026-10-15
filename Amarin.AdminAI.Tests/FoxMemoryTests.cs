using Amarin.Fox;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Память Розы для общения (вторая волна, M8): сколько разговоров и заметок он помнит, что
/// вытесняет, чего не запоминает никогда и как всё стирается.
/// </summary>
public sealed class FoxMemoryTests
{
    private static readonly DateTime Noon = new(2026, 10, 15, 12, 0, 0);

    [Fact]
    public void Only_the_last_talks_are_kept_and_long_ones_are_cut()
    {
        var state = new FoxState();
        for (var i = 0; i < FoxMemory.MaxExchanges + 5; i++)
        {
            FoxMemory.AddExchange(state, "вопрос " + i, "ответ " + i, Noon.AddMinutes(i));
        }

        FoxMemory.AddExchange(state, new string('я', 1000), new string('р', 1000), Noon.AddHours(1));

        Assert.Equal(FoxMemory.MaxExchanges, state.Conversation.Count);
        Assert.Equal("вопрос 6", state.Conversation[0].Human);
        Assert.Equal(FoxMemory.HumanLimit, state.Conversation[^1].Human.Length);
        Assert.Equal(FoxMemory.RosaLimit, state.Conversation[^1].Rosa.Length);
    }

    [Fact]
    public void A_talk_without_an_answer_is_not_remembered()
    {
        var state = new FoxState();

        FoxMemory.AddExchange(state, "привет", "  ", Noon);

        Assert.Empty(state.Conversation);
    }

    /// <summary>То же другими знаками не дублируется, а заметка шире прежней заменяет её.</summary>
    [Fact]
    public void A_note_is_kept_once_and_a_wider_one_replaces_it()
    {
        var state = new FoxState();

        Assert.True(FoxMemory.Remember(state, "Любит зелёный чай", Noon));
        Assert.False(FoxMemory.Remember(state, "любит зелёный  чай!", Noon));
        Assert.False(FoxMemory.Remember(state, "зелёный чай", Noon));
        Assert.True(FoxMemory.Remember(state, "Любит зелёный чай по утрам", Noon));

        Assert.Equal(["Любит зелёный чай по утрам"], state.Notes.Select(note => note.Text));
    }

    [Fact]
    public void The_oldest_notes_give_way()
    {
        var state = new FoxState();
        for (var i = 0; i < FoxMemory.MaxNotes + 3; i++)
        {
            Assert.True(FoxMemory.Remember(state, "Заметка номер " + (char)('А' + i), Noon.AddMinutes(i)));
        }

        Assert.Equal(FoxMemory.MaxNotes, state.Notes.Count);
        Assert.Equal("Заметка номер Г", state.Notes[0].Text);
    }

    /// <summary>
    /// Секрет не ложится на диск ни при каком ответе модели: ключ, токен, номер карты или
    /// телефона, слово о пароле. Короткая обычная заметка с числом — ложится.
    /// </summary>
    [Theory]
    [InlineData("Пароль от почты qwerty", false)]
    [InlineData("password is hunter2", false)]
    [InlineData("Карта 4276 3800 1234 5678", false)]
    [InlineData("Телефон +7 912 345-67-89", false)]
    [InlineData("Ключ sk-or-v1-a8f3c9d2e7b14f6a", false)]
    [InlineData("ПИН-код 1234", false)]
    [InlineData("Ему 30 лет, живёт в Казани", true)]
    [InlineData("Родился 2026-10-15", true)]
    [InlineData("ok", false)]
    public void Secrets_and_scraps_are_never_kept(string note, bool kept)
    {
        var state = new FoxState();

        Assert.Equal(kept, FoxMemory.Remember(state, note, Noon));
        Assert.Equal(kept ? 1 : 0, state.Notes.Count);
    }

    [Fact]
    public void Forgetting_erases_notes_and_talks()
    {
        var state = new FoxState();
        FoxMemory.AddExchange(state, "привет", "привет!", Noon);
        FoxMemory.Remember(state, "Работает по ночам", Noon);

        FoxMemory.Forget(state);

        Assert.Empty(state.Notes);
        Assert.Empty(state.Conversation);
    }
}
