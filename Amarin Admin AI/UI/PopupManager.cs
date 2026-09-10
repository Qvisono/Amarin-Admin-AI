using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Один открытый попап за раз и закрытие по клику мимо.
/// </summary>
/// <remarks>
/// <para>
/// Родной <c>StaysOpen="False"</c> здесь не работает по двум причинам. Первая: он ловит нажатие
/// раньше кнопки, гасит попап и тут же отдаёт то же самое нажатие <c>ClickMode="Press"</c>,
/// который снова взводит <c>IsChecked</c> — попап моргает и остаётся открытым. Вторая: захват
/// мыши живёт в пределах одного HWND, а каждый попап — своё окно, поэтому клик по соседнему
/// попапу первый попап просто не видит, и они копятся на экране.
/// </para>
/// <para>
/// Поэтому закрытие берём на себя: попапы регистрируются здесь, при открытии одного все
/// остальные гасятся, а на окне висит туннельный <c>PreviewMouseDown</c>, который закрывает
/// попап, если нажатие пришло не в его содержимое и не в его же кнопку. Кнопку пропускаем
/// намеренно — её <c>IsChecked</c> сам снимет <c>IsOpen</c> по привязке.
/// </para>
/// </remarks>
internal static class PopupManager
{
    private sealed class Entry
    {
        public required WeakReference<Popup> Popup { get; init; }

        public required WeakReference<ToggleButton>? Toggle { get; init; }
    }

    private static readonly List<Entry> Entries = [];
    private static readonly List<WeakReference<Window>> Hooked = [];

    /// <summary>Отдаёт попап под присмотр менеджера. Повторная регистрация игнорируется.</summary>
    public static void Register(Popup popup, ToggleButton? toggle = null)
    {
        ArgumentNullException.ThrowIfNull(popup);

        Prune();
        foreach (var entry in Entries)
        {
            if (entry.Popup.TryGetTarget(out var known) && ReferenceEquals(known, popup))
            {
                return;
            }
        }

        popup.StaysOpen = true;
        Entries.Add(new Entry
        {
            Popup = new WeakReference<Popup>(popup),
            Toggle = toggle is null ? null : new WeakReference<ToggleButton>(toggle)
        });

        popup.Opened += OnPopupOpened;
    }

    /// <summary>Гасит всё открытое. Вызывается при уходе фокуса с окна и по Esc.</summary>
    public static void CloseAll()
    {
        foreach (var entry in Entries.ToList())
        {
            Close(entry);
        }
    }

    /// <summary>Открыт ли сейчас хоть один зарегистрированный попап. Для тестов.</summary>
    internal static bool AnyOpen()
    {
        foreach (var entry in Entries)
        {
            if (entry.Popup.TryGetTarget(out var popup) && popup.IsOpen)
            {
                return true;
            }
        }

        return false;
    }

    private static void OnPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not Popup opened)
        {
            return;
        }

        Prune();
        foreach (var entry in Entries.ToList())
        {
            if (entry.Popup.TryGetTarget(out var popup) && !ReferenceEquals(popup, opened))
            {
                Close(entry);
            }
        }

        Hook(Window.GetWindow(opened));
    }

    private static void Hook(Window? window)
    {
        if (window is null)
        {
            return;
        }

        foreach (var reference in Hooked)
        {
            if (reference.TryGetTarget(out var known) && ReferenceEquals(known, window))
            {
                return;
            }
        }

        Hooked.Add(new WeakReference<Window>(window));

        // handledEventsToo: кнопки и списки внутри окна гасят нажатие, а попап всё равно
        // должен закрыться — иначе клик по элементу с собственной обработкой оставляет его висеть.
        window.AddHandler(
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(OnWindowMouseDown),
            handledEventsToo: true);
        window.AddHandler(
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnWindowMouseWheel),
            handledEventsToo: true);
        window.PreviewKeyDown += OnWindowKeyDown;
        window.Deactivated += (_, _) => CloseAll();
        window.LocationChanged += (_, _) => CloseAll();
        window.SizeChanged += (_, _) => CloseAll();
        window.Closed += (_, _) => CloseAll();
    }

    private static void OnWindowMouseDown(object sender, MouseButtonEventArgs e) =>
        CloseOutside(e.OriginalSource as DependencyObject);

    private static void OnWindowMouseWheel(object sender, MouseWheelEventArgs e) =>
        CloseOutside(e.OriginalSource as DependencyObject);

    private static void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && AnyOpen())
        {
            e.Handled = true;
            CloseAll();
        }
    }

    private static void CloseOutside(DependencyObject? source)
    {
        Prune();
        foreach (var entry in Entries.ToList())
        {
            if (!entry.Popup.TryGetTarget(out var popup) || !popup.IsOpen)
            {
                continue;
            }

            if (IsWithin(source, popup.Child) || IsWithin(source, Target(entry)))
            {
                continue;
            }

            Close(entry);
        }
    }

    private static ToggleButton? Target(Entry entry) =>
        entry.Toggle is not null && entry.Toggle.TryGetTarget(out var toggle) ? toggle : null;

    private static void Close(Entry entry)
    {
        if (entry.Popup.TryGetTarget(out var popup) && popup.IsOpen)
        {
            popup.IsOpen = false;
        }

        if (Target(entry) is { IsChecked: true } toggle)
        {
            toggle.IsChecked = false;
        }
    }

    /// <summary>
    /// Содержимое попапа живёт в отдельном окне, поэтому идём вверх и по визуальному дереву,
    /// и по логическому: визуальный родитель у корня попапа пуст, а логический ведёт обратно
    /// в дерево владельца. Заодно перепрыгиваем из шаблона на владеющий контрол.
    /// </summary>
    private static bool IsWithin(DependencyObject? source, DependencyObject? root)
    {
        if (source is null || root is null)
        {
            return false;
        }

        var guard = 0;
        var current = source;
        while (current is not null && guard++ < 256)
        {
            if (ReferenceEquals(current, root))
            {
                return true;
            }

            var parent = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : null;

            parent ??= LogicalTreeHelper.GetParent(current);
            parent ??= (current as FrameworkElement)?.TemplatedParent;
            parent ??= (current as FrameworkContentElement)?.Parent;
            current = parent;
        }

        return false;
    }

    private static void Prune()
    {
        Entries.RemoveAll(entry => !entry.Popup.TryGetTarget(out _));
        Hooked.RemoveAll(reference => !reference.TryGetTarget(out _));
    }
}
