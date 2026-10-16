using System.Reflection;

namespace Amarin.Fox;

/// <summary>
/// Список слов инстинкта из ресурса <c>Brain/Words/&lt;имя&gt;.txt</c>.
/// </summary>
/// <remarks>
/// Слова в ресурсах, а не в коде: их дописывают чаще, чем код, и читаются они глазами в диффе.
/// Строка — начало слова (русские слова меняют окончания: «вирус», «вирусом», «вирусы»), со знаком
/// «=» — только слово целиком (иначе «ура» ловило бы «уравнение»), с пробелом внутри — фраза,
/// которая ищется подстрокой.
/// </remarks>
internal sealed class FoxWords
{
    private readonly string[] _prefixes;
    private readonly HashSet<string> _whole;
    private readonly string[] _phrases;

    private FoxWords(IEnumerable<string> lines)
    {
        var prefixes = new List<string>();
        var whole = new HashSet<string>(StringComparer.Ordinal);
        var phrases = new List<string>();
        foreach (var raw in lines)
        {
            var line = Normalize(raw.Trim());
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('='))
            {
                whole.Add(line[1..]);
            }
            else if (line.Contains(' ', StringComparison.Ordinal))
            {
                phrases.Add(line);
            }
            else
            {
                prefixes.Add(line);
            }
        }

        _prefixes = [.. prefixes];
        _whole = whole;
        _phrases = [.. phrases];
    }

    /// <summary>Все слова, как они записаны (без «=»), — для перевода раскладки и тестов.</summary>
    public IEnumerable<string> Words => _prefixes.Concat(_whole);

    public static FoxWords Load(string name)
    {
        var assembly = typeof(FoxWords).Assembly;
        using var stream = assembly.GetManifestResourceStream("Amarin.Fox.Words." + name + ".txt")
                           ?? throw new InvalidOperationException("Нет списка слов: " + name);
        using var reader = new StreamReader(stream);
        return new FoxWords(reader.ReadToEnd().Split('\n'));
    }

    /// <summary>Есть ли в тексте хоть одно слово списка. Текст — уже через <see cref="Normalize"/>.</summary>
    public bool In(FoxText text)
    {
        foreach (var phrase in _phrases)
        {
            if (text.Joined.Contains(phrase, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var word in text.Words)
        {
            if (_whole.Contains(word))
            {
                return true;
            }

            foreach (var prefix in _prefixes)
            {
                if (word.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Знает ли список это слово целиком (для частых слов при проверке раскладки).</summary>
    public bool Knows(string word) => _whole.Contains(word) || _prefixes.Contains(word, StringComparer.Ordinal);

    /// <summary>Нижний регистр без культуры машины, «ё» как «е».</summary>
    public static string Normalize(string text) => text.ToLowerInvariant().Replace('ё', 'е');
}

/// <summary>Текст сообщения, разобранный один раз: слова и строка из них через пробел.</summary>
internal sealed class FoxText
{
    public FoxText(string text)
    {
        var normalized = FoxWords.Normalize(text);
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var inWord = i < normalized.Length && (char.IsLetterOrDigit(normalized[i]) || normalized[i] is '#' or '+');
            if (inWord && start < 0)
            {
                start = i;
            }
            else if (!inWord && start >= 0)
            {
                words.Add(normalized[start..i]);
                start = -1;
            }
        }

        Words = words;
        Joined = " " + string.Join(' ', words) + " ";
    }

    public IReadOnlyList<string> Words { get; }

    /// <summary>Слова через пробел с пробелами по краям — для поиска фраз.</summary>
    public string Joined { get; }
}
