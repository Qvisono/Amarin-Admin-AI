using System.Reflection;
using System.Security.Principal;
using Amarin.Core;

namespace Amarin.UI;

internal static class RuntimeContext
{
    /// <summary>
    /// Версия программы, как её выпустили: три числа и пометка беты, если она есть
    /// (<c>1.29.0-beta.1</c>). Показывается в заголовке окна и в настройках.
    /// </summary>
    /// <remarks>
    /// Из <c>AssemblyInformationalVersion</c>, а не из версии сборки: SDK выбрасывает пометку из
    /// второй, и бета выглядела бы финальной — ни человек, ни проверка обновлений их бы не
    /// различили. Хвост после «+» (хеш коммита, который SDK приписывает сам) отрезается.
    /// </remarks>
    public static string AppVersion { get; } = UpdateChecker.VersionOf(Assembly.GetExecutingAssembly());

    /// <summary>То же для сравнения с выпусками на GitHub.</summary>
    public static ReleaseVersion AppRelease => ReleaseVersion.Parse(AppVersion) ?? new Version(1, 0, 0);

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