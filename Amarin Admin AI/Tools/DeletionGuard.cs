using System.Text.RegularExpressions;

namespace Amarin.Tools;

internal static partial class DeletionGuard
{
    private static readonly string[] PowerShellDeletionPatterns =
    [
        @"\bRemove-Item\b",
        @"\bRemove-ItemProperty\b",
        @"\bClear-Content\b",
        @"\bri\b",
        @"\brm\b",
        @"\bdel\b",
        @"\berase\b",
        @"\brmdir\b",
        @"\brd\b",
        @"\bFormat-Volume\b",
        @"\bClear-RecycleBin\b"
    ];

    public static bool PowerShellAttemptsDeletion(string command)
    {
        foreach (var pattern in PowerShellDeletionPatterns)
        {
            if (Regex.IsMatch(command, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    public const string FileDeletionBlockedMessage =
        "Удаление существующих файлов и папок запрещено политикой Amarin.";
}