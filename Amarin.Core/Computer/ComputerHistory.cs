using System.Text.Json;
using Amarin.Tools;

namespace Amarin.Core;

/// <summary>
/// Что из истории агента с мышью уходит модели: последний снимок и последнее дерево каждого окна,
/// а прежние — заглушкой.
/// </summary>
/// <remarks>
/// <para>
/// Сто шагов со снимком на каждом не влезли бы ни в окно модели, ни в счёт: снимок стоит
/// дороже десятка деревьев, а нужен из них последний — модель решает по тому, что на экране
/// сейчас. Старое дерево окна ещё и вредно: его id уже не действуют, и модель нажимала бы по ним.
/// </para>
/// <para>
/// Правит только копию для запроса, и правило зависит лишь от самих сообщений — так одна и та же
/// история всегда даёт один и тот же запрос, и кэш провайдера не сбивается без причины. Пары
/// «вызов — ответ» не рвутся: у ответа инструмента меняется текст, а не место.
/// </para>
/// </remarks>
internal static class ComputerHistory
{
    /// <summary>Сколько последних снимков модель видит картинкой.</summary>
    /// <remarks>
    /// Один: дальше по пути запрос проходит <see cref="ApiContextLimiter"/>, и он всё равно оставляет
    /// вложения только последнего сообщения с ними. Оставь здесь два — модель увидела бы одну
    /// картинку и чужую заглушку вместо второй, не зная, что её убрали нарочно.
    /// </remarks>
    public const int KeepImages = 1;

    public const string ImageStub = "[An earlier screenshot was removed from the history. Take a new one if you need to see the screen.]";

    public const string TreeStub = "[An earlier element tree of this window was removed: its ids are no longer valid.]";

    /// <summary>Прежнее чтение страницы браузером агента (1.37.0): его id тоже уже не действуют.</summary>
    public const string PageStub = "[An earlier read of the web page was removed: its element ids are no longer valid.]";

    /// <summary>Прежнее чтение страницы, которую потом прочитали снова: свежее — ниже.</summary>
    public const string PageRereadStub = "[An earlier read of this page; a later read of it is below.]";

    /// <summary>
    /// Сколько прежних страниц (разных адресов) остаются в истории кратко — текстом и ссылками.
    /// </summary>
    /// <remarks>
    /// С запасом на работу «список — пункт — список»: агенту нужны и список, и его пункты (в чате с
    /// тестами курса — список, шесть тестов и журнал оценок). Дальше — заголовок с адресом и заглушка:
    /// полтысячи раундов текстом страниц не влезли бы в окно.
    /// </remarks>
    public const int KeepPageDigests = 10;

    /// <summary>Сколько знаков текста и сколько ссылок у прежней страницы.</summary>
    public const int DigestText = 3000;

    public const int DigestLinks = 40;

