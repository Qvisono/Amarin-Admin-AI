using System.Text.Json;

namespace Amarin.Core;

/// <summary>Папка в списке чатов.</summary>
public sealed class ChatFolder
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Collapsed { get; set; }
}

/// <summary>Цветной тег. Цвет — ключ кисти палитры, а не сам цвет: тег обязан меняться с темой.</summary>
public sealed class ChatTag
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Color { get; set; } = ChatOrganizer.TagColors[0];
}

/// <summary>Куда человек положил чат.</summary>
public sealed class ChatPlacement
{
    public string? FolderId { get; set; }

    public List<string> Tags { get; set; } = [];

    public bool Archived { get; set; }

    public bool IsEmpty => FolderId is null && Tags.Count == 0 && !Archived;
}

/// <summary>Как сортировать чаты внутри групп.</summary>
public enum ChatSort
{
    Updated,
    Created,
    Title,
    Cost
}

/// <summary>
/// Папки, теги и архив (D5) — файл <c>chats/organize.json</c>.
/// </summary>
/// <remarks>
/// Отдельным файлом, а не полями описи: опись пересобирается из самих переписок, когда её нет,
/// и раскладка по папкам пропала бы вместе с ней. Здесь же и назначения чатов — по id; чат,
/// которого больше нет, из файла вычищается при удалении.
/// </remarks>
internal sealed class ChatOrganizer
{
    internal const string FileName = "organize.json";

    /// <summary>Кисти палитры, из которых выбирается цвет тега.</summary>
    /// <remarks>
    /// Синий, бирюзовый и фиолетовый — из подсветки кода: в каждой теме это разные оттенки.
    /// Прежде здесь стояли акцент и цвет ссылки, а в тёмных темах оба почти белые — два тега
    /// выходили одного цвета. Теги со старыми цветами так и показываются: ключи ресурсов живы.
    /// </remarks>
    public static readonly string[] TagColors =
        ["Status.Danger", "Status.Warning", "Status.Success", "Code.Type", "Code.Keyword", "Code.Control", "Text.Muted"];

    private readonly Lock _gate = new();
    private string _path;
    private State _state;
    private int _version;

    public ChatOrganizer(string root)
    {
        _path = Path.Combine(root, "chats", FileName);
        _state = Read(_path);
    }

    public sealed class State
    {
        public List<ChatFolder> Folders { get; set; } = [];

        public List<ChatTag> Tags { get; set; } = [];

        public Dictionary<string, ChatPlacement> Chats { get; set; } = [];
    }

    public event Action? Changed;

    /// <summary>
    /// Растёт с каждым изменением раскладки. По нему держатель снимка понимает, что его копия
    /// ещё верна, и не снимает новую: список чатов обновляется по несколько раз в секунду.
    /// </summary>
    public int Version => Volatile.Read(ref _version);

    public void UseRoot(string root)
    {
        lock (_gate)
        {
            _path = Path.Combine(root, "chats", FileName);
            _state = Read(_path);
            _version++;
        }

        Changed?.Invoke();
    }

    /// <summary>Снимок для раскладки: копии, чтобы список собирался без замка.</summary>
    public State Snapshot()
    {
        lock (_gate)
        {
            return new State
            {
                Folders = _state.Folders.Select(folder => new ChatFolder { Id = folder.Id, Name = folder.Name, Collapsed = folder.Collapsed }).ToList(),
                Tags = _state.Tags.Select(tag => new ChatTag { Id = tag.Id, Name = tag.Name, Color = tag.Color }).ToList(),
                Chats = _state.Chats.ToDictionary(
                    pair => pair.Key,
                    pair => new ChatPlacement { FolderId = pair.Value.FolderId, Tags = [.. pair.Value.Tags], Archived = pair.Value.Archived })
            };
        }
    }

    public ChatPlacement PlacementOf(string chatId)
    {
        lock (_gate)
        {
            return _state.Chats.TryGetValue(chatId, out var placement)
                ? new ChatPlacement { FolderId = placement.FolderId, Tags = [.. placement.Tags], Archived = placement.Archived }
                : new ChatPlacement();
        }
    }

    public ChatFolder CreateFolder(string name)
    {
        var folder = new ChatFolder { Id = NewId(), Name = name.Trim() };
        Mutate(state => state.Folders.Add(folder));
        return folder;
    }

    public void RenameFolder(string id, string name) =>
        Mutate(state =>
        {
            if (state.Folders.FirstOrDefault(folder => folder.Id == id) is { } folder)
            {
                folder.Name = name.Trim();
            }
        });

