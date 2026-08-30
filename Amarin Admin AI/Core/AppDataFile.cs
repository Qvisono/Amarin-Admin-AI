using System.Text;

namespace Amarin.Core;

internal static class AppDataFile
{
    public static void WriteAtomic(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }
}
