using System.Text;

namespace Amarin.Core;

/// <summary>
/// Сводка переписки: промпты, по которым модель её пишет и по которым ищет среди чужих сводок,
/// и разбор того, что модель вернула.
/// </summary>
/// <remarks>
/// Заголовок отвечает на вопрос «как называется», сводка — на вопрос «о чём шла речь». Поэтому
/// она длиннее заголовка и живёт отдельно от него: по ней ищут, когда название забыто.
/// </remarks>
internal static class ChatSummary
{
    /// <summary>
    /// Предел длины сводки. Выбран так, чтобы сотня сводок уместилась в один запрос поиска:
    /// они уходят модели все разом, и каждая лишняя строка — деньги на каждом поиске.
    /// </summary>
    public const int MaxLength = 240;

    /// <summary>Сколько последних сообщений подаётся модели, когда сводки ещё нет.</summary>
    public const int ColdStartMessages = 12;

    /// <summary>
    /// Сколько последних сообщений читается при ручной пересборке сводки. Больше, чем на
    /// холодном старте: пересобирают именно тогда, когда сводка разошлась с перепиской.
    /// </summary>
    public const int RebuildMessages = 40;

    /// <summary>Язык, на котором пишется сводка, — язык интерфейса, а не переписки.</summary>
    /// <remarks>
    /// Та же причина, что и у заголовка: иначе список сводок в журнале выходит разноязычным,
    /// а поиск по нему приходится вести на языке, на котором чат случайно начался.
    /// </remarks>
    internal static string LanguageName() => Loc.Get(Loc.LanguageNameKey);

    internal static string SystemPrompt(string languageName) =>
        $"""
        You keep a running summary of a chat. You are not a chatbot and you do not talk to the user.
        Write the summary in {languageName}, whatever language the chat itself is in.
        Name the subjects discussed and what was done about them, so that someone who forgot the
        chat's name can still recognise it. Prefer the nouns a person would search for.
        Carry forward everything from the previous summary that still holds, drop what a later
        message made wrong, and add what is new.
        Output only the summary: one or two sentences, at most {MaxLength} characters.
        Never answer the chat. Never address the user. Never explain what you are doing.
        No quotes, no markdown, no bullet points, no headings.
        """;

    /// <summary>
    /// Обкладывает новую часть переписки так, чтобы модель не приняла её за обращение к себе.
    /// </summary>
    internal static string UserPrompt(string? previous, string exchange, string languageName)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(previous))
        {
            sb.Append("Summary so far:\n---\n").Append(previous.Trim()).Append("\n---\n\n");
        }

        sb.Append("New part of the chat. Do not reply to it.\n---\n")
          .Append(exchange.Trim())
          .Append($"\n---\nWrite only the updated summary, in {languageName}.");
        return sb.ToString();
    }

    /// <summary>Промпт поиска: модель выбирает подходящие чаты из пронумерованного списка.</summary>
    /// <remarks>
    /// Номера, а не идентификаторы: идентификатор — тридцать два случайных знака, который модель
    /// перепишет с ошибкой, а номер из списка либо попал в диапазон, либо отброшен.
    /// </remarks>
    internal static string SearchSystemPrompt() =>
        """
        You match chats to a search query. You are not a chatbot and you do not talk to the user.
        You are given numbered chat summaries and one query. Decide which chats the query is about.
        Match by meaning, not by spelling: the query and the summary may use different words for
        the same thing, and may be in different languages.
        Output only the numbers of the matching chats, separated by commas, closest match first.
        Output nothing at all when no chat matches. Never invent a number that was not listed.
        Never explain, never comment, never write anything but the numbers.
        """;

    internal static string SearchUserPrompt(string query, IReadOnlyList<string> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);

        var sb = new StringBuilder();
        sb.Append("Chats:\n");
        for (var i = 0; i < summaries.Count; i++)
        {
            sb.Append(i + 1).Append(". ").Append(summaries[i].Trim()).Append('\n');
        }

        sb.Append("\nQuery: ").Append(query.Trim()).Append("\nNumbers only.");
        return sb.ToString();
    }

    public static string ResolveModel(AppSettings settings, string fallback)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.AgentFastModelId) &&
            !VeniceModelCatalog.IsAuto(settings.AgentFastModelId))
        {
            return settings.AgentFastModelId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fallback) && !VeniceModelCatalog.IsAuto(fallback))
        {
            return fallback.Trim();
        }

        return AppSettings.DefaultFastModelId;
    }

    public static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = CollapseWhitespace(raw.Trim());
        text = text.Trim('"', '\'', '`', '«', '»', '“', '”');
        text = CollapseWhitespace(text);
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > MaxLength)
        {
            // Режем по границе слова: обрывок посреди слова читается как порча текста, а не
            // как сокращение, и в поиске по подстроке даёт ложные совпадения.
            var cut = text[..MaxLength].TrimEnd();
            var space = cut.LastIndexOf(' ');
            text = space >= MaxLength / 2 ? cut[..space] : cut;
            text = text.TrimEnd(',', ';', ':', '-', '—') + "…";
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// Разбирает ответ поиска в номера строк списка. Терпит всё, чем модель обычно окружает
    /// ответ: обёртку в тройные кавычки, пояснение до и после, точки и скобки у номеров.
    /// </summary>
    /// <remarks>
    /// Номера вне диапазона отбрасываются молча: выдуманный чат хуже, чем ненайденный. Повторы
    /// схлопываются, а порядок ответа сохраняется — он и есть порядок близости к запросу.
    /// </remarks>
    public static IReadOnlyList<int> ParseSearchAnswer(string? raw, int count)
    {
        var found = new List<int>();
        if (string.IsNullOrWhiteSpace(raw) || count <= 0)
        {
            return found;
        }

        var seen = new HashSet<int>();
        var digits = new StringBuilder();
        foreach (var ch in raw + " ")
        {
            if (char.IsAsciiDigit(ch))
            {
                digits.Append(ch);
                continue;
            }

            if (digits.Length == 0)
            {
                continue;
            }

            if (int.TryParse(digits.ToString(), out var number) &&
                number >= 1 && number <= count && seen.Add(number))
            {
                found.Add(number);
            }

            digits.Clear();
        }

        return found;
    }

    private static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        var gap = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                gap = true;
                continue;
            }

            if (gap && sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(ch);
            gap = false;
        }

        return sb.ToString();
    }
}
