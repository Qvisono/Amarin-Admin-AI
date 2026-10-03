using System.Globalization;

namespace Amarin.Core;

/// <summary>Как строка движется при обновлении списка.</summary>
internal enum ChatListMotion
{
    /// <summary>Стоит, как стояла, или просто появилась/пропала.</summary>
    None,

    /// <summary>Появилась, потому что раскрыли её папку или архив, — вырастает.</summary>
    Enter,

    /// <summary>Уходит, потому что её папку или архив свернули, — сворачивается и пропадает.</summary>
    Leave
}

/// <summary>
/// Место в обновлённом списке. Строка нового списка (<see cref="NewIndex"/>) берётся из
/// прежнего (<see cref="OldIndex"/>) или строится заново (<see cref="OldIndex"/> = -1); уходящая
/// (<see cref="NewIndex"/> = -1) ещё показывается, пока сворачивается.
/// </summary>
internal readonly record struct ChatListSlot(int NewIndex, int OldIndex, ChatListMotion Motion);

/// <summary>
/// Раскладка, снятая в момент рисования: ключи строк, их штампы, раскрыт ли заголовок и чья
/// вложенная строка.
/// </summary>
/// <remarks>
/// Снимок, а не сами строки: запись описи (<see cref="ChatIndexEntry"/>) хранилище правит на
/// месте, и прежняя раскладка, державшая ту же запись, видела бы уже новое название — сверка
/// сочла бы переименованную строку прежней, и на экране осталось бы старое имя.
/// </remarks>
internal sealed class ChatListShown
{
    public static readonly ChatListShown Empty = new([], [], [], []);

    internal ChatListShown(string[] keys, string[] stamps, bool?[] open, string?[] owners)
    {
        Keys = keys;
        Stamps = stamps;
        Open = open;
        Owners = owners;
    }

    public int Count => Keys.Length;

    internal string[] Keys { get; }

    internal string[] Stamps { get; }

    /// <summary>Раскрыт ли заголовок папки или архива; у прочих строк — null.</summary>
    internal bool?[] Open { get; }

    /// <summary>Ключ заголовка над вложенной строкой; у прочих — null.</summary>
    internal string?[] Owners { get; }
}

/// <summary>
/// Точечное обновление боковой панели: какие строки оставить как есть, какие построить заново и
/// какие из них появляются раскрытием папки или уходят её свёртыванием.
/// </summary>
/// <remarks>
/// <para>
/// До 1.30.0 любое изменение состава — раскрытие папки, переименование, новый чат — сносило
/// панель целиком и строило каждую строку заново: на трёх сотнях чатов щелчок по папке стоил
/// 120 мс работы потока окна. Теперь окно сверяет прежнюю раскладку с новой по ключам и трогает
/// только изменившееся.
/// </para>
/// <para>
/// Строки остаются прямыми детьми панели, как и прежде, — на этом держатся выбор Shift-щелчком,
/// Ctrl+Tab и перетаскивание (см. <c>ChatRowState</c>). Положение в карточке папки, раздел для
/// броска и признаки строки (открыт, думает, выбран) ставятся на месте при каждом обновлении,
/// поэтому в штамп строки не входят — как и то, свёрнута ли папка: её заголовок переключается
/// на месте, и шеврон успевает повернуться.
/// </para>
/// </remarks>
internal static class ChatListPatch
{
    /// <summary>Ключ строки: одна и та же строка в двух раскладках — один ключ.</summary>
    public static string KeyOf(ChatListNode node) => node switch
    {
        ChatListGroup group => "g:" + group.TitleKey,
        ChatListFolder folder => "f:" + folder.Folder.Id,
        ChatListArchive => "a",
        ChatListChat chat => "c:" + chat.Entry.Id,
        _ => "?"
    };

    /// <summary>Всё, из чего строка строится: совпал — строку можно оставить как есть.</summary>
    public static string StampOf(ChatListNode node) => node switch
    {
        ChatListGroup group => group.TitleKey,
        ChatListFolder folder => folder.Folder.Name + "\u001f" + folder.Count.ToString(CultureInfo.InvariantCulture),
        ChatListArchive archive => archive.Count.ToString(CultureInfo.InvariantCulture),
        ChatListChat chat => string.Join(
            '\u001f',
            chat.Entry.Title,
            chat.Entry.UpdatedAt.Ticks.ToString(CultureInfo.InvariantCulture),
            chat.Entry.IsPinned ? "1" : "0",
            chat.Entry.TotalCost.ToString(CultureInfo.InvariantCulture),
            string.Join(',', chat.Tags.Select(tag => tag.Id + "=" + tag.Color))),
        _ => ""
    };

