using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Убирает из отправляемой истории то, что устарело: прежние версии файлов, тексты, которые уже
/// лежат на диске, и документы, прочитанные несколько вопросов назад.
/// </summary>
/// <remarks>
/// <para>
/// В длинном чате о файле модель видит его пять версий: три чтения, две записи. Читая историю, она
/// цепляется за старую — правит то, чего в файле уже нет, или возвращает убранное. Поэтому
/// результат <c>read_file</c>, после которого файл меняли или перечитали то же место, заменяется
/// заглушкой «прочитай заново»; длинные тексты в старых вызовах записи — началом и пометкой, что
/// текст в файле; документы старых сообщений — ссылкой на путь; прежние версии блока кода, когда
/// ниже есть две новее, — пометкой.
/// </para>
/// <para>
/// Правится только копия для запроса: хранимая история (<c>session.ApiMessages</c>), отпечаток
/// сжатия и экспорт не меняются ни на байт. Последние два хода человека не трогаются — над ними
/// модель работает прямо сейчас. Всё детерминировано: одна и та же история даёт один и тот же
/// запрос, и кэш провайдера ломается не чаще, чем сообщение выходит из защищённой части.
/// </para>
/// </remarks>
internal static partial class ContextHygiene
{
    /// <summary>Строка аргумента длиннее этого в старом вызове сокращается.</summary>
    internal const int LongArgument = 1_500;

    /// <summary>Сколько начала длинного аргумента оставить.</summary>
    internal const int ArgumentHead = 400;

    /// <summary>Результат чтения короче этого не заменяется: заглушка была бы не короче.</summary>
    private const int ShortResult = 400;

    /// <summary>Блок кода короче этого не считается версией: маленькие блоки дешевле оставить.</summary>
    private const int VersionedBlock = 200;

    /// <summary>Сворачивать ли прежние версии блоков кода. Самое смелое из правил — отдельной ручкой.</summary>
    internal const bool CollapseCodeVersions = true;

