namespace Amarin.AdminAI.Tests;

/// <summary>Исходники программы рядом со сборкой тестов — для проверок, которые читают код.</summary>
/// <remarks>
/// Один помощник на все такие проверки. До 1.30.0 их было пять копий, и каждая искала только папку
/// «Amarin Admin AI»: вынеси файл в другой проект — и проверка, сканирующая «все исходники», молча
/// перестала бы его видеть, оставаясь зелёной. Теперь рабочих проектов несколько (приложение и с
/// 1.30.0 <c>Amarin.Core</c>), и обход идёт по всем сразу.
/// </remarks>
internal static class SourceTree
{
    /// <summary>Имена папок рабочих проектов — тех, чей код уходит в программу.</summary>
    private static readonly string[] Projects = ["Amarin Admin AI", "Amarin.Core"];

    /// <summary>Корень репозитория: папка с файлом решения, найденная вверх от сборки тестов.</summary>
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>Папка проекта приложения («Amarin Admin AI»): окна, разметка, запуск.</summary>
    public static string ProjectDirectory => Path.Combine(RepositoryRoot, Projects[0]);

    /// <summary>Папка тестов.</summary>
    public static string TestsDirectory => Path.Combine(RepositoryRoot, "Amarin.AdminAI.Tests");

    /// <summary>Папки всех рабочих проектов, какие есть в репозитории.</summary>
    public static IReadOnlyList<string> ProjectDirectories =>
        [.. Projects.Select(name => Path.Combine(RepositoryRoot, name)).Where(Directory.Exists)];

    /// <summary>Все .cs и .xaml рабочих проектов, кроме сборочных папок.</summary>
    public static IEnumerable<string> SourceFiles() =>
        ProjectDirectories.SelectMany(root => Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                           path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(IsSource);

    /// <summary>
    /// Файл рабочего проекта по пути от папки проекта (<c>UI/MainWindow.xaml</c>), а если его там нет —
    /// по имени файла в любом рабочем проекте.
    /// </summary>
    /// <remarks>
    /// Второе — ради переноса файлов между папками и проектами: проверка, названная по старому
    /// пути, продолжает находить свой файл, пока его имя единственно. Двух файлов с одним именем
    /// поиск не угадывает и падает с понятной причиной.
    /// </remarks>
    public static string ProjectFile(string relative)
    {
        foreach (var root in ProjectDirectories)
        {
            var candidate = Path.Combine(root, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Find(Path.GetFileName(relative));
    }

    /// <summary>Единственный файл с таким именем среди рабочих проектов.</summary>
    public static string Find(string fileName)
    {
        var matches = ProjectDirectories
            .SelectMany(root => Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            .Where(IsSource)
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"{fileName} нет ни в одном рабочем проекте."),
            _ => throw new InvalidOperationException($"{fileName} не единственный: {string.Join(", ", matches)}")
        };
    }

    /// <summary>Файл от корня репозитория (<c>.github/workflows/ci.yml</c>).</summary>
    public static string RepositoryFile(string relative) => Path.Combine(RepositoryRoot, relative);

    private static bool IsSource(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal) &&
               !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Amarin Admin AI.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Amarin Admin AI.slnx");
    }
}
