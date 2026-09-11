namespace Amarin.Core;

internal static class AppPaths
{
    public const string FolderName = "Amarin Admin AI";

    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName);

    /// <summary>
    /// Last known Venice balance. A file of its own rather than a field in settings.json:
    /// it changes after every single turn, and settings would be rewritten just as often.
    /// </summary>
    public static string BalanceFile => Path.Combine(Root, "balance.json");

    public static string ChatsDirectory => Path.Combine(Root, "chats");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChatsDirectory);
    }
}
