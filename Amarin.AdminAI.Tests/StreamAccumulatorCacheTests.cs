using System.Text;
using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// <see cref="ChatStreamAccumulator.Text"/> и <see cref="ChatStreamAccumulator.InlineReasoning"/>
/// теперь считаются один раз на состояние, а текст без маркеров размышления не разбирается вовсе.
/// </summary>
/// <remarks>
/// Прежде оба свойства разбирали накопленный ответ заново при каждом чтении, а читают их по
/// нескольку раз на каждый чанк потока. Здесь проверяется, что от кэша и от быстрого пути
/// результат не изменился ни в одном из случаев, ради которых <see cref="ReasoningSplit"/>
/// и написан.
/// </remarks>
public sealed class StreamAccumulatorCacheTests
{
    private static ChatCompletionChunk Content(string piece) =>
        JsonSerializer.Deserialize(
            """{"choices":[{"index":0,"delta":{"content":"""
            + JsonSerializer.Serialize(piece)
            + """}}]}""",
            VeniceJsonContext.Default.ChatCompletionChunk)!;

    private static ChatStreamAccumulator StreamInPieces(string whole, int pieceSize)
    {
        var accumulator = new ChatStreamAccumulator();
        for (var at = 0; at < whole.Length; at += pieceSize)
        {
            accumulator.Apply(Content(whole.Substring(at, Math.Min(pieceSize, whole.Length - at))));
        }

        return accumulator;
    }

    [Theory]
    // Обычный ответ: ни одного символа, с которого маркер начинается.
    [InlineData("Просто ответ без тегов, две строки.\nВторая строка.")]
    // Быстрый путь обязан отключиться от любого '<' — в коде он встречается постоянно.
    [InlineData("Условие пишется как `a < b`, а в XAML это &lt;Grid&gt;.")]
    [InlineData("<think>прикидываю варианты</think>Ответ: сорок два.")]
    [InlineData("<think>ещё думаю, не дописал")]
    [InlineData("рассуждение без открывающего тега</think>А вот и ответ.")]
    [InlineData("◁think▷ход мысли◁/think▷итог")]
    [InlineData("<thinking>раз</thinking>между<think>два</think>конец")]
    public void Chunked_stream_matches_a_single_split(string whole)
    {
        var expected = ReasoningSplit.Split(whole);

        foreach (var pieceSize in new[] { 1, 3, 7, 64 })
        {
            var accumulator = StreamInPieces(whole, pieceSize);

            Assert.Equal(expected.Answer, accumulator.Text);
            Assert.Equal(expected.Reasoning, accumulator.InlineReasoning);
        }
    }

    [Fact]
    public void Reading_twice_without_new_text_returns_the_same_instance()
    {
        // Ради этого всё и делалось: до кэша каждое чтение собирало новую строку из всего
        // накопленного ответа, а читают их по нескольку раз на каждый чанк.
        var accumulator = StreamInPieces("<think>ход мысли</think>Ответ на месте.", 5);

        Assert.Same(accumulator.Text, accumulator.Text);
        Assert.Same(accumulator.InlineReasoning, accumulator.InlineReasoning);
    }

    [Fact]
    public void A_new_chunk_invalidates_the_remembered_split()
    {
        var accumulator = new ChatStreamAccumulator();
        accumulator.Apply(Content("Начало"));
        Assert.Equal("Начало", accumulator.Text);

        accumulator.Apply(Content(" и продолжение"));
        Assert.Equal("Начало и продолжение", accumulator.Text);
    }

    [Fact]
    public void Text_growing_only_inside_a_think_block_is_not_reported_as_visible()
    {
        var accumulator = new ChatStreamAccumulator();

        // Apply отвечает на вопрос «вырос ли видимый ответ», и размышление обязано отвечать «нет»:
        // иначе пузырь перерисовывался бы на каждый чанк хода мысли.
        Assert.False(accumulator.Apply(Content("<think>")));
        Assert.False(accumulator.Apply(Content("всё ещё думаю")));
        Assert.Equal("", accumulator.Text);

        Assert.True(accumulator.Apply(Content("</think>Готово.")));
        Assert.Equal("Готово.", accumulator.Text);
    }

    [Fact]
    public void A_long_answer_without_markers_stays_whole()
    {
        // Тот самый случай, ради которого быстрый путь и нужен: длинный ответ обычным текстом.
        var builder = new StringBuilder();
        for (var i = 0; i < 2000; i++)
        {
            builder.Append("слово").Append(i).Append(' ');
        }

        var whole = builder.ToString();
        var accumulator = StreamInPieces(whole, 9);

        Assert.Equal(whole, accumulator.Text);
        Assert.Equal("", accumulator.InlineReasoning);
    }
}
