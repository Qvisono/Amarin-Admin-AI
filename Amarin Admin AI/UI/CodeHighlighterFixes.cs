using ColorCode;

namespace Amarin.UI;

/// <summary>
/// Правила ColorCode, которые на незакрытой строке или комментарии перебирают варианты без конца,
/// и их замены на то же самое без перебора.
/// </summary>
/// <remarks>
/// <para>
/// Подсветка идёт на потоке окна при каждой перерисовке ответа, пока он пишется, а в блоке, который
/// модель ещё дописывает, строка и комментарий незакрыты почти всегда. У правила строки JSON
/// <c>"[^"\\]*(?:\\[^\r\n]|[^"\\]*)*"</c> повтор вложен в повтор, и неудачный разбор перебирает все
/// способы нарезать текст: каждый лишний символ удваивает время. Строка в 40 символов — это дни, и
/// программа висела намертво посреди ответа с JSON (1.36.0-beta.1). Ключ JSON после запятой виснет
/// так же и в готовом ответе: правило ищет за строкой двоеточие и не находит его у строки массива.
/// </para>
/// <para>
/// Замена — подстрока на подстроку того же языка (совпадает тот же текст, номера групп те же), но
/// без двух способов прочесть один символ. Язык с заменой регистрируется под своим же <c>Id</c>:
/// так его берёт и подсветка вложенных языков (скрипт внутри HTML). Сверяют и замены, и все языки
/// разом <c>CodeHighlighterSafetyTests</c>.
/// </para>
/// </remarks>
internal static class CodeHighlighterFixes
{
    /// <summary>Что заменить и чем. Порядок не важен: куски не пересекаются.</summary>
    internal static readonly (string Unsafe, string Safe)[] Replacements =
    [
        // Строка JSON (и в правиле строки, и в правиле ключа): развёрнутый цикл — каждое
        // повторение начинается с обратной косой черты, и прочесть строку можно одним способом.
        (@"""[^""\\]*(?:\\[^\r\n]|[^""\\]*)*""", @"""[^""\\]*(?:\\[^\r\n][^""\\]*)*"""),

        // /* */ — C#, JS, TS, Java, C++, PHP, CSS. Перевод строки и так входит в [^*] и [^*/],
        // а лишняя ветка давала каждому из них второй способ прочтения.
        (@"/\*([^*]|[\r\n]|(\*+([^*/]|[\r\n])))*\*+/", @"/\*([^*]|(\*+([^*/])))*\*+/"),

        // (* *) — тот же случай.
        (@"\(\*([^*]|[\r\n]|(\*+([^*)]|[\r\n])))*\*+\)", @"\(\*([^*]|(\*+([^*)])))*\*+\)"),

        // <!-- --> — XML и HTML: перевод строки входит в [^\-].
        (@"(--([^\-]|[\r\n]|-[^\-])*--", @"(--([^\-]|-[^\-])*--"),

        // {- -} — Haskell: перевод строки входит в [^-].
        (@"{-(?:[^-]|-(?!})|[\r\n])*-}", @"{-(?:[^-]|-(?!}))*-}"),

        // Блок внутри markdown: пробел, табуляция и \r входят и в класс, и в точку; без класса
        // остаётся «перевод строки или любой другой символ» — то же самое одним способом.
        (@"((?:[ \t\r\n]|.)*?)", @"((?:\n|.)*?)"),

        // Значение атрибута в теге HTML, ASP.NET и XML: ленивое [^\n]+? могло дотянуться через
        // кавычку до следующего атрибута, и у незакрытого тега каждое значение читалось
        // всеми длинами сразу. Значение — до своей кавычки; пустое ("") теперь тоже значение,
        // прежде оно склеивалось со следующим атрибутом.
        (@"(""[^\n]+?"")", @"(""[^\n""]*"")"),
        (@"('[^\n]+?')", @"('[^\n']*')"),
        (@"("")([^\n]+?)("")", @"("")([^\n""]*)("")"),
        (@"(')([^\n]+?)(')", @"(')([^\n']*)(')"),

        // Строка в тройных кавычках F#: при (?s) точка и так берёт кавычку, а ветка "" давала
        // второй способ прочесть каждую пару.
        (@"""""""(?:""""|.)*?""""""", @""""""".*?"""""""),

        // Строки Koka: «один или больше символов» внутри повтора — классический (a+)*. По символу
        // за повторение — тот же текст одним способом.
        (@"(?:[^\t\n\\']+|", @"(?:[^\t\n\\']|"),
        (@"(?:[^\t\n\\""]+|", @"(?:[^\t\n\\""]|"),
        (@"(?:("""")|[^""]+)*", @"(?:("""")|[^""])*")
    ];

    /// <summary>
    /// Какие правила заменены: язык, прежнее правило, новое. Заполняет <see cref="Apply"/>; нужно
    /// тестам — сверить, что новое правило читает обычный код так же, как прежнее.
    /// </summary>
    internal static IReadOnlyList<(string Language, string Original, string Fixed)> Changed { get; private set; } = [];

    /// <summary>
    /// Ставит исправленные копии языков вместо встроенных. Звать один раз и до первой подсветки:
    /// ColorCode запоминает разобранный язык по <c>Id</c>, и разобранный раньше остался бы прежним.
    /// </summary>
    public static void Apply()
    {
        var changed = new List<(string, string, string)>();
        foreach (var language in Languages.All.ToList())
        {
            if (Patch(language, changed) is { } patched)
            {
                Languages.Load(patched);
            }
        }

        Changed = changed;
    }

    /// <summary>Правило с заменами; то же правило, если менять нечего.</summary>
    internal static string Fix(string regex)
    {
        foreach (var (unsafePart, safePart) in Replacements)
        {
            regex = regex.Replace(unsafePart, safePart, StringComparison.Ordinal);
        }

        return regex;
    }

    private static PatchedLanguage? Patch(ILanguage language, List<(string, string, string)> changed)
    {
        var any = false;
        var rules = new List<LanguageRule>(language.Rules.Count);
        foreach (var rule in language.Rules)
        {
            var fixedRegex = Fix(rule.Regex);
            if (!string.Equals(fixedRegex, rule.Regex, StringComparison.Ordinal))
            {
                any = true;
                changed.Add((language.Id, rule.Regex, fixedRegex));
            }

            rules.Add(new LanguageRule(fixedRegex, rule.Captures));
        }

        return any ? new PatchedLanguage(language, rules) : null;
    }

    /// <summary>Встроенный язык с исправленными правилами; остальное — от него самого.</summary>
    private sealed class PatchedLanguage(ILanguage inner, IList<LanguageRule> rules) : ILanguage
    {
        public string Id => inner.Id;

        public string FirstLinePattern => inner.FirstLinePattern;

        public string Name => inner.Name;

        public IList<LanguageRule> Rules => rules;

        public string CssClassName => inner.CssClassName;

        public bool HasAlias(string lang) => inner.HasAlias(lang);
    }
}
