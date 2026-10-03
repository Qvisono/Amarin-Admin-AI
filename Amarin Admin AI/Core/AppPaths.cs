namespace Amarin.Core;

internal static class AppPaths
{
    public const string FolderName = "Amarin Admin AI";

    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            FolderName);

    /// <summary>
    /// Последние известные остатки ключей (<see cref="BalanceBook"/>). Своим файлом, а не полем
    /// settings.json: меняется после каждого хода, и настройки переписывались бы так же часто.
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
