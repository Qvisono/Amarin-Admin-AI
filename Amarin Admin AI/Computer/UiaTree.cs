using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Amarin.Core;

namespace Amarin.Computer;

/// <summary>
/// Элементы окон через UI Automation: дерево окна с пределом, свежее место элемента, фокус и
/// нажатие без мыши.
/// </summary>
/// <remarks>
/// <para>
/// Дерево читается обходом в глубину: на каждый раскрываемый узел — один кэширующий запрос его
/// детей (<see cref="CacheRequest"/> на <c>Element | Children</c>), и обход кончается на пределе
/// элементов или времени. Прежде было одно кэширование всего поддерева, а предел резал только
/// обход: окно браузера с тысячами элементов кэшировалось целиком на каждое нажатие, секундами.
/// Невидимое (<c>IsOffscreen</c>) в выдачу идёт, но не раскрывается: прокрученная за край часть
/// страницы — большая часть её дерева.
/// </para>
/// <para>
/// Безымянные контейнеры (панели, группы, картинки без подписи) в выдачу не попадают — их
/// дети встают на их место. Модели они ничего не говорят, а предел дерева съели бы первыми.
/// </para>
/// <para>
/// Ключ элемента — его RuntimeId, а сам элемент запоминается до следующего дерева того же окна:
/// по нему потом спрашивают свежее место, ставят фокус и нажимают, не обходя дерево заново.
/// </para>
/// </remarks>
internal sealed class UiaTree
{
    private static readonly ControlType[] Structural =
    [
        ControlType.Pane, ControlType.Group, ControlType.Custom, ControlType.Separator, ControlType.Image,
        ControlType.Text, ControlType.Thumb, ControlType.Header
    ];

