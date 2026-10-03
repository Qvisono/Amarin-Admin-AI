using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>Правила перекладки ленты при изменении размера окна — без окна.</summary>
public sealed class TranscriptReflowTests
{
    [Fact]
    public void Reading_the_middle_keeps_everything_above_the_eye_still()
    {
        // Окно растёт вниз от верха видимого: полоса идёт от верха вниз на экран.
        Assert.Equal((1000.0, 2800.0), TranscriptReflow.LiveBand(1000, 800, 1000, stuckToBottom: false));
    }

    [Fact]
    public void At_the_bottom_the_band_reaches_up()
    {
        // У низа растущее окно открывает текст сверху.
        Assert.Equal((0.0, 1800.0), TranscriptReflow.LiveBand(1000, 800, 1000, stuckToBottom: true));
    }

    [Fact]
    public void A_negative_reach_or_height_does_not_turn_the_band_inside_out()
    {
        Assert.Equal((500.0, 500.0), TranscriptReflow.LiveBand(500, -10, -5, stuckToBottom: false));
    }

    [Theory]
    [InlineData(150, 250, 1, 2)]
    [InlineData(0, 50, 0, 0)]
    [InlineData(100, 200, 1, 1)]
    [InlineData(350, 900, 3, 3)]
    [InlineData(-500, 120, 0, 1)]
    public void The_messages_touching_a_band_are_found(double from, double to, int first, int last)
    {
        double[] tops = [0, 100, 200, 300];
        Assert.Equal((first, last), TranscriptReflow.Overlapping(tops.Length, i => tops[i], from, to));
    }

    [Theory]
    [InlineData(-300, -10)]
    [InlineData(0, 0)]
    [InlineData(200, 100)]
    public void A_band_that_touches_nothing_is_empty(double from, double to)
    {
        double[] tops = [0, 100, 200, 300];
        Assert.Equal((0, -1), TranscriptReflow.Overlapping(tops.Length, i => tops[i], from, to));
    }

    [Fact]
    public void An_empty_transcript_has_nothing_to_find() =>
        Assert.Equal((0, -1), TranscriptReflow.Overlapping(0, _ => 0, 0, 100));

    [Fact]
    public void The_search_agrees_with_a_plain_scan_on_uneven_heights()
    {
        var random = new Random(30);
        var tops = new double[500];
        for (var i = 1; i < tops.Length; i++)
        {
            tops[i] = tops[i - 1] + random.Next(1, 900);
        }

        for (var probe = 0; probe < 200; probe++)
        {
            var from = random.NextDouble() * tops[^1];
            var to = from + random.Next(1, 4000);
            var touching = Enumerable.Range(0, tops.Length)
                .Where(i => tops[i] < to && (i == tops.Length - 1 || tops[i + 1] > from))
                .ToList();
            Assert.Equal((touching.First(), touching.Last()), TranscriptReflow.Overlapping(tops.Length, i => tops[i], from, to));
        }
    }

    [Fact]
    public void Release_goes_outward_from_the_band_nearest_first()
    {
        Assert.Equal([4, 1, 5, 0], TranscriptReflow.Outward(6, 2, 3));
    }

    [Fact]
    public void With_no_band_everything_is_released_in_order()
    {
        Assert.Equal([0, 1, 2], TranscriptReflow.Outward(3, 0, -1));
    }

    [Fact]
    public void A_band_at_an_edge_releases_only_the_other_side()
    {
        Assert.Equal([2, 1, 0], TranscriptReflow.Outward(4, 3, 3));
        Assert.Equal([1, 2, 3], TranscriptReflow.Outward(4, 0, 0));
    }
}