    /// <summary>Удаляет папку; её чаты остаются — просто без папки.</summary>
    public void DeleteFolder(string id) =>
        Mutate(state =>
        {
            state.Folders.RemoveAll(folder => folder.Id == id);
            foreach (var placement in state.Chats.Values.Where(placement => placement.FolderId == id))
            {
                placement.FolderId = null;
            }

            Prune(state);
        });

    public void SetCollapsed(string folderId, bool collapsed) =>
        Mutate(state =>
        {
            if (state.Folders.FirstOrDefault(folder => folder.Id == folderId) is { } folder)
            {
                folder.Collapsed = collapsed;
            }
        });

    public ChatTag CreateTag(string name, string color)
    {
        var tag = new ChatTag { Id = NewId(), Name = name.Trim(), Color = TagColors.Contains(color) ? color : TagColors[0] };
        Mutate(state => state.Tags.Add(tag));
        return tag;
    }

    /// <summary>Новое имя и цвет тега. Цвет не из набора <see cref="TagColors"/> не принимается.</summary>
    public void UpdateTag(string id, string name, string color) =>
        Mutate(state =>
        {
            if (state.Tags.FirstOrDefault(tag => tag.Id == id) is { } tag)
            {
                tag.Name = name.Trim();
                if (TagColors.Contains(color))
                {
                    tag.Color = color;
                }
            }
        });

    public void DeleteTag(string id) =>
        Mutate(state =>
        {
            state.Tags.RemoveAll(tag => tag.Id == id);
            foreach (var placement in state.Chats.Values)
            {
                placement.Tags.Remove(id);
            }

            Prune(state);
        });

    public void MoveToFolder(IEnumerable<string> chatIds, string? folderId) =>
        Mutate(state =>
        {
            var target = folderId is not null && state.Folders.Any(folder => folder.Id == folderId) ? folderId : null;
            foreach (var id in chatIds)
            {
                Placement(state, id).FolderId = target;
            }

            Prune(state);
        });

    /// <summary>Тег у всех выбранных: если он был у всех — снимается, иначе ставится всем.</summary>
    public void ToggleTag(IReadOnlyCollection<string> chatIds, string tagId) =>
        Mutate(state =>
        {
            if (state.Tags.All(tag => tag.Id != tagId))
            {
                return;
            }

            var all = chatIds.All(id => state.Chats.TryGetValue(id, out var placement) && placement.Tags.Contains(tagId));
            foreach (var id in chatIds)
            {
                var placement = Placement(state, id);
                placement.Tags.Remove(tagId);
                if (!all)
                {
                    placement.Tags.Add(tagId);
                }
            }

            Prune(state);
        });

    public void SetArchived(IEnumerable<string> chatIds, bool archived) =>
        Mutate(state =>
        {
            foreach (var id in chatIds)
            {
                Placement(state, id).Archived = archived;
            }

            Prune(state);
        });

    /// <summary>Чат удалён — его назначения тоже.</summary>
    public void Forget(string chatId) =>
        Mutate(state => state.Chats.Remove(chatId));

    private static ChatPlacement Placement(State state, string id)
    {
        if (!state.Chats.TryGetValue(id, out var placement))
        {
            placement = new ChatPlacement();
            state.Chats[id] = placement;
        }

        return placement;
    }

    /// <summary>Пустые назначения не хранятся — файл растёт только с тем, что человек разложил.</summary>
    private static void Prune(State state)
    {
        foreach (var id in state.Chats.Where(pair => pair.Value.IsEmpty).Select(pair => pair.Key).ToList())
        {
            state.Chats.Remove(id);
        }
    }

    private void Mutate(Action<State> change)
    {
        lock (_gate)
        {
            change(_state);
            _version++;
            try
            {
                AppDataFile.WriteAtomic(_path, JsonSerializer.Serialize(_state, AppJson.Options));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Раскладка останется в памяти до конца сеанса; упадёт диск — не повод терять её сразу.
            }
        }

        Changed?.Invoke();
    }

    private static State Read(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), AppJson.Options) ?? new State()
                : new State();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new State();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..10];
}

/// <summary>Строка раскладки списка чатов.</summary>
internal abstract record ChatListNode;

internal sealed record ChatListGroup(string TitleKey) : ChatListNode;

internal sealed record ChatListFolder(ChatFolder Folder, int Count) : ChatListNode;

internal sealed record ChatListArchive(int Count, bool Expanded) : ChatListNode;

/// <param name="Nested">Строка внутри раскрытой папки или архива — её рисуют в карточке папки.</param>
internal sealed record ChatListChat(ChatIndexEntry Entry, IReadOnlyList<ChatTag> Tags, bool Nested = false) : ChatListNode;

