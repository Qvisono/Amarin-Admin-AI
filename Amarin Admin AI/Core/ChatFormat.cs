using System.Globalization;

namespace Amarin.Core;

internal static class ChatFormat
{
    public static string Duration(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)Math.Round(elapsed.TotalSeconds, MidpointRounding.AwayFromZero));
        if (totalSeconds < 60)
        {
            return $"{totalSeconds}s";
        }

        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}m{seconds}s";
    }

    public static string Working(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        return $"Working {totalSeconds}s";
    }

    public static string Clock(DateTime timestamp) => timestamp.ToString("HH:mm");

    public static string Cost(VeniceCost? cost)
    {
        if (cost is null || !cost.HasData)
        {
            return "";
        }

        var usd = cost.Usd < 0.0001m && cost.Usd > 0
            ? "<0,0001"
            : cost.Usd.ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',');
        return "$" + usd;
    }
}
