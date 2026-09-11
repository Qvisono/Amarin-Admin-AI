namespace Amarin.UI;

internal enum StartupRoute
{
    /// <summary>Консольный прогон инструментов — идёт всегда, даже при запущенной программе.</summary>
    SmokeTools,

    /// <summary>Программа уже работает: запрос отдан ей, этот процесс больше не нужен.</summary>
    HandedOff,

    /// <summary>Обычный запуск окна.</summary>
    Run
}

internal static class StartupRouter
{
    /// <summary>
    /// Куда идти запуску.
    /// </summary>
    /// <remarks>
    /// Отдельной функцией, чтобы порядок проверялся тестом, а не чтением кода. Порядок важен:
    /// <c>--smoke-tools</c> решается до замка — это консольный прогон инструментов, и он обязан
    /// работать при уже запущенной программе; а замок берётся до всего остального, иначе дубль
    /// успел бы мигнуть экраном входа и переписать <c>profiles.json</c>.
    /// </remarks>
    public static StartupRoute Decide(StartupArgs startup, Func<bool> tryBecomePrimary)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(tryBecomePrimary);

        if (startup.SmokeTools)
        {
            return StartupRoute.SmokeTools;
        }

        return tryBecomePrimary() ? StartupRoute.Run : StartupRoute.HandedOff;
    }
}
