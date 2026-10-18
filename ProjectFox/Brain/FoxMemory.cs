using System.Text.RegularExpressions;

namespace Amarin.Fox;

/// <summary>
/// Память Розы для общения: последние разговоры с ним и заметки о человеке, которые он сам решил
/// запомнить. Лежит в <see cref="FoxState"/>, то есть в том же <c>fox/state.json</c>: архив,
/// шифрование и «Удалить все данные» её уже покрывают.
/// </summary>
/// <remarks>
/// Правила чистые и проверяются тестами: сколько помнить, что вытеснять, чего не помнить никогда.
/// Секрет не сохраняется вовсе — длинный токен, ряд цифр, слово о пароле: заметку пишет модель, и
/// попроси её человек «запомни мой пароль», он лёг бы на диск открытым текстом при выключенном
/// шифровании переписки.
/// </remarks>
internal static partial class FoxMemory
{
    /// <summary>
    /// Сколько разговоров помнит. С 1.36.0 их видно в переписке, поэтому полсотни, а не дюжина;
    /// модели по-прежнему уходят только последние (<see cref="ExchangesInTalk"/>).
    /// </summary>
    public const int MaxExchanges = 50;

    /// <summary>Сколько последних разговоров уходит модели в новом разговоре.</summary>
    public const int ExchangesInTalk = 6;

    /// <summary>Сколько заметок о человеке помнит.</summary>
    public const int MaxNotes = 24;

    public const int HumanLimit = 300;
    public const int RosaLimit = 200;
    public const int NoteLimit = 120;

    /// <summary>Разговор состоялся: слова человека и ответ Розы. Старые вытесняются.</summary>
    public static void AddExchange(FoxState state, string human, string rosa, DateTime now)
    {
        human = Cut(human.Trim(), HumanLimit);
        rosa = Cut(rosa.Trim(), RosaLimit);
        if (human.Length == 0 || rosa.Length == 0)
        {
            return;
        }

        state.Conversation.Add(new FoxExchange { Human = human, Rosa = rosa, At = now });
        if (state.Conversation.Count > MaxExchanges)
        {
            state.Conversation.RemoveRange(0, state.Conversation.Count - MaxExchanges);
        }
    }

    /// <summary>
    /// Запоминает заметку. <c>false</c> — не запомнил: пусто, секрет или уже помнит то же. Заметка,
    /// которая шире прежней, заменяет её, а не лежит рядом.
    /// </summary>
    public static bool Remember(FoxState state, string? note, DateTime now)
    {
        var text = Cut(Regex.Replace(note ?? "", @"\s+", " ").Trim(), NoteLimit);
        if (text.Length < 3 || LooksSecret(text))
        {
            return false;
        }

        var key = Key(text);
        if (state.Notes.Exists(existing => Key(existing.Text).Contains(key, StringComparison.Ordinal)))
        {
            return false;
        }

        state.Notes.RemoveAll(existing => key.Contains(Key(existing.Text), StringComparison.Ordinal));
        state.Notes.Add(new FoxNote { Text = text, At = now });
        if (state.Notes.Count > MaxNotes)
        {
            state.Notes.RemoveRange(0, state.Notes.Count - MaxNotes);
        }

        return true;
    }

    /// <summary>«Забыть» в «Нашей дружбе»: и заметки, и разговоры.</summary>
    public static void Forget(FoxState state)
    {
        state.Notes.Clear();
        state.Conversation.Clear();
    }

    /// <summary>Похоже на секрет: ключ или токен, номер карты или телефона, слово о пароле.</summary>
    internal static bool LooksSecret(string text) =>
        LongToken().IsMatch(text) || DigitRun().IsMatch(text) || SecretWord().IsMatch(text);

    /// <summary>Сравнение без регистра, знаков и лишних пробелов: «Любит чай.» и «любит чай» — одно.</summary>
    private static string Key(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static string Cut(string text, int limit) =>
        text.Length <= limit ? text : text[..(limit - 1)].TrimEnd() + "…";

    /// <summary>Слово без пробелов длиннее двадцати знаков, где есть и буквы, и цифры, — ключ, токен, хеш.</summary>
    [GeneratedRegex(@"(?=\S*\d)(?=\S*[A-Za-z])[\w\-+/=]{20,}")]
    private static partial Regex LongToken();

    /// <summary>Девять и больше цифр подряд, можно через пробел или дефис: карта, телефон, документ.</summary>
    [GeneratedRegex(@"\d(?:[ \-]?\d){8,}")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"парол|password|passwd|пин-?код|\bpin\b|cvv|cvc|секретн\w* (?:слов|фраз|код)|seed", RegexOptions.IgnoreCase)]
    private static partial Regex SecretWord();
}