    /// <summary>Раскрыт ли заголовок папки или архива; у прочих строк — null.</summary>
    public static bool? IsOpen(ChatListNode node) => node switch
    {
        ChatListFolder folder => !folder.Folder.Collapsed,
        ChatListArchive archive => archive.Expanded,
        _ => null
    };

    /// <summary>Снимок раскладки — его окно хранит до следующего обновления.</summary>
    public static ChatListShown Snapshot(IReadOnlyList<ChatListNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var keys = new string[nodes.Count];
        var stamps = new string[nodes.Count];
        var open = new bool?[nodes.Count];
        var owners = new string?[nodes.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? owner = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            // Повтор ключа (такого не должно быть) получает номер, чтобы две строки не слились.
            var key = KeyOf(nodes[i]);
            for (var n = 2; !seen.Add(key); n++)
            {
                key = KeyOf(nodes[i]) + "#" + n.ToString(CultureInfo.InvariantCulture);
            }

            keys[i] = key;
            stamps[i] = StampOf(nodes[i]);
            open[i] = IsOpen(nodes[i]);
            switch (nodes[i])
            {
                case ChatListFolder or ChatListArchive:
                    owner = key;
                    break;
                case ChatListChat { Nested: true }:
                    owners[i] = owner;
                    break;
                default:
                    owner = null;
                    break;
            }
        }

        return new ChatListShown(keys, stamps, open, owners);
    }

    /// <summary>
    /// Сверяет прежнюю раскладку с новой.
    /// </summary>
    /// <returns>
    /// Места нового списка по порядку; уходящие строки стоят сразу за заголовком своей папки,
    /// как стояли и раньше. Строки прежнего списка, которых здесь нет, просто убираются.
    /// </returns>
    public static IReadOnlyList<ChatListSlot> Plan(ChatListShown before, ChatListShown after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var oldAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var oldOpen = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < before.Count; i++)
        {
            oldAt[before.Keys[i]] = i;
            if (before.Open[i] is { } open)
            {
                oldOpen[before.Keys[i]] = open;
            }
        }

        var newAt = new HashSet<string>(after.Keys, StringComparer.Ordinal);
        var newOpen = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var i = 0; i < after.Count; i++)
        {
            if (after.Open[i] is { } open)
            {
                newOpen[after.Keys[i]] = open;
            }
        }

        var used = new bool[before.Count];
        var slots = new List<ChatListSlot>(after.Count);
        var headerSlot = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < after.Count; i++)
        {
            var old = oldAt.TryGetValue(after.Keys[i], out var at) && !used[at] && before.Stamps[at] == after.Stamps[i]
                ? at
                : -1;
            if (old >= 0)
            {
                used[old] = true;
            }

            // Раскрытие: строки папки, которой прежде не было видно ни одной, — потому что папка
            // была свёрнута, а не потому что чат переехал сюда из другого раздела.
            var motion = ChatListMotion.None;
            if (old < 0 && !oldAt.ContainsKey(after.Keys[i]) &&
                after.Owners[i] is { } owner && oldOpen.TryGetValue(owner, out var wasOpen) && !wasOpen)
            {
                motion = ChatListMotion.Enter;
            }

            if (after.Open[i] is not null)
            {
                headerSlot[after.Keys[i]] = slots.Count;
            }

            slots.Add(new ChatListSlot(i, old, motion));
        }

        // Свёртывание: строки, которые пропали вместе со своей папкой, а не переехали.
        var leaving = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < before.Count; j++)
        {
            if (used[j] || newAt.Contains(before.Keys[j]) || before.Owners[j] is not { } owner ||
                !newOpen.TryGetValue(owner, out var open) || open)
            {
                continue;
            }

            if (!leaving.TryGetValue(owner, out var rows))
            {
                leaving[owner] = rows = [];
            }

            rows.Add(j);
        }

        // Вставляем с конца, чтобы номера мест ещё не вставленных заголовков не съезжали.
        foreach (var (owner, rows) in leaving.OrderByDescending(pair => headerSlot.GetValueOrDefault(pair.Key, -1)))
        {
            if (headerSlot.TryGetValue(owner, out var at))
            {
                slots.InsertRange(at + 1, rows.Select(row => new ChatListSlot(-1, row, ChatListMotion.Leave)));
            }
        }

        return slots;
    }
}
