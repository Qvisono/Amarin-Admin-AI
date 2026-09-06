using System.Reflection;
using System.Security.Principal;

namespace Amarin.UI;

internal static class RuntimeContext
{
    public static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Shown in the window title and in Settings. Plain version — 1.14.0 is a full release.</summary>
    public static string AppVersionDisplay => AppVersion;

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}