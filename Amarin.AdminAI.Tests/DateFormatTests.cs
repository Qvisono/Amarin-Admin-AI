using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Часы над ответом показывают только «HH:mm», а какого числа ответ пришёл — не говорят.
/// Полная дата живёт в подсказке, а её порядок человек выбирает сам.
/// </summary>
public sealed class DateFormatTests
{
    private static readonly DateTime Sample = new(2026, 9, 3, 18, 32, 41);

    [Theory]
    [InlineData(DateFormat.DayMonthShort, "03.09.26")]
    [InlineData(DateFormat.MonthDayShort, "09.03.26")]
    [InlineData(DateFormat.DayMonthFull, "03.09.2026")]
    [InlineData(DateFormat.MonthDayFull, "09.03.2026")]
    public void Each_choice_orders_the_date_the_way_its_caption_promises(DateFormat format, string expected) =>
        Assert.Equal(expected, ChatFormat.Date(Sample, format));

    [Fact]
    public void The_tooltip_spells_the_date_out_to_the_second() =>
        Assert.Equal("03.09.26, 18:32:41", ChatFormat.Stamp(Sample, DateFormat.DayMonthShort));

    [Fact]
    public void Journal_rows_stop_at_minutes() =>
        Assert.Equal("03.09.2026, 18:32", ChatFormat.DateTimeShort(Sample, DateFormat.DayMonthFull));

    /// <remarks>
    /// Человек выбирает подпись «DD.MM.YY» — с точками. На культуре, где разделитель даты слэш
    /// или дефис, <c>CurrentCulture</c> подменил бы их, и выбор перестал бы значить то, что
    /// написано в списке.
    /// </remarks>
    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void The_separator_survives_a_foreign_culture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("03.09.26", ChatFormat.Date(Sample, DateFormat.DayMonthShort));
            Assert.Equal("03.09.26, 18:32:41", ChatFormat.Stamp(Sample, DateFormat.DayMonthShort));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void An_unknown_value_falls_back_to_the_day_first_order() =>
        Assert.Equal("dd.MM.yy", ChatFormat.Pattern((DateFormat)42));

    /// <summary>Часы остались часами: подсказка их не заменяет.</summary>
    [Fact]
    public void The_clock_itself_still_shows_only_hours_and_minutes() =>
        Assert.Equal("18:32", ChatFormat.Clock(Sample));
}

/// <summary>
/// Подсказка висит на прозрачной обёртке, а не на самом тексте: голый <c>TextBlock</c> отвечает
/// мыши только по глифам, и на просветах между цифрами подсказка мигала бы.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ClockTooltipTests
{
    private readonly WpfFixture _wpf;

    public ClockTooltipTests(WpfFixture wpf) => _wpf = wpf;

    private static ChatDisplayMessage Assistant() => new()
    {
        Role = "assistant",
        Id = "clock-probe",
        CreatedAt = new DateTime(2026, 9, 3, 18, 32, 41),
        Text = "проба",
        ResolvedModelId = "grok-4-6",
        Status = AssistantStatus.Complete
    };

    private AssistantMessageView Build(DateFormat format) =>
        _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            return ChatMessageViews.CreateAssistant(window, Assistant(), new MessageActions(), format);
        });

    [Fact]
    public void The_chip_carries_the_full_date() =>
        Assert.Equal("03.09.26, 18:32:41", _wpf.Ui.Invoke(() => Build(DateFormat.DayMonthShort).ClockChip.ToolTip));

    [Fact]
    public void The_chosen_order_reaches_the_chip() =>
        Assert.Equal("09.03.2026, 18:32:41", _wpf.Ui.Invoke(() => Build(DateFormat.MonthDayFull).ClockChip.ToolTip));

    [Fact]
    public void The_chip_wraps_the_clock_and_answers_the_mouse_across_its_whole_box()
    {
        var view = Build(DateFormat.DayMonthShort);
        var (wrapsClock, hasBrush, delay) = _wpf.Ui.Invoke(() => (
            ReferenceEquals(view.Clock, view.ClockChip.Child),

            // Прозрачный, но не null: кисть Transparent ловит мышь, отсутствие кисти — нет.
            view.ClockChip.Background is not null,
            ToolTipService.GetInitialShowDelay(view.ClockChip)));

        Assert.True(wrapsClock);
        Assert.True(hasBrush);
        Assert.Equal(150, delay);
    }
}
