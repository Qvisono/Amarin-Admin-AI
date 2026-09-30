using System.Text.RegularExpressions;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Ключ, которого нет в словаре, <see cref="Loc"/> показывает человеку как есть — «S.Key.Active»
/// вместо подписи. Так и было до 1.28.0 с подсказкой активного ключа и с ответом журнала при
/// сбое: обе строки забыли завести, и ни один тест этого не заметил.
/// </summary>
public sealed partial class LocalizationKeyUsageTests
{
    [Fact]
    public void Every_key_the_code_asks_for_is_in_the_dictionary()
    {
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in SourceTree.SourceFiles())
        {
            var name = Path.GetFileName(file);
            if (name == "StringsRu.cs" || file.Contains($"{Path.DirectorySeparatorChar}Lang{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (Match match in QuotedKey().Matches(text))
            {
                Collect(match.Groups[1].Value);
            }

            foreach (Match match in ResourceKey().Matches(text))
            {
                Collect(match.Groups[1].Value);
            }
        }

        Assert.True(missing.Count == 0, "нет в словаре: " + string.Join(", ", missing));

        void Collect(string key)
        {
            // «S.Tool.» и подобные — приставки, к которым код дописывает имя сам.
            if (!key.EndsWith('.') && !StringsRu.Values.ContainsKey(key))
            {
                missing.Add(key);
            }
        }
    }

    [GeneratedRegex("\"(S\\.[A-Za-z0-9_.]+)\"")]
    private static partial Regex QuotedKey();

    [GeneratedRegex(@"DynamicResource (S\.[A-Za-z0-9_.]+)")]
    private static partial Regex ResourceKey();
}
