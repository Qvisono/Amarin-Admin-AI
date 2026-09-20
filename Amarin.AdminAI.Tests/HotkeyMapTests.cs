using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Запись сочетания клавиш: что считается сочетанием и как оно приводится к одному виду.
/// </summary>
/// <remarks>
/// Без окна — как <c>ChatZoomMathTests</c>: правила о записи живут в <see cref="HotkeyMap"/>
/// именно затем, чтобы их можно было проверить, ничего не показывая на экране.
/// </remarks>
public sealed class HotkeyMapTests
{
    [Fact]
    public void A_shortcut_without_a_modifier_is_refused()
    {
        // Голая буква перехватывалась бы прямо во время набора сообщения, и печатать стало бы
        // нечем: обработчик окна видит клавиши раньше поля ввода.
        Assert.False(HotkeyMap.TryParse("F", out _));
        Assert.False(HotkeyMap.TryParse("Enter", out _));
        Assert.False(HotkeyMap.TryParse("", out _));
        Assert.False(HotkeyMap.TryParse(null, out _));
    }

    [Fact]
    public void A_modifier_alone_is_not_a_shortcut()
    {
        Assert.False(HotkeyMap.TryParse("Ctrl", out _));
        Assert.False(HotkeyMap.TryParse("Ctrl+Shift", out _));
    }

    [Fact]
    public void Shift_and_Win_do_not_count_as_the_modifier()
    {
        // Shift+N — это просто заглавная «N»; отняв её у клавиатуры, мы отняли бы у человека
        // букву. Сочетания с Win забирает себе Windows, и назначенное на них молча не
        // сработало бы.
        Assert.False(HotkeyMap.TryParse("Shift+N", out _));
        Assert.False(HotkeyMap.TryParse("Win+D", out _));
        Assert.False(HotkeyMap.TryParse("Win+Shift+S", out _));

        // А вместе с Ctrl или Alt — вполне.
        Assert.True(HotkeyMap.TryParse("Ctrl+Shift+N", out _));
        Assert.True(HotkeyMap.TryParse("Win+Alt+Delete", out _));
    }

    [Fact]
    public void The_order_of_modifiers_does_not_make_a_second_shortcut()
    {
        // Иначе Shift+Ctrl+N и Ctrl+Shift+N лежали бы в настройках двумя разными записями,
        // и одна из них молча не срабатывала бы.
        Assert.True(HotkeyMap.TryParse("Shift+Ctrl+N", out var first));
        Assert.True(HotkeyMap.TryParse("Ctrl+Shift+N", out var second));
        Assert.Equal("Ctrl+Shift+N", first);
        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("control+f", "Ctrl+F")]
    [InlineData("  Ctrl + F  ", "Ctrl+F")]
    [InlineData("Win+Alt+Delete", "Alt+Win+Delete")]
    public void A_sloppy_record_is_read_and_straightened(string written, string expected)
    {
        Assert.True(HotkeyMap.TryParse(written, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Two_ordinary_keys_are_not_a_shortcut()
    {
        Assert.False(HotkeyMap.TryParse("Ctrl+F+G", out _));
    }

    [Fact]
    public void An_unassigned_action_falls_back_to_the_factory_shortcut()
    {
        // Отсутствующий ключ значит «как с завода»: ровно поэтому settings.json прежних
        // версий читается без миграции.
        Assert.Equal("Ctrl+F", HotkeyMap.Gesture(null, HotkeyMap.NewChat));
        Assert.Equal("Ctrl+F", HotkeyMap.Gesture(new Dictionary<string, string>(), HotkeyMap.NewChat));
        Assert.True(HotkeyMap.IsDefault(null, HotkeyMap.NewChat));
    }

    [Fact]
    public void A_broken_record_in_the_settings_falls_back_too()
    {
        // Файл правят руками, и «Ctrl» без клавиши там появиться может. Оставлять действие
        // вовсе без сочетания из-за опечатки — хуже, чем вернуть заводское.
        var assignments = new Dictionary<string, string> { [HotkeyMap.NewChat] = "Ctrl" };

        Assert.Equal("Ctrl+F", HotkeyMap.Gesture(assignments, HotkeyMap.NewChat));
    }

    [Fact]
    public void An_assigned_shortcut_wins_and_is_no_longer_the_default()
    {
        var assignments = new Dictionary<string, string> { [HotkeyMap.NewChat] = "shift+ctrl+n" };

        Assert.Equal("Ctrl+Shift+N", HotkeyMap.Gesture(assignments, HotkeyMap.NewChat));
        Assert.False(HotkeyMap.IsDefault(assignments, HotkeyMap.NewChat));
    }

    [Fact]
    public void Every_action_ships_with_a_readable_default_and_its_own_labels()
    {
        Assert.NotEmpty(HotkeyMap.All);

        foreach (var action in HotkeyMap.All)
        {
            Assert.True(HotkeyMap.TryParse(action.DefaultGesture, out var normalized));
            Assert.Equal(action.DefaultGesture, normalized);

            // Подписи идут через словарь, а не литералами: иначе раздел остался бы русским
            // на любом языке.
            Assert.True(StringsRu.Values.ContainsKey(action.TitleKey), action.TitleKey);
            Assert.True(StringsRu.Values.ContainsKey(action.DescriptionKey), action.DescriptionKey);
        }

        // Одинаковые имена действий развели бы назначения по одному ключу настроек.
        Assert.Equal(HotkeyMap.All.Count, HotkeyMap.All.Select(a => a.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_shortcut_is_shown_with_spaces_around_the_plus()
    {
        Assert.Equal("Ctrl + F", HotkeyMap.Display("Ctrl+F"));
    }
}
