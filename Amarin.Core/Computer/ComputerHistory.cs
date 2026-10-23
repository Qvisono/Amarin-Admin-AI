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

    /// <summary>Начало выдачи страницы инструментом <c>browser</c>.</summary>
    public const string PageMark = "[page] ";

    public static List<ChatMessage> Trim(IReadOnlyList<ChatMessage> messages, int keepImages = KeepImages)
    {
        var result = new List<ChatMessage>(messages);

        var imagesSeen = 0;
        var treesSeen = new HashSet<string>(StringComparer.Ordinal);
        var pageSeen = false;
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

            // Страница браузера (1.37.0) — та же логика: модели нужна последняя, а прежние длинны и
            // держат устаревшие id.
            if (PageStart(message) is { } page)
            {
                if (pageSeen)
                {
                    result[i] = WithText(message, page.Text[..page.Start] + PageStub);
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
