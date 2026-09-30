namespace Amarin.Core;

/// <summary>
/// Правило автоблокировки: когда закрывать окно экраном блокировки.
/// </summary>
/// <remarks>
/// Без WPF — как <see cref="HotkeyMap"/>: решение проверяется тестами без окна, а окно только
/// отмечает ввод и раз в полминуты спрашивает.
/// </remarks>
public static class AutoLock
{
    /// <summary>Что предлагается на странице «Безопасность»; 0 — не блокировать.</summary>
    public static readonly IReadOnlyList<int> Choices = [0, 5, 15, 30, 60];

    /// <summary>
    /// Приводит значение из настроек к одному из <see cref="Choices"/>: файл правят руками, и
    /// странице с таймером нельзя расходиться в том, что считать «через 10 минут».
    /// </summary>
    /// <remarks>Нестандартное значение округляется вверх — блокировка не наступит раньше заданного.</remarks>
    public static int Normalize(int minutes)
    {
        if (minutes <= 0)
        {
            return 0;
        }

        foreach (var choice in Choices)
        {
            if (choice >= minutes)
            {
                return choice;
            }
        }

        return Choices[^1];
    }

    /// <summary>Пора ли блокировать.</summary>
    /// <param name="minutes">Значение из настроек.</param>
    /// <param name="hasPassword">У профиля есть пароль: без него снять блокировку нечем.</param>
    /// <param name="lastInputUtc">Последний ввод в окне программы.</param>
    public static bool IsDue(int minutes, bool hasPassword, DateTime lastInputUtc, DateTime nowUtc)
    {
        var limit = Normalize(minutes);
        return hasPassword && limit > 0 && nowUtc - lastInputUtc >= TimeSpan.FromMinutes(limit);
    }
}
