namespace Amarin.Core;

/// <summary>
/// Выбранные строки списка чатов, захваченные папки и якорь для Shift+щелчка.
/// </summary>
/// <remarks>
/// До 1.30.0 — поля окна (<c>_selectedChats</c>, <c>_selectionAnchor</c>), и проверить их можно было
/// только оконным тестом. Порядок строк для диапазона окно отдаёт сюда тем, что на экране: выбрать
/// «всё между» можно только по видимому порядку, а он — дело раскладки, не выбора.
/// <para>
/// С 1.32.0 выбирают и папки: рамкой по заголовку, Ctrl+A, Ctrl+щелчком по заголовку. Выбранная
/// папка — это все её чаты (их и выбирают) плюс сама папка: «Удалить» уносит и её. Целиком она
/// выбрана, пока выбраны все её чаты; снял Ctrl+щелчком один — папка остаётся на месте. Это
/// считается при чтении (<see cref="WholeFolders"/>), а не снимается отдельно: так выбор не
/// обязан знать, в какой папке лежит каждый чат.
/// </para>
/// </remarks>
internal sealed class ChatSelection
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(StringComparer.Ordinal);

    /// <summary>Откуда считать диапазон: последняя строка, которую выбирали щелчком.</summary>
    public string? Anchor { get; private set; }

    /// <summary>Сколько выбрано чатов.</summary>
    public int Count => _selected.Count;

    /// <summary>Выбрано хоть что-то: чат или папка (пустая папка — тоже выбор).</summary>
    public bool IsEmpty => _selected.Count == 0 && _folders.Count == 0;

    /// <summary>Выбранные чаты. Снимок: по нему можно менять выбор.</summary>
    public IReadOnlyList<string> Items => [.. _selected];

    public bool Contains(string id) => _selected.Contains(id);

    /// <summary>
    /// Папки, выбранные целиком: захвачены и все их чаты выбраны. Удалённая с тех пор папка в
    /// <paramref name="members"/> не значится — и здесь её нет.
    /// </summary>
    public IReadOnlyList<string> WholeFolders(ChatListMembers members)
    {
        ArgumentNullException.ThrowIfNull(members);
        return [.. _folders.Where(id => members.Folders.TryGetValue(id, out var chats) && chats.All(_selected.Contains))];
    }

    /// <summary>Ctrl+щелчок: добавить или снять строку; она же становится якорем.</summary>
    public void Toggle(string id)
    {
        if (!_selected.Add(id))
        {
            _selected.Remove(id);
        }

        Anchor = id;
    }

    /// <summary>
    /// Ctrl+щелчок по заголовку: выбрать папку целиком или, если она уже выбрана целиком, снять.
    /// </summary>
    public void ToggleFolder(string folderId, IReadOnlyCollection<string> chats)
    {
        ArgumentNullException.ThrowIfNull(chats);
        if (_folders.Contains(folderId) && chats.All(_selected.Contains))
        {
            _folders.Remove(folderId);
            _selected.ExceptWith(chats);
            return;
        }

        _folders.Add(folderId);
        _selected.UnionWith(chats);
    }

    /// <summary>
    /// Shift+щелчок: всё между якорем и этой строкой — в том порядке, что на экране.
    /// </summary>
    /// <param name="visible">Строки списка в порядке показа.</param>
    /// <param name="fallbackAnchor">Якорь, если щелчком ещё не выбирали, — открытый чат.</param>
    /// <remarks>
    /// Якоря или строки на экране нет (чат ушёл в свёрнутую папку) — диапазон не из чего строить, и
    /// щелчок работает как Ctrl+щелчок, а не молча ничего не делает. Якорь диапазон не сдвигает:
    /// следующий Shift+щелчок считается от того же места, как в Проводнике.
    /// </remarks>
    public void SelectRange(string id, IReadOnlyList<string> visible, string fallbackAnchor)
    {
        ArgumentNullException.ThrowIfNull(visible);

        var from = IndexOf(visible, Anchor ?? fallbackAnchor);
        var to = IndexOf(visible, id);
        if (from < 0 || to < 0)
        {
            Toggle(id);
            return;
        }

        _selected.Clear();
        _folders.Clear();
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _selected.Add(visible[i]);
        }
    }

    /// <summary>
    /// Начало рамки: что останется выбранным под ней. С Ctrl рамка добавляет к прежнему выбору,
    /// без него — начинает новый.
    /// </summary>
    public ChatSelectionBasis BeginSweep(bool additive) =>
        additive ? new ChatSelectionBasis([.. _selected], [.. _folders]) : ChatSelectionBasis.Empty;

    /// <summary>Рамка накрыла эти чаты и заголовки: выбор — основа плюс накрытое.</summary>
    public void Sweep(ChatSelectionBasis basis, IEnumerable<string> chats, IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(basis);
        _selected.Clear();
        _selected.UnionWith(basis.Chats);
        _selected.UnionWith(chats);
        _folders.Clear();
        _folders.UnionWith(basis.Folders);
        _folders.UnionWith(folders);
    }

    /// <summary>Ctrl+A: всё, что показывает список, — чаты и папки с их чатами, свёрнутые тоже.</summary>
    public void SelectAll(ChatListMembers members)
    {
        ArgumentNullException.ThrowIfNull(members);
        _selected.Clear();
        _selected.UnionWith(members.All);
        _folders.Clear();
        _folders.UnionWith(members.Folders.Keys);
    }

    /// <summary>Снять выбор. Пустой выбор не трогается — и якорь у него остаётся.</summary>
    /// <returns><c>true</c> — что-то было выбрано.</returns>
    public bool Clear()
    {
        if (IsEmpty)
        {
            return false;
        }

        Reset();
        return true;
    }

    /// <summary>Забыть всё: чаты разложены броском, сменился профиль.</summary>
    public void Reset()
    {
        _selected.Clear();
        _folders.Clear();
        Anchor = null;
    }

    /// <summary>
    /// Снять выбор со строк и папок, которых больше нет (удалены из другого места, сменился профиль).
    /// </summary>
    public void KeepOnly(IReadOnlySet<string> alive, IReadOnlySet<string>? aliveFolders = null)
    {
        ArgumentNullException.ThrowIfNull(alive);
        _selected.RemoveWhere(id => !alive.Contains(id));
        if (aliveFolders is not null)
        {
            _folders.RemoveWhere(id => !aliveFolders.Contains(id));
        }
    }

    /// <summary>Снять выбор с этих строк — их убрали из вида (в архив).</summary>
    public void Remove(IEnumerable<string> ids) => _selected.ExceptWith(ids);

    /// <summary>Забыть папки — их удалили.</summary>
    public void RemoveFolders(IEnumerable<string> folderIds) => _folders.ExceptWith(folderIds);

    /// <summary>
    /// Что несёт перетаскивание за эту строку: весь выбор, если строка в нём и выбрано больше
    /// одной, иначе — её одну.
    /// </summary>
    public IReadOnlyList<string> DragSet(string id) =>
        _selected.Count > 1 && _selected.Contains(id) ? Items : [id];

    private static int IndexOf(IReadOnlyList<string> visible, string id)
    {
        for (var i = 0; i < visible.Count; i++)
        {
            if (string.Equals(visible[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>Что было выбрано до рамки и останется выбранным под ней (рамка с Ctrl).</summary>
internal sealed record ChatSelectionBasis(IReadOnlyList<string> Chats, IReadOnlyList<string> Folders)
{
    public static ChatSelectionBasis Empty { get; } = new([], []);
}
