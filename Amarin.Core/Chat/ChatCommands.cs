namespace Amarin.Core;

/// <summary>Команда через «/» из поля ввода, уже разделённая на имя и аргумент.</summary>
internal readonly record struct ChatCommand(string Name, string Argument, string Complexity);

/// <summary>Команда «/» для подсказки и справки (D9): имя, что писать после, что делает.</summary>
/// <param name="ArgumentKey">Ключ подписи аргумента; null — команда без аргумента.</param>
internal sealed record ChatCommandInfo(string Name, string? ArgumentKey, string DescriptionKey);

/// <summary>Команда, которую выполняет само окно, а не модель (D9).</summary>
internal readonly record struct LocalCommand(string Name, string Argument);

/// <summary>
/// Разбирает немногие команды через «/», которые понимает поле ввода. Всё, что не совпало точно, —
/// не команда и уходит модели обычным текстом: пути и код с «/» в начале люди пишут постоянно.
/// </summary>
internal static class ChatCommands
{
    public const string Agent = "agent";
    public const string Heavy = "heavy";
    public const string Lite = "lite";
    public const string Fast = "fast";

    public const string New = "new";
    public const string Model = "model";
    public const string Compact = "compact";
    public const string Export = "export";
    public const string Instruction = "instr";
    public const string Recipe = "recipe";
    public const string ReadOnly = "readonly";

    /// <summary>
    /// Все команды — один список на программу: из него строятся подсказка у поля, справка и
    /// строка о командах в системном промпте чата. Второй список разошёлся бы с ним молча.
    /// </summary>
    public static readonly IReadOnlyList<ChatCommandInfo> All =
    [
        new(New, "S.Command.Arg.Text", "S.Command.New"),
        new(Model, "S.Command.Arg.Name", "S.Command.Model"),
        new(Compact, null, "S.Command.Compact"),
        new(Export, "S.Command.Arg.Format", "S.Command.Export"),
        new(Instruction, "S.Command.Arg.Name", "S.Command.Instr"),
        new(Recipe, "S.Command.Arg.Name", "S.Command.Recipe"),
        new(ReadOnly, null, "S.Command.ReadOnly"),
        new(Agent, "S.Command.Arg.Task", "S.Command.Agent")
    ];

    /// <summary>
    /// Подсказка, пока набирается имя команды: поле начинается с «/», пробела ещё нет. Пустой
    /// список — подсказки нет (в том числе для путей вроде <c>/usr/bin</c>: у них уже есть второй
    /// «/», а у команд его не бывает).
    /// </summary>
    public static IReadOnlyList<ChatCommandInfo> Suggest(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '/' || text.IndexOfAny([' ', '\t', '\r', '\n']) >= 0 ||
            text.IndexOf('/', 1) >= 0)
        {
            return [];
        }

        var typed = text[1..];
        return All.Where(command => command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Команда окна: имя из <see cref="All"/> (кроме агента — его разбирает <see cref="TryParse"/>)
    /// строго целиком. Всё прочее, начинающееся с «/», — обычный текст для модели.
    /// </summary>
    public static LocalCommand? TryParseLocal(string? input)
    {
        var text = input?.Trim() ?? "";
        if (text.Length < 2 || text[0] != '/')
        {
            return null;
        }

        var (verb, rest) = SplitFirstWord(text[1..]);
        var known = All.FirstOrDefault(command =>
            command.Name != Agent && command.Name.Equals(verb, StringComparison.OrdinalIgnoreCase));
        if (known is null)
        {
            return null;
        }

        // У команды без аргумента хвост значит, что это не команда, а фраза: «/compact и что дальше».
        if (known.ArgumentKey is null && rest.Length > 0)
        {
            return null;
        }

        return new LocalCommand(known.Name, rest);
    }

    /// <summary>
    /// Модель для <c>/model &lt;имя&gt;</c>: точное совпадение имени или идентификатора, затем
    /// начало, затем вхождение; из равных — с самым коротким именем (его и имели в виду чаще).
    /// </summary>
    public static string? MatchModel(string? query, IEnumerable<(string Id, string Name)> candidates)
    {
        var needle = query?.Trim() ?? "";
        if (needle.Length == 0)
        {
            return null;
        }

        var list = candidates.ToList();
        bool Is(string value) => value.Equals(needle, StringComparison.OrdinalIgnoreCase);
        bool Starts(string value) => value.StartsWith(needle, StringComparison.OrdinalIgnoreCase);
        bool Has(string value) => value.Contains(needle, StringComparison.OrdinalIgnoreCase);

        foreach (var rule in new Func<(string Id, string Name), bool>[]
                 {
                     item => Is(item.Name) || Is(item.Id) || Is(ModelRef.Bare(item.Id)),
                     item => Starts(item.Name) || Starts(ModelRef.Bare(item.Id)),
                     item => Has(item.Name) || Has(item.Id)
                 })
        {
            var found = list.Where(rule).OrderBy(item => item.Name.Length).ThenBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault();
            if (found.Id is not null)
            {
                return found.Id;
            }
        }

        return null;
    }

    /// <summary>Строка о командах для системного промпта чата — правило, без примеров.</summary>
    public static string PromptBlock() =>
        "The user can type these commands in the input box; the app runs them itself, you never receive them: " +
        string.Join(", ", All.Select(command => "/" + command.Name)) +
        ". Mention one only when it directly solves what the user asks about.";

    /// <summary>
    /// Узнаёт <c>/agent &lt;задача&gt;</c> (по умолчанию тяжёлый уровень), а также <c>/agent lite …</c>,
    /// <c>/agent-lite …</c> и такую же пару для <c>fast</c>. Null — это не команда, в том числе
    /// <c>/agent</c> без задачи: запускать нечего, и это остаётся текстом.
    /// </summary>
    public static ChatCommand? TryParse(string? input)
    {
        var text = input?.TrimStart() ?? "";
        if (text.Length < 2 || text[0] != '/')
        {
            return null;
        }

        var space = text.IndexOfAny([' ', '\t', '\r', '\n']);
        var verb = (space < 0 ? text[1..] : text[1..space]).Trim();
        var rest = (space < 0 ? "" : text[(space + 1)..]).Trim();

        var complexity = Heavy;
        if (verb.Equals($"{Agent}-{Lite}", StringComparison.OrdinalIgnoreCase))
        {
            complexity = Lite;
        }
        else if (verb.Equals($"{Agent}-{Fast}", StringComparison.OrdinalIgnoreCase))
        {
            complexity = Fast;
        }
        else if (verb.Equals($"{Agent}-{Heavy}", StringComparison.OrdinalIgnoreCase))
        {
            complexity = Heavy;
        }
        else if (verb.Equals(Agent, StringComparison.OrdinalIgnoreCase))
        {
            // «/agent lite сделай»: уточнение — уточнение, только если за ним есть задача, иначе
            // «lite» и есть задача.
            var (word, tail) = SplitFirstWord(rest);
            if (tail.Length > 0 &&
                (word.Equals(Lite, StringComparison.OrdinalIgnoreCase) ||
                 word.Equals(Fast, StringComparison.OrdinalIgnoreCase) ||
                 word.Equals(Heavy, StringComparison.OrdinalIgnoreCase)))
            {
                complexity = word.ToLowerInvariant();
                rest = tail;
            }
        }
        else
        {
            return null;
        }

        return rest.Length == 0 ? null : new ChatCommand(Agent, rest, complexity);
    }

    private static (string Word, string Tail) SplitFirstWord(string text)
    {
        var space = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return space < 0
            ? (text, "")
            : (text[..space], text[(space + 1)..].Trim());
    }
}
