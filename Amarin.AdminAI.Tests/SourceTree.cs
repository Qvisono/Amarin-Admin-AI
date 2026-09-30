namespace Amarin.AdminAI.Tests;

/// <summary>Исходники программы рядом со сборкой тестов — для проверок, которые читают код.</summary>
internal static class SourceTree
{
    /// <summary>Папка проекта программы («Amarin Admin AI»), найденная вверх от сборки тестов.</summary>
    public static string ProjectDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "Amarin Admin AI");
                if (File.Exists(Path.Combine(candidate, "Amarin Admin AI.csproj")))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Amarin Admin AI");
        }
    }

    /// <summary>Все .cs и .xaml программы, кроме сборочных папок.</summary>
    public static IEnumerable<string> SourceFiles()
    {
        var root = ProjectDirectory;
        var separator = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
                           !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal));
    }
}