/// <summary>
/// Раскладка боковой панели: закреплённые, папки, группы по датам (или один список при другой
/// сортировке) и архив. Чистая функция — проверяется тестами без окна.
/// </summary>
internal static class ChatListLayout
{
    public static IReadOnlyList<ChatListNode> Build(
        IReadOnlyList<ChatIndexEntry> items,
        ChatOrganizer.State organize,
        ChatSort sort,
        string? tagFilter,
        bool archiveExpanded,
        DateTime today)
    {
        var tags = organize.Tags.ToDictionary(tag => tag.Id, StringComparer.Ordinal);
        var folders = organize.Folders.ToDictionary(folder => folder.Id, StringComparer.Ordinal);
        ChatPlacement PlacementOf(ChatIndexEntry entry) =>
            organize.Chats.TryGetValue(entry.Id, out var placement) ? placement : Empty;

        var visible = items.Where(entry => tagFilter is null || PlacementOf(entry).Tags.Contains(tagFilter)).ToList();
        var nodes = new List<ChatListNode>();

        ChatListChat Row(ChatIndexEntry entry) =>
            new(entry, PlacementOf(entry).Tags.Where(tags.ContainsKey).Select(id => tags[id]).ToList());

        void Group(string key, IEnumerable<ChatIndexEntry> entries)
        {
            var list = Sort(entries, sort).ToList();
            if (list.Count == 0)
            {
                return;
            }

            nodes.Add(new ChatListGroup(key));
            nodes.AddRange(list.Select(Row));
        }

        var active = visible.Where(entry => !PlacementOf(entry).Archived).ToList();
        var archived = visible.Where(entry => PlacementOf(entry).Archived).ToList();

        Group("S.ChatList.Pinned", active.Where(entry => entry.IsPinned));

        // Папки — после закреплённых: у закреплённого чата место одно, наверху, даже если он в папке.
        // Под своим заголовком: без него первая папка читалась как ещё один закреплённый чат.
        var loose = active.Where(entry => !entry.IsPinned).ToList();
        var foldersTitled = false;
        foreach (var folder in organize.Folders)
        {
            var inside = Sort(loose.Where(entry => PlacementOf(entry).FolderId == folder.Id), sort).ToList();
            if (inside.Count == 0 && tagFilter is not null)
            {
                continue;
            }

            if (!foldersTitled)
            {
                nodes.Add(new ChatListGroup("S.ChatList.Folders"));
                foldersTitled = true;
            }

            nodes.Add(new ChatListFolder(folder, inside.Count));
            if (!folder.Collapsed)
            {
                nodes.AddRange(inside.Select(entry => Row(entry) with { Nested = true }));
            }
        }

        var unfiled = loose.Where(entry => PlacementOf(entry).FolderId is not { } id || !folders.ContainsKey(id)).ToList();
        if (sort == ChatSort.Updated)
        {
            var yesterday = today.AddDays(-1);
            Group("S.ChatList.Today", unfiled.Where(entry => entry.UpdatedAt.Date == today));
            Group("S.ChatList.Yesterday", unfiled.Where(entry => entry.UpdatedAt.Date == yesterday));
            Group("S.ChatList.Earlier", unfiled.Where(entry => entry.UpdatedAt.Date < yesterday || entry.UpdatedAt.Date > today));
        }
        else
        {
            Group("S.ChatList.All", unfiled);
        }

        if (archived.Count > 0)
        {
            nodes.Add(new ChatListArchive(archived.Count, archiveExpanded));
            if (archiveExpanded)
            {
                nodes.AddRange(Sort(archived, sort).Select(entry => Row(entry) with { Nested = true }));
            }
        }

        return nodes;
    }

    /// <summary>
    /// Выдача поиска: одна группа в заданном порядке, с тегами, без папок и архива — найденное
    /// не должно прятаться за свёрнутой папкой.
    /// </summary>
    public static IReadOnlyList<ChatListNode> Flat(IReadOnlyList<ChatIndexEntry> items, ChatOrganizer.State organize, string titleKey)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var tags = organize.Tags.ToDictionary(tag => tag.Id, StringComparer.Ordinal);
        var nodes = new List<ChatListNode>(items.Count + 1) { new ChatListGroup(titleKey) };
        foreach (var entry in items)
        {
            var own = organize.Chats.TryGetValue(entry.Id, out var placement) ? placement.Tags : [];
            nodes.Add(new ChatListChat(entry, own.Where(tags.ContainsKey).Select(id => tags[id]).ToList()));
        }

        return nodes;
    }

    public static IEnumerable<ChatIndexEntry> Sort(IEnumerable<ChatIndexEntry> entries, ChatSort sort) => sort switch
    {
        ChatSort.Created => entries.OrderByDescending(entry => entry.CreatedAt == default ? entry.UpdatedAt : entry.CreatedAt),
        ChatSort.Title => entries.OrderBy(entry => entry.Title, StringComparer.CurrentCultureIgnoreCase),
        ChatSort.Cost => entries.OrderByDescending(entry => entry.TotalCost).ThenByDescending(entry => entry.UpdatedAt),
        _ => entries.OrderByDescending(entry => entry.UpdatedAt)
    };

    private static readonly ChatPlacement Empty = new();
}

