namespace Amarin.Core;

/// <summary>
/// Языки блоков кода (D13): как назвать, с каким расширением сохранить и можно ли отдать агенту
/// на выполнение.
/// </summary>
internal static class CodeLanguages
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ps"] = "powershell",
        ["ps1"] = "powershell",
        ["pwsh"] = "powershell",
        ["posh"] = "powershell",
        ["bat"] = "batch",
        ["cmd"] = "batch",
        ["dos"] = "batch",
        ["py"] = "python",
        ["js"] = "javascript",
        ["ts"] = "typescript",
        ["cs"] = "csharp",
        ["c#"] = "csharp",
        ["sh"] = "bash",
        ["shell"] = "bash",
        ["zsh"] = "bash",
        ["yml"] = "yaml",
        ["md"] = "markdown",
        ["rs"] = "rust",
        ["c++"] = "cpp",
        ["htm"] = "html"
    };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = ".ps1",
        ["batch"] = ".cmd",
        ["python"] = ".py",
        ["javascript"] = ".js",
        ["typescript"] = ".ts",
        ["csharp"] = ".cs",
        ["bash"] = ".sh",
        ["json"] = ".json",
        ["xml"] = ".xml",
        ["html"] = ".html",
        ["css"] = ".css",
        ["sql"] = ".sql",
        ["yaml"] = ".yaml",
        ["ini"] = ".ini",
        ["toml"] = ".toml",
        ["reg"] = ".reg",
        ["markdown"] = ".md",
        ["c"] = ".c",
        ["cpp"] = ".cpp",
        ["java"] = ".java",
        ["go"] = ".go",
        ["rust"] = ".rs",
        ["php"] = ".php",
        ["ruby"] = ".rb",
        ["csv"] = ".csv",
        ["dockerfile"] = ".dockerfile"
    };

    /// <summary>Каноническое имя языка: «ps1», «pwsh» и «PowerShell» — один язык.</summary>
    public static string Normalize(string? language)
    {
        var name = (language ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return "text";
        }

        return Aliases.TryGetValue(name, out var canonical) ? canonical : name;
    }

    /// <summary>Расширение файла для «Сохранить»; незнакомый язык — <c>.txt</c>.</summary>
    public static string Extension(string? language) =>
        Extensions.TryGetValue(Normalize(language), out var extension) ? extension : ".txt";

    /// <summary>
    /// Скрипт, который агент может выполнить на этом ПК: PowerShell и командные файлы Windows.
    /// Прочее (bash, python) здесь выполнять нечем — кнопки у них нет.
    /// </summary>
    public static bool IsRunnableScript(string? language) => Normalize(language) is "powershell" or "batch";
}