    private static readonly HashSet<string> Writers = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file", "edit_file", "edit_document", "create_document", "save_image"
    };

    /// <summary>Чистит отправляемую копию истории на месте: заменяет элементы списка новыми сообщениями.</summary>
    public static void Apply(List<ChatMessage> outgoing)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        var turns = ContextCompaction.HumanTurns(outgoing);
        if (turns.Count <= ContextCompaction.KeepTurns)
        {
            return;
        }

        var firstProtected = turns[^ContextCompaction.KeepTurns];
        var calls = CallsById(outgoing);
        CollapseSupersededReads(outgoing, calls, firstProtected);
        TrimLongArguments(outgoing, firstProtected);
        CollapseOldDocuments(outgoing, firstProtected);
        if (CollapseCodeVersions)
        {
            CollapseOldCodeVersions(outgoing, firstProtected);
        }
    }

    private sealed record CallInfo(int Index, string Name, JsonElement Arguments);

    private static Dictionary<string, CallInfo> CallsById(List<ChatMessage> messages)
    {
        var calls = new Dictionary<string, CallInfo>(StringComparer.Ordinal);
        for (var i = 0; i < messages.Count; i++)
        {
            foreach (var call in messages[i].ToolCalls ?? [])
            {
                calls.TryAdd(call.Id, new CallInfo(i, call.Function.Name, ParseOrEmpty(call.Function.Arguments)));
            }
        }

        return calls;
    }

    // ───────────────────────── чтения, которые устарели ─────────────────────────

    private static void CollapseSupersededReads(List<ChatMessage> messages, Dictionary<string, CallInfo> calls, int firstProtected)
    {
        var ordered = calls.Values.OrderBy(call => call.Index).ToList();
        for (var i = 0; i < firstProtected; i++)
        {
            var message = messages[i];
            if (message.Role != "tool" || message.ToolCallId is not { } id ||
                !calls.TryGetValue(id, out var read) ||
                !read.Name.Equals("read_file", StringComparison.OrdinalIgnoreCase) ||
                PathOf(read.Arguments, "path") is not { } path ||
                (ChatContent.ReadText(message.Content)?.Length ?? 0) < ShortResult)
            {
                continue;
            }

            var window = WindowOf(read.Arguments);
            var superseded = ordered.Any(later =>
                later.Index > read.Index &&
                (Changes(later, path) ||
                 (later.Name.Equals("read_file", StringComparison.OrdinalIgnoreCase) &&
                  PathOf(later.Arguments, "path") == path && WindowOf(later.Arguments) == window)));
            if (!superseded)
            {
                continue;
            }

            messages[i] = Copy(message, content: ChatContent.Text(
                $"[read_file {path}: this result is out of date - the file was changed or read again later in the conversation. Read it again with read_file if you need its current text.]"));
        }
    }

    /// <summary>Меняет ли вызов файл по этому пути — записью, правкой или результатом save_as.</summary>
    private static bool Changes(CallInfo call, string path) =>
        Writers.Contains(call.Name) &&
        (PathOf(call.Arguments, "path") == path || PathOf(call.Arguments, "save_as") == path);

    /// <summary>Путь так, как его раскроет инструмент, — без этого «отчёт.docx» и полный путь были бы разными файлами.</summary>
    private static string? PathOf(JsonElement arguments, string field)
    {
        var raw = FileToolPaths.String(arguments, field);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (FileToolPaths.IsAttachmentHandle(raw))
        {
            return raw.Trim();
        }

        return FileToolPaths.TryResolve(raw, out var resolved, out _) ? resolved.ToLowerInvariant() : null;
    }

    private static string WindowOf(JsonElement arguments) =>
        string.Join("|",
            (FileToolPaths.Int(arguments, "offset") ?? 1).ToString(CultureInfo.InvariantCulture),
            (FileToolPaths.Int(arguments, "limit") ?? 0).ToString(CultureInfo.InvariantCulture),
            FileToolPaths.String(arguments, "sheet") ?? "",
            FileToolPaths.String(arguments, "range") ?? "",
            (FileToolPaths.Int(arguments, "part") ?? 1).ToString(CultureInfo.InvariantCulture));

    // ───────────────────────── длинные аргументы старых вызовов ─────────────────────────

    private static void TrimLongArguments(List<ChatMessage> messages, int firstProtected)
    {
        for (var i = 0; i < firstProtected; i++)
        {
            var message = messages[i];
            if (message.ToolCalls is not { Count: > 0 } toolCalls ||
                !toolCalls.Any(call => call.Function.Arguments.Length > LongArgument))
            {
                continue;
            }

            var changed = false;
            var rebuilt = new List<ToolCall>(toolCalls.Count);
            foreach (var call in toolCalls)
            {
                if (TrimArguments(call.Function.Arguments) is { } trimmed)
                {
                    changed = true;
                    rebuilt.Add(new ToolCall
                    {
                        Id = call.Id,
                        Type = call.Type,
                        Function = new FunctionCall { Name = call.Function.Name, Arguments = trimmed }
                    });
                }
                else
                {
                    rebuilt.Add(call);
                }
            }

            if (changed)
            {
                messages[i] = Copy(message, toolCalls: rebuilt);
            }
        }
    }

    /// <summary>Аргументы с сокращёнными длинными строками; null — сокращать нечего или не разобрались.</summary>
    internal static string? TrimArguments(string arguments)
    {
        if (arguments.Length <= LongArgument)
        {
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(ToolArguments.Parse(arguments).GetRawText());
        }
        catch (JsonException)
        {
            return null;
        }

        return root is not null && Trim(root) ? root.ToJsonString() : null;
    }

    private static bool Trim(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > LongArgument)
                    {
                        obj[key] = Shorten(text);
                        changed = true;
                    }
                    else if (child is not null)
                    {
                        changed |= Trim(child);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > LongArgument)
                    {
                        array[i] = Shorten(text);
                        changed = true;
                    }
                    else if (array[i] is { } child)
                    {
                        changed |= Trim(child);
                    }
                }

                break;
        }

        return changed;
    }

    private static string Shorten(string text) =>
        text[..ArgumentHead] + $"… [{(text.Length - ArgumentHead).ToString(CultureInfo.InvariantCulture)} more chars omitted from this old call; the text itself is in the file]";

    // ───────────────────────── документы старых сообщений ─────────────────────────

    private static void CollapseOldDocuments(List<ChatMessage> messages, int firstProtected)
    {
        for (var i = 0; i < firstProtected; i++)
        {
            var message = messages[i];
            if (message.Role != "user" || message.Content is not { } content)
            {
                continue;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                if (CollapseDocuments(content.GetString()) is { } text)
                {
                    messages[i] = Copy(message, content: ChatContent.Text(text));
                }

                continue;
            }

            if (content.ValueKind != JsonValueKind.Array || JsonNode.Parse(content.GetRawText()) is not JsonArray parts)
            {
                continue;
            }

            var changed = false;
            foreach (var part in parts.OfType<JsonObject>())
            {
                if (part["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "text" &&
                    part["text"] is JsonValue value && value.TryGetValue<string>(out var text) &&
                    CollapseDocuments(text) is { } collapsed)
                {
                    part["text"] = collapsed;
                    changed = true;
                }
            }

            if (changed)
            {
                messages[i] = Copy(message, content: JsonSerializer.SerializeToElement(parts));
            }
        }
    }

    /// <summary>Текст, где тела блоков <c>&lt;document&gt;</c> заменены ссылкой; null — блоков нет.</summary>
    internal static string? CollapseDocuments(string? text)
    {
        if (text is null || !text.Contains(DocumentDigest.OpenTag, StringComparison.Ordinal))
        {
            return null;
        }

        var collapsed = DocumentBlock().Replace(text, match =>
            DocumentDigest.OpenTag + match.Groups["attributes"].Value +
            ">[shown with an earlier message; read_file with this path to see it again]" + DocumentDigest.CloseTag);
        return string.Equals(collapsed, text, StringComparison.Ordinal) ? null : collapsed;
    }

    [GeneratedRegex(@"<document (?<attributes>[^>\n]*)>.*?</document>", RegexOptions.Singleline)]
    private static partial Regex DocumentBlock();

    // ───────────────────────── прежние версии блоков кода ─────────────────────────

    private static void CollapseOldCodeVersions(List<ChatMessage> messages, int firstProtected)
    {
        var blocks = new List<(int Message, Match Block, HashSet<string> Lines, string Language)>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role != "assistant" || messages[i].Content is not { ValueKind: JsonValueKind.String } content)
            {
                continue;
            }

            foreach (Match match in CodeBlock().Matches(content.GetString() ?? ""))
            {
                if (match.Groups["body"].Length >= VersionedBlock)
                {
                    blocks.Add((i, match, LinesOf(match.Groups["body"].Value), match.Groups["language"].Value.Trim().ToLowerInvariant()));
                }
            }
        }

        var collapse = new Dictionary<int, List<Match>>();
        foreach (var block in blocks.Where(block => block.Message < firstProtected))
        {
            var newer = blocks.Count(other =>
                other.Message > block.Message &&
                other.Language == block.Language &&
                Similar(block.Lines, other.Lines));
            if (newer < 2)
            {
                continue;
            }

            if (!collapse.TryGetValue(block.Message, out var list))
            {
                list = [];
                collapse[block.Message] = list;
            }

            list.Add(block.Block);
        }

        foreach (var (index, matches) in collapse)
        {
            var text = ChatContent.ReadText(messages[index].Content) ?? "";
            var updated = new StringBuilder();
            var at = 0;
            foreach (var match in matches.OrderBy(match => match.Index))
            {
                updated.Append(text, at, match.Index - at)
                    .Append("```").Append(match.Groups["language"].Value).Append('\n')
                    .Append("[earlier version of this block - newer versions follow later in the conversation; work from the latest one]\n")
                    .Append("```");
                at = match.Index + match.Length;
            }

            updated.Append(text, at, text.Length - at);
            messages[index] = Copy(messages[index], content: ChatContent.Text(updated.ToString()));
        }
    }

    /// <summary>Две версии одного блока: больше половины строк общие.</summary>
    private static bool Similar(HashSet<string> first, HashSet<string> second)
    {
        if (first.Count == 0 || second.Count == 0)
        {
            return false;
        }

        var common = first.Count(second.Contains);
        return common * 2 > Math.Max(first.Count, second.Count);
    }

    private static HashSet<string> LinesOf(string body) =>
        body.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"```(?<language>[^\n`]*)\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    // ───────────────────────── общее ─────────────────────────

    private static JsonElement ParseOrEmpty(string? arguments)
    {
        try
        {
            return ToolArguments.Parse(arguments);
        }
        catch (JsonException)
        {
            return ToolArguments.Empty();
        }
    }

    private static ChatMessage Copy(ChatMessage message, JsonElement? content = null, List<ToolCall>? toolCalls = null) => new()
    {
        Role = message.Role,
        Content = content ?? message.Content,
        ReasoningContent = message.ReasoningContent,
        ToolCalls = toolCalls ?? message.ToolCalls,
        ToolCallId = message.ToolCallId,
        Annotations = message.Annotations,
        Name = message.Name
    };
}