/// <summary>Границы ширины боковой панели (D5).</summary>
internal static class SidebarWidths
{
    public const double Default = 184;

    public const double Min = 160;

    public const double Max = 380;

    /// <summary>Сохранённая ширина в допустимых границах; мусор в файле — заводская.</summary>
    public static double Clamp(double? width) =>
        width is { } value && double.IsFinite(value) ? Math.Clamp(value, Min, Max) : Default;
}

/// <summary>Цена чата целиком (E3): все ответы во всех вариантах.</summary>
internal static class ChatCost
{
    public static decimal Total(ChatSession session)
    {
        lock (session.Gate)
        {
            return ChatBranches.AllMessages(session).Sum(message => message.Cost?.Usd ?? 0m);
        }
    }

    /// <summary>
    /// Из чего сложилась цена чата: модели по именам, агенты, инструменты и служебная работа —
    /// по всем вариантам ответов сразу. Последняя строка — итог.
    /// </summary>
    /// <remarks>
    /// У старых переписок разбивки на сообщении нет (<see cref="ChatDisplayMessage.ModelCost"/>
    /// появился позже цены), и строки не складываются в итог. Разница идёт строкой «Прочее» —
    /// иначе сумма под чертой не сошлась бы со строками над ней.
    /// </remarks>
    public static IReadOnlyList<(string Label, VeniceCost Cost)> Breakdown(ChatSession session)
    {
        var models = new Dictionary<string, VeniceCost>(StringComparer.Ordinal);
        var agents = VeniceCost.Zero;
        var tools = VeniceCost.Zero;
        var router = VeniceCost.Zero;
        var guard = VeniceCost.Zero;
        var title = VeniceCost.Zero;
        var summary = VeniceCost.Zero;
        var compact = VeniceCost.Zero;
        var total = VeniceCost.Zero;

        lock (session.Gate)
        {
            foreach (var message in ChatBranches.AllMessages(session))
            {
                if (message.Cost is not { HasData: true } cost)
                {
                    continue;
                }

                total = total.Add(cost);
                if (message.ModelCost is { HasData: true } model)
                {
                    var name = VeniceModelCatalog.GetDisplayName(message.ResolvedModelId ?? message.RequestedModelId ?? "");
                    name = string.IsNullOrWhiteSpace(name) ? Loc.Get("S.Cost.Model") : name;
                    models[name] = models.TryGetValue(name, out var sum) ? sum.Add(model) : model;
                }

                foreach (var call in message.ToolRounds.SelectMany(round => round.Calls))
                {
                    if (call.NestedAgent is { Cost.HasData: true } agent)
                    {
                        agents = agents.Add(agent.Cost);
                    }
                    else if (call.Cost is { HasData: true } paid)
                    {
                        tools = tools.Add(paid);
                    }
                }

                router = Plus(router, message.RouterCost);
                guard = Plus(guard, message.GuardCost);
                title = Plus(title, message.TitleCost);
                summary = Plus(summary, message.SummaryCost);
                compact = Plus(compact, message.CompactCost);
            }
        }

        var lines = new List<(string, VeniceCost)>();
        lines.AddRange(models.OrderByDescending(pair => pair.Value.Usd).Select(pair => (pair.Key, pair.Value)));
        Line(lines, "S.Cost.Agents", agents);
        Line(lines, "S.Cost.Tools", tools);
        Line(lines, "S.Cost.Router", router);
        Line(lines, "S.Cost.Guard", guard);
        Line(lines, "S.Cost.ChatTitle", title);
        Line(lines, "S.Cost.Summary", summary);
        Line(lines, "S.Cost.Compact", compact);

        var listed = lines.Aggregate(VeniceCost.Zero, (sum, line) => sum.Add(line.Item2));
        var rest = total.Usd - listed.Usd;
        if (rest > 0.0001m)
        {
            lines.Add((Loc.Get("S.Cost.Other"), new VeniceCost { Usd = rest, HasData = true }));
        }

        lines.Add((Loc.Get("S.Cost.Total"), total));
        return lines;
    }

    private static VeniceCost Plus(VeniceCost sum, VeniceCost? cost) =>
        cost is { HasData: true } ? sum.Add(cost) : sum;

    private static void Line(List<(string, VeniceCost)> lines, string key, VeniceCost cost)
    {
        if (cost.HasData && cost.Usd > 0m)
        {
            lines.Add((Loc.Get(key), cost));
        }
    }
}
