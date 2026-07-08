namespace Amarin.Core;

public enum SessionMode
{
    Continuous,
    Isolated
}

public static class SessionModeParser
{
    public static bool TryParse(string? value, out SessionMode mode)
    {
        mode = SessionMode.Continuous;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.Equals("continuous", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("history", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal))
        {
            mode = SessionMode.Continuous;
            return true;
        }

        if (value.Equals("isolated", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("new", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("2", StringComparison.Ordinal))
        {
            mode = SessionMode.Isolated;
            return true;
        }

        return false;
    }

    public static string ToDisplayName(SessionMode mode) => mode switch
    {
        SessionMode.Continuous => "история сессии",
        SessionMode.Isolated => "новая сессия на каждый запрос",
        _ => mode.ToString()
    };

    public static string ToShortName(SessionMode mode) => mode switch
    {
        SessionMode.Continuous => "с историей",
        SessionMode.Isolated => "без истории",
        _ => mode.ToString()
    };

    public static string ToConfigValue(SessionMode mode) => mode switch
    {
        SessionMode.Isolated => "isolated",
        _ => "continuous"
    };
}