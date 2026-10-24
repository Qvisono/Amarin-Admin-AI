using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Поиск палитры команд (1.37.0): что находится, в каком порядке и что выделяется.
/// </summary>
public sealed class PaletteSearchTests
{
    private static readonly PaletteItem[] Items =
    [
        new(PaletteKind.Chat, "c1", "Шифры в архиве 7-Zip", "вчера"),
        new(PaletteKind.Action, "NewChat", "Новый чат", "Открыть пустой чат"),
        new(PaletteKind.Setting, "s1", "Шифровать чаты на диске", "Безопасность › Приватность"),
        new(PaletteKind.Setting, "s2", "Тема оформления по расписанию", "Оформление"),
        new(PaletteKind.Setting, "s3", "Тема", "Оформление"),
        new(PaletteKind.Command, "readonly", "/readonly", "Только чтение"),
        new(PaletteKind.Chat, "c2", "Как зашифровать диск D: BitLocker", "12 окт")
    ];

    [Fact]
    public void Groups_keep_their_order_whatever_scores_their_items_get()
    {
        var found = PaletteSearch.Find(Items, "шифр");

        Assert.Equal(
            [PaletteKind.Setting, PaletteKind.Chat, PaletteKind.Chat],
            found.Select(match => match.Item.Kind));
    }

    [Fact]
    public void A_title_starting_with_the_query_beats_a_word_inside_it()
    {
        var chats = PaletteSearch.Find(Items, "шифр").Where(match => match.Item.Kind == PaletteKind.Chat).ToList();

        // «Шифры…» начинается с запроса, «…зашифровать…» содержит его внутри слова.
        Assert.Equal("c1", chats[0].Item.Id);
        Assert.Equal("c2", chats[1].Item.Id);
    }

    [Fact]
    public void A_shorter_title_wins_among_equal_matches()
    {
        var settings = PaletteSearch.Find(Items, "тема").Select(match => match.Item.Id).ToList();

        Assert.Equal(["s3", "s2"], settings);
    }

    [Fact]
    public void Every_word_must_be_found_and_the_path_counts()
    {
        Assert.Single(PaletteSearch.Find(Items, "чаты приватность"));
        Assert.Empty(PaletteSearch.Find(Items, "чаты расписание"));
    }

    [Fact]
    public void Case_and_yo_do_not_matter_and_the_hit_is_where_the_word_starts()
    {
        var match = Assert.Single(PaletteSearch.Find(
            [new PaletteItem(PaletteKind.Setting, "x", "Ещё раз: Ёлка", "")], "ЕЛК"));

        Assert.Equal(9, match.Start);
        Assert.Equal(3, match.Length);
    }

    [Fact]
    public void An_empty_query_offers_actions_and_recent_chats_only()
    {
        var found = PaletteSearch.Find(Items, "  ");

        Assert.Equal(
            [PaletteKind.Action, PaletteKind.Chat, PaletteKind.Chat],
            found.Select(match => match.Item.Kind));
    }

    [Fact]
    public void A_group_is_cut_to_its_limit()
    {
        var many = Enumerable.Range(0, 20)
            .Select(i => new PaletteItem(PaletteKind.Command, "c" + i, "/cmd" + i))
            .ToList();

        Assert.Equal(PaletteSearch.LimitOf(PaletteKind.Command), PaletteSearch.Find(many, "cmd").Count);
    }
}
