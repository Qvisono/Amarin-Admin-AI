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

    /// <summary>
    /// Ключи Venice активного профиля, зашифрованные DPAPI. Рядом с settings.json и по тем же
    /// правилам: у каждого профиля свой файл.
    /// </summary>
    public static string KeysFile => Path.Combine(Root, "keys.json");

    /// <summary>
    /// Свёрнутые по дням траты, по файлу на ключ. Отдельной папкой, а не полем в настройках:
    /// она дописывается при каждом заходе на страницу «Key &amp; Info» и растёт весь год.
    /// </summary>
    public static string UsageDirectory => Path.Combine(Root, "usage");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ChatsDirectory);
    }
}
