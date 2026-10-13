namespace Amarin.Fox;

/// <summary>
/// Шалости Розы: когда можно и какая.
/// </summary>
/// <remarks>
/// <para>
/// Шалость — не помеха работе: ни одна не срабатывает, пока человек пишет, ждёт ответа, выбирает
/// чаты, смотрит в лупу или читает вопрос поверх ленты. Отсюда длинный список условий в
/// <see cref="Allowed"/> — каждое закрывает случай, где шалость отняла бы у человека нажатие.
/// </para>
/// <para>
/// Делают что-то в программе ровно две (<see cref="Acting"/>): ткнуть «Новый чат» — черновики и
/// ходы при этом не теряются — и подтолкнуть колонку чатов. Остальные — только картинка поверх
/// окна. Набор закреплён тестом: третья «настоящая» шалость — это решение, а не правка списка.
/// </para>
/// </remarks>
internal static class FoxMischief
{
    public const string NewChat = "new-chat";
    public const string PushColumn = "push-column";
    public const string SitOnSend = "sit-on-send";
    public const string TailInInput = "tail-in-input";
    public const string Footprints = "footprints";
    public const string PeekColumn = "peek-column";
    public const string ChewChip = "chew-chip";

    /// <summary>Шалости, которые что-то делают в программе.</summary>
    public static readonly string[] Acting = [NewChat, PushColumn];

    /// <summary>Шалости-картинки: поверх окна, ничего не нажимают.</summary>
    public static readonly string[] Visual = [SitOnSend, TailInInput, Footprints, PeekColumn, ChewChip];

    /// <summary>Не чаще пяти раз в час.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(12);

    /// <summary>Разозлённый шалит вдвое чаще.</summary>
    public static readonly TimeSpan AngryCooldown = TimeSpan.FromMinutes(6);

    /// <summary>Сколько человек должен не печатать, чтобы Роза решился.</summary>
    public static readonly TimeSpan QuietTyping = TimeSpan.FromSeconds(20);

    public static bool Angry(FoxState state) => state.Cold || state.Resentment >= 50;

    public static bool Allowed(FoxState state, FoxSituation situation, DateTime now)
    {
        if (!situation.MischiefEnabled || !situation.WindowActive || situation.Covered || !situation.InputEmpty ||
            situation.TurnRunning || situation.UiBusy || situation.Away || situation.SinceTyping < QuietTyping)
        {
            return false;
        }

        var cooldown = Angry(state) ? AngryCooldown : Cooldown;
        return state.LastMischiefAt is not { } last || now - last >= cooldown;
    }

    /// <summary>Какую шалость выкинуть: разозлённый чаще берётся за настоящие.</summary>
    public static string Choose(FoxState state, Random random)
    {
        var acting = Angry(state) ? 0.5 : 0.2;
        return random.NextDouble() < acting
            ? Acting[random.Next(Acting.Length)]
            : Visual[random.Next(Visual.Length)];
    }
}
