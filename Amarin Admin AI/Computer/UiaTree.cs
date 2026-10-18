using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Amarin.Core;

namespace Amarin.Computer;

/// <summary>
/// Элементы окон через UI Automation: дерево окна одним запросом, свежее место элемента, фокус и
/// нажатие без мыши.
/// </summary>
/// <remarks>
/// <para>
/// Дерево берётся одним кэширующим запросом на всё окно (<see cref="CacheRequest"/> на поддерево), а
/// не обходом по одному элементу: каждый шаг обхода — отдельный вызов в чужой процесс, и у
/// окна браузера их было бы тысячи.
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

    private readonly Lock _gate = new();
    private readonly Dictionary<IntPtr, Dictionary<string, AutomationElement>> _known = [];

    public IReadOnlyList<ComputerElement> Inspect(IntPtr hwnd, int limit)
    {
        var request = Request();
        request.TreeScope = TreeScope.Element | TreeScope.Descendants;
        var root = AutomationElement.FromHandle(hwnd).GetUpdatedCache(request);

        var found = new List<ComputerElement>();
        var known = new Dictionary<string, AutomationElement>(StringComparer.Ordinal);
        Walk(root, 0, "");

        lock (_gate)
        {
            _known[hwnd] = known;
        }

        return found;

        void Walk(AutomationElement element, int depth, string parentName)
        {
            foreach (AutomationElement child in element.CachedChildren)
            {
                if (found.Count >= limit)
                {
                    return;
                }

                // Подпись внутри кнопки или пункта повторяет его имя — вторая строка о том же.
                var info = Cached(child, depth);
                var listed = info is not null &&
                             !(info.Role == "text" && string.Equals(info.Name, parentName, StringComparison.Ordinal));
                if (listed && info is not null)
                {
                    found.Add(info);
                    known[info.Key] = child;
                }

                Walk(child, listed ? depth + 1 : depth, listed && info is not null ? info.Name : parentName);
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
            return Live(element, key, depth: 0);
        }
        catch (ElementNotAvailableException)
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

    /// <summary>Элемент с фокусом клавиатуры. Null — его нет или он не отвечает.</summary>
    public static ComputerElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement is { } element ? Live(element, KeyOf(element.GetRuntimeId()), 0) : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException or ArgumentException)
        {
            return null;
        }
    }

    public static string? FocusName()
    {
        try
        {
            return AutomationElement.FocusedElement?.Current.Name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException or ArgumentException)
        {
            return null;
        }
    }

    private AutomationElement? Find(IntPtr hwnd, string key)
    {
        lock (_gate)
        {
            return _known.TryGetValue(hwnd, out var known) && known.TryGetValue(key, out var element) ? element : null;
        }
    }

    private static CacheRequest Request()
    {
        var request = new CacheRequest
        {
            TreeFilter = Automation.ControlViewCondition,
            AutomationElementMode = AutomationElementMode.Full
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

        var password = element.GetCachedPropertyValue(AutomationElement.IsPasswordProperty) is true;
        return new ComputerElement(
            KeyOf(element.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[]),
            depth,
            RoleOf(type),
            name,
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

    /// <summary>Тот же элемент живыми свойствами — для свежего места и для поля с фокусом.</summary>
    private static ComputerElement Live(AutomationElement element, string key, int depth)
    {
        var current = element.Current;
        return new ComputerElement(
            key,
            depth,
            RoleOf(current.ControlType),
            current.Name ?? "",
            null,
            RectOf(current.BoundingRectangle),
            current.IsEnabled,
            current.HasKeyboardFocus,
            current.IsPassword,
            current.IsOffscreen);
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