    /// <summary>Чьих детей не спрашиваем: в них нет ничего, на что модели стоило бы нажать.</summary>
    private static readonly ControlType[] Leaves =
    [
        ControlType.Text, ControlType.Image, ControlType.Separator, ControlType.Thumb, ControlType.ScrollBar,
        ControlType.ProgressBar
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<IntPtr, Dictionary<string, AutomationElement>> _known = [];

    public ComputerTree Inspect(IntPtr hwnd, int limit, TimeSpan budget)
    {
        var clock = Stopwatch.StartNew();
        var request = Request(TreeScope.Element | TreeScope.Children);
        var root = AutomationElement.FromHandle(hwnd).GetUpdatedCache(request);

        var found = new List<ComputerElement>();
        var known = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        var pending = new Stack<(AutomationElement Node, int Depth, string ParentName)>();
        Push(root.CachedChildren, 0, "");

        var cut = false;
        while (pending.Count > 0)
        {
            if (found.Count >= limit || clock.Elapsed > budget)
            {
                cut = true;
                break;
            }

            var (node, depth, parentName) = pending.Pop();

            // Подпись внутри кнопки или пункта повторяет его имя — вторая строка о том же.
            var info = Cached(node, depth);
            if (info is { Role: "text" } && string.Equals(info.Name, parentName, StringComparison.Ordinal))
            {
                info = null;
            }

            if (info is not null)
            {
                found.Add(info);
                known[info.Key] = node;
            }

            if (!Expandable(node))
            {
                continue;
            }

            AutomationElementCollection children;
            try
            {
                children = node.GetUpdatedCache(request).CachedChildren;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                // Элемент исчез посреди обхода — окно меняется; остальное дерево всё равно годно.
                continue;
            }

            Push(children, info is null ? depth : depth + 1, info?.Name ?? parentName);
        }

        lock (_gate)
        {
            // Закрытые окна больше не спросят, а их элементы держали бы объекты UI Automation чужих
            // процессов до выхода из программы — живёт она днями.
            foreach (var closed in _known.Keys.Where(handle => !ComputerNative.IsWindow(handle)).ToList())
            {
                _ = _known.Remove(closed);
            }

            _known[hwnd] = known;
        }

        return new ComputerTree(found, cut);

        // В стек в обратном порядке: сверху окажется первый ребёнок, и выдача идёт сверху вниз.
        void Push(AutomationElementCollection children, int depth, string parentName)
        {
            for (var i = children.Count - 1; i >= 0; i--)
            {
                pending.Push((children[i], depth, parentName));
            }
        }
    }

    /// <summary>Тот же элемент сейчас. Null — его нет или окно с тех пор смотрели заново.</summary>
    public ComputerElement? Element(IntPtr hwnd, string key)
    {
        if (Find(hwnd, key) is not { } element)
        {
            return null;
        }

        try
        {
            return Describe(element.GetUpdatedCache(Request(TreeScope.Element)), key, depth: 0);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return null;
        }
    }

    public bool Focus(IntPtr hwnd, string key)
    {
        if (Find(hwnd, key) is not { } element)
        {
            return false;
        }

        try
        {
            element.SetFocus();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return false;
        }
    }

    /// <summary>Нажатие без мыши — по тому, что элемент умеет: нажаться, переключиться, выбраться, раскрыться.</summary>
    public bool Invoke(IntPtr hwnd, string key)
    {
        if (Find(hwnd, key) is not { } element)
        {
            return false;
        }

        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return true;
            }

            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle))
            {
                ((TogglePattern)toggle).Toggle();
                return true;
            }

            if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select))
            {
                ((SelectionItemPattern)select).Select();
                return true;
            }

            if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand))
            {
                var pattern = (ExpandCollapsePattern)expand;
                if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Expanded)
                {
                    pattern.Collapse();
                }
                else
                {
                    pattern.Expand();
                }

                return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// Элемент с фокусом клавиатуры вместе с его значением. Null — его нет или он не отвечает.
    /// </summary>
    /// <remarks>
    /// Значение — ради SynGuard: по нажатию Enter не понять, что уйдёт, а текст поля с фокусом
    /// говорит. Прежде здесь читались свойства по одному и без значения, и защитник в настоящей
    /// программе текста поля не видел никогда. Поле пароля значения не отдаёт.
    /// </remarks>
    public static ComputerElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement is { } element
                ? Describe(element.GetUpdatedCache(Request(TreeScope.Element)), KeyOf(element.GetRuntimeId()), 0)
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Скольких окон элементы помнятся — тестам.</summary>
    internal int KnownWindows
    {
        get
        {
            lock (_gate)
            {
                return _known.Count;
            }
        }
    }

    private AutomationElement? Find(IntPtr hwnd, string key)
    {
        lock (_gate)
        {
            return _known.TryGetValue(hwnd, out var known) && known.TryGetValue(key, out var element) ? element : null;
        }
    }

    /// <summary>
    /// Что читается одним запросом: все свойства, из которых собирается элемент, — один вызов в
    /// чужой процесс вместо восьми.
    /// </summary>
    private static CacheRequest Request(TreeScope scope)
    {
        var request = new CacheRequest
        {
            TreeFilter = Automation.ControlViewCondition,
            AutomationElementMode = AutomationElementMode.Full,
            TreeScope = scope
        };
        foreach (var property in new[]
                 {
                     AutomationElement.ControlTypeProperty, AutomationElement.NameProperty,
                     AutomationElement.IsEnabledProperty, AutomationElement.HasKeyboardFocusProperty,
                     AutomationElement.IsPasswordProperty, AutomationElement.IsOffscreenProperty,
                     AutomationElement.BoundingRectangleProperty, AutomationElement.RuntimeIdProperty,
                     ValuePattern.ValueProperty, TogglePattern.ToggleStateProperty, SelectionItemPattern.IsSelectedProperty
                 })
        {
            request.Add(property);
        }

        return request;
    }

    /// <summary>Элемент из кэша дерева. Null — безымянный контейнер, который в выдачу не идёт.</summary>
    private static ComputerElement? Cached(AutomationElement element, int depth)
    {
        var type = element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
        var name = element.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
        if (type is null || (name.Length == 0 && Structural.Contains(type)))
        {
            return null;
        }

        return Describe(element, KeyOf(element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[]), depth);
    }

    /// <summary>Стоит ли спрашивать детей: невидимое и листья не раскрываются.</summary>
    private static bool Expandable(AutomationElement element) =>
        element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty) is not true &&
        !(element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) is ControlType type && Leaves.Contains(type));

    /// <summary>Элемент по кэшированным свойствам.</summary>
    private static ComputerElement Describe(AutomationElement element, string key, int depth)
    {
        var type = element.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
        var password = element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty) is true;
        return new ComputerElement(
            key,
            depth,
            RoleOf(type),
            element.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "",
            password ? null : ValueOf(
                element.GetCachedPropertyValue(ValuePattern.ValueProperty),
                element.GetCachedPropertyValue(TogglePattern.ToggleStateProperty),
                element.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty)),
            RectOf(element.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty)),
            Enabled: element.GetCachedPropertyValue(AutomationElement.IsEnabledProperty) is not false,
            Focused: element.GetCachedPropertyValue(AutomationElement.HasKeyboardFocusProperty) is true,
            IsPassword: password,
            Offscreen: element.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty) is true);
    }

    private static string? ValueOf(object value, object toggle, object selected)
    {
        if (value is string { Length: > 0 } text)
        {
            return text;
        }

        if (toggle is ToggleState state)
        {
            return state switch
            {
                ToggleState.On => "on",
                ToggleState.Off => "off",
                _ => "mixed"
            };
        }

        return selected is true ? "selected" : null;
    }

    /// <summary>Тип элемента для модели: «list item», а не «ControlType.ListItem» и не перевод Windows.</summary>
    internal static string RoleOf(ControlType? type)
    {
        var name = type?.ProgrammaticName ?? "";
        var dot = name.LastIndexOf('.');
        name = dot >= 0 ? name[(dot + 1)..] : name;
        var role = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                role.Append(' ');
            }

            role.Append(char.ToLowerInvariant(name[i]));
        }

        return role.Length == 0 ? "element" : role.ToString();
    }

    private static ScreenRect RectOf(object value) =>
        value is System.Windows.Rect rect ? RectOf(rect) : default;

    private static ScreenRect RectOf(System.Windows.Rect rect) =>
        rect.IsEmpty || double.IsInfinity(rect.Width) || double.IsInfinity(rect.Height)
            ? default
            : new ScreenRect((int)Math.Round(rect.X), (int)Math.Round(rect.Y), (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

    private static string KeyOf(int[]? runtimeId) =>
        runtimeId is { Length: > 0 }
            ? string.Join('.', runtimeId)
            : Guid.NewGuid().ToString("N");
}
