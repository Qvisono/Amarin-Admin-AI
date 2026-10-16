namespace Amarin.Fox;

/// <summary>О чём сообщение — ключи тем в <see cref="FoxDay.Topics"/> и в правилах мозга.</summary>
internal static class FoxTopic
{
    public const string Science = "science";
    public const string Code = "code";
    public const string Virus = "virus";
    public const string Scam = "scam";
    public const string Sad = "sad";
    public const string Happy = "happy";
    public const string Thanks = "thanks";
    public const string Rude = "rude";
    public const string RudeToFox = "rude-to-fox";
    public const string Praise = "praise";
    public const string Nonsense = "nonsense";
    public const string Long = "long";
}

/// <summary>Что инстинкт понял из сообщения: темы и бред.</summary>
/// <param name="Topics">Темы из <see cref="FoxTopic"/>.</param>
/// <param name="WrongLayout">Бред оказался русским в английской раскладке (или наоборот).</param>
internal sealed record FoxReading(IReadOnlySet<string> Topics, bool WrongLayout)
{
    public bool Has(string topic) => Topics.Contains(topic);
}

/// <summary>
/// Инстинкт Розы: мгновенная и бесплатная реакция по словам, без модели.
/// </summary>
/// <remarks>
/// <para>
/// Модель Розы уточняет реакцию позже (<c>FoxMind</c>), а инстинкт работает всегда — и без ключа,
/// и при выключенном чтении сообщений моделью. Поэтому он осторожен: лучше не заметить тему, чем
/// напугать Розу «вирусной рекламой» или назвать бредом команду PowerShell.
/// </para>
/// <para>
/// От культуры машины не зависит: регистр сбрасывается инвариантно, раннер CI — en-US, и тест,
/// который полагается на русскую машину, на нём краснел бы.
/// </para>
/// </remarks>
internal static class FoxInstinct
{
    /// <summary>С какой длины сообщение «ох, сколько».</summary>
    public const int LongMessage = 600;

    private static readonly Lazy<Lists> Words = new(() => new Lists());

    private sealed class Lists
    {
        public readonly FoxWords Science = FoxWords.Load("science");
        public readonly FoxWords Code = FoxWords.Load("code");
        public readonly FoxWords Virus = FoxWords.Load("virus");
        public readonly FoxWords VirusNot = FoxWords.Load("virus-not");
        public readonly FoxWords Clean = FoxWords.Load("clean");
        public readonly FoxWords Scam = FoxWords.Load("scam");
        public readonly FoxWords Sad = FoxWords.Load("sad");
        public readonly FoxWords Happy = FoxWords.Load("happy");
        public readonly FoxWords Thanks = FoxWords.Load("thanks");
        public readonly FoxWords Rude = FoxWords.Load("rude");
        public readonly FoxWords Praise = FoxWords.Load("praise");
        public readonly FoxWords Fox = FoxWords.Load("fox");
        public readonly FoxWords CommonRu = FoxWords.Load("common-ru");
        public readonly FoxWords CommonEn = FoxWords.Load("common-en");
    }

    /// <summary>Читает сообщение человека.</summary>
    public static FoxReading Read(string? message)
    {
        var topics = new HashSet<string>(StringComparer.Ordinal);
        var raw = message ?? "";
        if (raw.Trim().Length == 0)
        {
            return new FoxReading(topics, false);
        }

        var text = new FoxText(raw);
        var lists = Words.Value;
        var aboutFox = lists.Fox.In(text);

        if (LooksLikeCode(raw) || lists.Code.In(text))
        {
            topics.Add(FoxTopic.Code);
        }

        if (lists.Science.In(text))
        {
            topics.Add(FoxTopic.Science);
        }

        if (lists.Virus.In(text) && !lists.VirusNot.In(text))
        {
            topics.Add(FoxTopic.Virus);
        }

        AddIf(topics, FoxTopic.Scam, lists.Scam.In(text));
        AddIf(topics, FoxTopic.Sad, lists.Sad.In(text));
        AddIf(topics, FoxTopic.Happy, lists.Happy.In(text));
        AddIf(topics, FoxTopic.Thanks, lists.Thanks.In(text));

        if (lists.Rude.In(text))
        {
            topics.Add(aboutFox ? FoxTopic.RudeToFox : FoxTopic.Rude);
        }
        else if (aboutFox && lists.Praise.In(text))
        {
            topics.Add(FoxTopic.Praise);
        }

        AddIf(topics, FoxTopic.Long, raw.Length >= LongMessage);

        var wrongLayout = false;
        if (topics.Count == 0 && IsNonsense(raw, text, out wrongLayout))
        {
            topics.Add(FoxTopic.Nonsense);
        }

        return new FoxReading(topics, wrongLayout);
    }

    /// <summary>Ответ ассистента говорит, что угроз нет: облегчение после испуга.</summary>
    public static bool SoundsClean(string? reply) =>
        !string.IsNullOrWhiteSpace(reply) && Words.Value.Clean.In(new FoxText(reply));