    /// <summary>Начало выдачи страницы инструментом <c>browser</c>.</summary>
    public const string PageMark = "[page] ";

    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int keepImages = KeepImages)
    {
        var result = new List<ChatMessage>(messages);

        var imagesSeen = 0;
        var treesSeen = new HashSet<string>(StringComparer.Ordinal);
        var pageSeen = false;
        var pagesRead = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var digests = 0;
        var unseenFrom = result.FindLastIndex(message => message.Role.Equals("assistant", StringComparison.Ordinal)) + 1;
        for (var i = result.Count - 1; i >= 0; i--)
        {
            var message = result[i];
            if (HasImage(message))
            {
                if (++imagesSeen > keepImages)
                {
                    result[i] = WithText(message, ImageStub);
                }

                continue;
            }

            // Строка до дерева («нажато», «напечатано») остаётся: модели важно, что действие было.
            if (Tree(message) is { } tree && !treesSeen.Add(tree.Window))
            {
                result[i] = WithText(message, tree.Text[..tree.Start] + TreeStub);
                continue;
            }

            // Страница браузера: последняя — целиком, прежние — кратко, текстом и ссылками без id
            // (1.37.0-beta.8). Прежде их заменяла заглушка, и агент, забыв прочитанное, открывал те же
            // страницы по кругу. Прежнее чтение адреса, прочитанного позже снова, — заглушка: свежее ниже.
            // Страницы, которых модель ещё не видела (ответы на её последний ход), — целиком все:
            // модель открывает по нескольку страниц за ход, и сокращённые до того, как она их прочла,
            // она открывала снова той же пачкой без конца.
            if (PageStart(message) is { } page)
            {
                var body = page.Text[page.Start..];
                var url = PageUrl(body);
                var unseen = i >= unseenFrom;
                if (pageSeen && (!unseen || (url is not null && pagesRead.Contains(url))))
                {
                    result[i] = WithText(message, page.Text[..page.Start] + Earlier(body, url, pagesRead, ref digests));
                }

                if (url is not null)
                {
                    pagesRead.Add(url);
                }

                pageSeen = true;
            }
        }

        return result;
    }

    /// <summary>Дерево окна в ответе инструмента: чьё оно и где начинается. Null — дерева нет.</summary>
    private static (string Window, int Start, string Text)? Tree(ChatMessage message)
    {
        if (!message.Role.Equals("tool", StringComparison.Ordinal) ||
            !string.Equals(message.Name, ComputerTool.ToolName, StringComparison.Ordinal) ||
            ChatContent.ReadText(message.Content) is not { } text)
        {
            return null;
        }

        var start = text.IndexOf(ComputerTool.TreeMark, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var from = start + ComputerTool.TreeMark.Length;
        var end = text.IndexOf(']', from);
        return end > from ? (text[from..end], start, text) : null;
    }

    /// <summary>
    /// Прежнее чтение страницы: заглушка, если тот же адрес прочитан позже; кратко — у первых
    /// <see cref="KeepPageDigests"/> адресов от конца; дальше — заголовок, адрес и заглушка.
    /// </summary>
    private static string Earlier(string body, string? url, HashSet<string> readLater, ref int digests)
    {
        var header = PageHeader(body);
        if (url is not null && readLater.Contains(url))
        {
            return header + PageRereadStub;
        }

        if (digests < KeepPageDigests && BrowserTool.Digest(body, DigestText, DigestLinks) is { } digest)
        {
            digests++;
            return digest;
        }

        return header + PageStub;
    }

    /// <summary>Первые две строки выдачи: «[page] заголовок» и адрес.</summary>
    private static string PageHeader(string body)
    {
        var first = body.IndexOf('\n', StringComparison.Ordinal);
        var second = first < 0 ? -1 : body.IndexOf('\n', first + 1);
        return second < 0 ? body + "\n" : body[..(second + 1)];
    }

    private static string? PageUrl(string body)
    {
        var lines = body.Split('\n', 3);
        return lines.Length >= 2 && Uri.TryCreate(lines[1].Trim(), UriKind.Absolute, out _) ? lines[1].Trim() : null;
    }

    /// <summary>Выдача страницы в ответе <c>browser</c>: где она начинается. Null — страницы нет.</summary>
    private static (int Start, string Text)? PageStart(ChatMessage message)
    {
        if (!message.Role.Equals("tool", StringComparison.Ordinal) ||
            !string.Equals(message.Name, BrowserTool.ToolName, StringComparison.Ordinal) ||
            ChatContent.ReadText(message.Content) is not { } text)
        {
            return null;
        }

        var start = text.IndexOf(PageMark, StringComparison.Ordinal);
        return start < 0 ? null : (start, text);
    }

    private static bool HasImage(ChatMessage message)
    {
        if (message.Content is not { ValueKind: JsonValueKind.Array } content)
        {
            return false;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object &&
                part.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "image_url")
            {
                return true;
            }
        }

        return false;
    }

    private static ChatMessage WithText(ChatMessage message, string text) => new()
    {
        Role = message.Role,
        Name = message.Name,
        ToolCallId = message.ToolCallId,
        Content = ChatContent.Text(text)
    };
}
