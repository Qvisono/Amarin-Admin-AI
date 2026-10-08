using System.Text.RegularExpressions;
using System.Xml.Linq;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Текст программы без длинного и среднего тире (1.32.0): «—» и «–» — первая примета текста,
/// написанного моделью, и человек их не печатает. В строках интерфейса, README и заметках к
/// выпуску на их месте — дефис с пробелами или другая фраза.
/// </summary>
/// <remarks>
/// Проверяются значения, а не файлы целиком: в комментариях разметки и кода тире законны — их
/// читает разработчик. Системные промпты сюда не входят: их читает модель, и правят их только с
/// прогоном на живых запросах.
/// </remarks>
public sealed partial class TypographyTests
{
    private static readonly char[] Dashes = ['—', '–'];

    [Fact]
    public void Interface_strings_have_no_long_dashes()
    {
        var offenders = new List<string>();
        foreach (var file in new[] { "UI/Lang/Strings.ru.xaml", "UI/Lang/Strings.en.xaml" })
        {
            var document = XDocument.Load(SourceTree.ProjectFile(file));
            offenders.AddRange(document.Descendants()
                .Where(element => element.Name.LocalName == "String" && element.Value.IndexOfAny(Dashes) >= 0)
                .Select(element => $"{Path.GetFileName(file)}: {element.Attributes().First(attribute => attribute.Name.LocalName == "Key").Value}"));
        }

        offenders.AddRange(StringsRu.Values.Where(pair => pair.Value.IndexOfAny(Dashes) >= 0).Select(pair => "StringsRu.cs: " + pair.Key));
        Assert.True(offenders.Count == 0, "длинное тире в строках: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_readme_and_the_notes_of_this_version_have_no_long_dashes()
    {
        var csproj = File.ReadAllText(SourceTree.ProjectFile("Amarin Admin AI.csproj"));
        var version = VersionTag().Match(csproj).Groups[1].Value;
        var files = new List<string> { SourceTree.RepositoryFile("README.md") };
        var notes = SourceTree.RepositoryFile($".github/release-notes/v{version}.md");
        if (File.Exists(notes))
        {
            files.Add(notes);
        }

        var offenders = files
            .SelectMany(path => File.ReadAllLines(path).Select((line, index) => (path, line, index)))
            .Where(item => item.line.IndexOfAny(Dashes) >= 0)
            .Select(item => $"{Path.GetFileName(item.path)}:{item.index + 1}")
            .ToList();

        Assert.True(offenders.Count == 0, "длинное тире: " + string.Join(", ", offenders));
    }

    [GeneratedRegex("<Version>([^<]+)</Version>")]
    private static partial Regex VersionTag();
}