    /// <summary>Похоже на код, путь или команду — бредом это не бывает, а кодом может.</summary>
    internal static bool LooksLikeCode(string raw) =>
        raw.Contains("```", StringComparison.Ordinal) ||
        raw.Contains("=>", StringComparison.Ordinal) ||
        raw.Contains("();", StringComparison.Ordinal) ||
        (raw.Contains('{', StringComparison.Ordinal) && raw.Contains('}', StringComparison.Ordinal) && raw.Contains(';', StringComparison.Ordinal));

    /// <summary>
    /// Бред: набор случайных букв, каша по клавиатуре, текст не в той раскладке.
    /// </summary>
    /// <remarks>
    /// Не бред — то, что человек мог написать всерьёз: пути (<c>C:\…</c>, <c>/usr/…</c>), ссылки,
    /// команды с ключами (<c>-Force</c>), код, числа и короткие «ок», «да», «ага». Слово без гласных
    /// одно ещё ничего не значит (<c>lsblk</c>), поэтому решают приметы целиком: доля «плохих» слов,
    /// клавиатурные ряды, повторы букв — и узнавание частых слов после перевода раскладки.
    /// </remarks>
    internal static bool IsNonsense(string raw, FoxText text, out bool wrongLayout)
    {
        wrongLayout = false;
        var trimmed = raw.Trim();
        if (trimmed.Length < 4 || LooksLikeCode(raw) || LooksLikePathOrCommand(trimmed))
        {
            return false;
        }

        var words = text.Words.Where(word => word.Any(char.IsLetter)).ToList();
        if (words.Count == 0)
        {
            return false;
        }

        // Не та раскладка: после перевода хотя бы половина слов — частые слова другого языка.
        var lists = Words.Value;
        var swapped = words.Select(SwapLayout).ToList();
        var recognized = swapped.Count(word => lists.CommonRu.Knows(word) || lists.CommonEn.Knows(word));
        if (recognized > 0 && recognized * 2 >= words.Count && words.All(word => !lists.CommonRu.Knows(word) && !lists.CommonEn.Knows(word)))
        {
            wrongLayout = true;
            return true;
        }

        var bad = words.Count(IsMash);
        return bad > 0 && bad * 10 >= words.Count * 6 && words.Any(word => word.Length >= 5);
    }

    private static bool LooksLikePathOrCommand(string text) =>
        text.Contains(":\\", StringComparison.Ordinal) ||
        text.Contains("://", StringComparison.Ordinal) ||
        text.StartsWith('/') ||
        text.StartsWith('.') ||
        text.Contains(" -", StringComparison.Ordinal) ||
        text.Contains(" /", StringComparison.Ordinal) ||
        text.Contains('\\', StringComparison.Ordinal);

    private const string Vowels = "aeiouyаеиоуыэюя";

    private static readonly string[] Rows =
    [
        "qwertyuiop", "asdfghjkl", "zxcvbnm", "йцукенгшщзхъ", "фывапролджэ", "ячсмитьбю", "1234567890"
    ];

    /// <summary>Каша: пять подряд по клавиатурному ряду, одна буква пять раз, или длинное слово почти без гласных.</summary>
    private static bool IsMash(string word)
    {
        // Только латиница и кириллица: у иероглифов нет ни гласных, ни пробелов, и любое
        // сообщение по-китайски выглядело бы «длинным словом без гласных».
        if (word.Length < 4 || !word.All(symbol => symbol is >= 'a' and <= 'z' or >= 'а' and <= 'я'))
        {
            return false;
        }

        for (var i = 0; i + 4 < word.Length; i++)
        {
            if (word[i] == word[i + 1] && word[i] == word[i + 2] && word[i] == word[i + 3] && word[i] == word[i + 4])
            {
                return true;
            }
        }

        foreach (var row in Rows)
        {
            for (var i = 0; i + 4 < word.Length; i++)
            {
                var chunk = word.Substring(i, 5);
                if (row.Contains(chunk, StringComparison.Ordinal) || row.Contains(new string([.. chunk.Reverse()]), StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        var vowels = word.Count(symbol => Vowels.Contains(symbol, StringComparison.Ordinal));
        return word.Length >= 7 && vowels * 10 < word.Length;
    }

    private const string Latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
    private const string Cyrillic = "йцукенгшщзхъфывапролджэячсмитьбюё";

    /// <summary>То же нажатие в другой раскладке ЙЦУКЕН/QWERTY.</summary>
    internal static string SwapLayout(string word)
    {
        var chars = word.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var latin = Latin.IndexOf(chars[i], StringComparison.Ordinal);
            if (latin >= 0)
            {
                chars[i] = Cyrillic[latin];
                continue;
            }

            var cyrillic = Cyrillic.IndexOf(chars[i], StringComparison.Ordinal);
            if (cyrillic >= 0)
            {
                chars[i] = Latin[cyrillic];
            }
        }

        return FoxWords.Normalize(new string(chars));
    }

    private static void AddIf(HashSet<string> topics, string topic, bool condition)
    {
        if (condition)
        {
            topics.Add(topic);
        }
    }
}
