using System.Text.RegularExpressions;

namespace Amarin.Tools;

internal static partial class DeletionGuard
{
    [GeneratedRegex(
        @"\b(?:Remove-Item|Remove-ItemProperty|Clear-Content|ri|rm|del|erase|rmdir|rd|Format-Volume|Clear-RecycleBin)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeletionPattern();

    public static bool PowerShellAttemptsDeletion(string command) =>
        DeletionPattern().IsMatch(command);

    public const string FileDeletionBlockedMessage =
        "Удаление существующих файлов и папок запрещено политикой Amarin.";
}