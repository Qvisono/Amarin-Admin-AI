namespace Amarin.Core;

/// <summary>
/// Выбранные строки списка чатов и якорь для Shift+щелчка.
/// </summary>
/// <remarks>
/// До 1.30.0 — поля окна (<c>_selectedChats</c>, <c>_selectionAnchor</c>), и проверить их можно было
/// только оконным тестом. Порядок строк для диапазона окно отдаёт сюда тем, что на экране: выбрать
/// «всё между» можно только по видимому порядку, а он — дело раскладки, не выбора.
/// </remarks>
internal sealed class ChatSelection
{
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    /// <summary>Откуда считать диапазон: последняя строка, которую выбирали щелчком.</summary>
    public string? Anchor { get; private set; }

    public int Count => _selected.Count;

    /// <summary>Выбранные чаты. Снимок: по нему можно менять выбор.</summary>
    public IReadOnlyList<string> Items => [.. _selected];

    public bool Contains(string id) => _selected.Contains(id);

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
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _selected.Add(visible[i]);
        }
    }

    /// <summary>Снять выбор. Пустой выбор не трогается — и якорь у него остаётся.</summary>
    /// <returns><c>true</c> — что-то было выбрано.</returns>
    public bool Clear()
    {
        if (_selected.Count == 0)
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
        Anchor = null;
    }

    /// <summary>Снять выбор со строк, которых больше нет (удалены из другого места, сменился профиль).</summary>
    public void KeepOnly(IReadOnlySet<string> alive)
    {
        ArgumentNullException.ThrowIfNull(alive);
        _selected.RemoveWhere(id => !alive.Contains(id));
    }

    /// <summary>Снять выбор с этих строк — их убрали из вида (в архив).</summary>
    public void Remove(IEnumerable<string> ids) => _selected.ExceptWith(ids);

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
