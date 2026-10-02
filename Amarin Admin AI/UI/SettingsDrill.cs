using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Amarin.UI;

/// <summary>
/// Подстраницы настроек: строка со стрелкой «›» открывает вложенную страницу на месте текущей,
/// шапка «‹» и Esc возвращают назад.
/// </summary>
/// <remarks>
/// <para>
/// Так страница первого уровня остаётся короткой, как в 1.27.1, а тяжёлые блоки (лимиты трат,
/// резервные копии, список инструментов) не разрывают её посередине. Раскладка в разметке:
/// <c>Grid</c> с <see cref="IsHostProperty"/> внутри <c>ScrollViewer</c> страницы; первый ребёнок —
/// корень страницы, остальные — подстраницы (свёрнутые). Строка-ссылка указывает свою
/// подстраницу через <see cref="OpensProperty"/>, кнопка-шапка подстраницы помечена
/// <see cref="IsBackProperty"/>.
/// </para>
/// <para>
/// Прокрутка корня запоминается и возвращается: человек уходит с середины длинной страницы и
/// приходит туда же. Страница, ушедшая из вида (другой пункт навигации, закрытые настройки),
/// сама возвращается к корню — иначе при следующем открытии человек попал бы в подстраницу без
/// понятия, как туда пришёл.
/// </para>
/// </remarks>
internal static class SettingsDrill
{
    // ───────────────────────── Хост ─────────────────────────

    public static readonly DependencyProperty IsHostProperty = DependencyProperty.RegisterAttached(
        "IsHost", typeof(bool), typeof(SettingsDrill), new PropertyMetadata(false, OnIsHostChanged));

    public static bool GetIsHost(DependencyObject element) => (bool)element.GetValue(IsHostProperty);

    public static void SetIsHost(DependencyObject element, bool value) => element.SetValue(IsHostProperty, value);

    private static readonly DependencyProperty SavedOffsetProperty = DependencyProperty.RegisterAttached(
        "SavedOffset", typeof(double), typeof(SettingsDrill), new PropertyMetadata(0.0));

    private static readonly DependencyProperty OpenerProperty = DependencyProperty.RegisterAttached(
        "Opener", typeof(IInputElement), typeof(SettingsDrill), new PropertyMetadata(null));

    private static void OnIsHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Panel host)
        {
            return;
        }

        host.IsVisibleChanged -= ResetWhenHidden;
        if ((bool)e.NewValue)
        {
            host.IsVisibleChanged += ResetWhenHidden;
        }
    }

    private static void ResetWhenHidden(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Panel host && !(bool)e.NewValue)
        {
            ShowRoot(host, animate: false);
        }
    }

    // ───────────────────────── Строка-ссылка и шапка ─────────────────────────

    /// <summary>Какую подстраницу открывает кнопка. Ставится привязкой <c>{Binding ElementName=…}</c>.</summary>
    public static readonly DependencyProperty OpensProperty = DependencyProperty.RegisterAttached(
        "Opens", typeof(FrameworkElement), typeof(SettingsDrill), new PropertyMetadata(null, OnOpensChanged));

    public static FrameworkElement? GetOpens(DependencyObject element) => (FrameworkElement?)element.GetValue(OpensProperty);

    public static void SetOpens(DependencyObject element, FrameworkElement? value) => element.SetValue(OpensProperty, value);

    private static void OnOpensChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= OpenOnClick;
        if (e.NewValue is not null)
        {
            button.Click += OpenOnClick;
        }
    }

    private static void OpenOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject button && GetOpens(button) is { } sub)
        {
            e.Handled = true;
            Open(sub, sender as IInputElement);
        }
    }

    /// <summary>Кнопка «‹ Заголовок» в шапке подстраницы.</summary>
    public static readonly DependencyProperty IsBackProperty = DependencyProperty.RegisterAttached(
        "IsBack", typeof(bool), typeof(SettingsDrill), new PropertyMetadata(false, OnIsBackChanged));

    public static bool GetIsBack(DependencyObject element) => (bool)element.GetValue(IsBackProperty);

    public static void SetIsBack(DependencyObject element, bool value) => element.SetValue(IsBackProperty, value);

    private static void OnIsBackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
        {
            return;
        }

        button.Click -= BackOnClick;
        if ((bool)e.NewValue)
        {
            button.Click += BackOnClick;
        }
    }

    private static void BackOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject button && Back(button))
        {
            e.Handled = true;
        }
    }

    // ───────────────────────── Событие «открыта» ─────────────────────────

    /// <summary>
    /// Подстраница открыта — повод освежить то, что на ней считается заранее (самые тяжёлые
    /// чаты, сводка инструментов). Поднимается на самой подстранице и всплывает.
    /// </summary>
    public static readonly RoutedEvent OpenedEvent = EventManager.RegisterRoutedEvent(
        "Opened", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SettingsDrill));

    public static void AddOpenedHandler(DependencyObject element, RoutedEventHandler handler) =>
        (element as UIElement)?.AddHandler(OpenedEvent, handler);

    public static void RemoveOpenedHandler(DependencyObject element, RoutedEventHandler handler) =>
        (element as UIElement)?.RemoveHandler(OpenedEvent, handler);

    // ───────────────────────── Переходы ─────────────────────────

    /// <summary>Открывает подстраницу, запомнив прокрутку корня.</summary>
    public static void Open(FrameworkElement sub, IInputElement? opener = null)
    {
        if (LogicalTreeHelper.GetParent(sub) is not Panel host || !GetIsHost(host) || host.Children.Count < 2)
        {
            return;
        }

        var scroll = ScrollOf(host);
        if (scroll is not null)
        {
            SmoothScroll.Cancel(scroll);
            host.SetValue(SavedOffsetProperty, scroll.VerticalOffset);
        }

        sub.SetValue(OpenerProperty, opener);
        foreach (UIElement child in host.Children)
        {
            child.Visibility = ReferenceEquals(child, sub) ? Visibility.Visible : Visibility.Collapsed;
        }

        scroll?.ScrollToVerticalOffset(0);
        UiMotion.Enter(sub, dx: 18);
        sub.RaiseEvent(new RoutedEventArgs(OpenedEvent, sub));

        // Фокус — на шапку «‹»: с клавиатуры следующим шагом человек либо идёт по подстранице,
        // либо возвращается, а фокус на свёрнутой строке-ссылке просто потерялся бы.
        sub.Dispatcher.BeginInvoke(
            () => sub.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Возвращает к корню ту страницу, в подстранице которой лежит <paramref name="inside"/>.</summary>
    public static bool Back(DependencyObject inside)
    {
        for (var node = inside; node is not null; node = LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node))
        {
            if (node is Panel host && GetIsHost(host))
            {
                return ShowRoot(host, animate: true);
            }
        }

        return false;
    }

    /// <summary>
    /// Esc в настройках: если видна какая-нибудь подстраница внутри <paramref name="scope"/>,
    /// возвращает её к корню.
    /// </summary>
    public static bool TryBackIn(DependencyObject scope)
    {
        foreach (var host in Hosts(scope))
        {
            if (host.IsVisible && OpenSub(host) is not null)
            {
                return ShowRoot(host, animate: true);
            }
        }

        return false;
    }

    /// <summary>Открытая подстраница хоста или <c>null</c>, если показан корень.</summary>
    public static FrameworkElement? OpenSub(Panel host)
    {
        for (var i = 1; i < host.Children.Count; i++)
        {
            if (host.Children[i] is FrameworkElement sub && sub.Visibility == Visibility.Visible)
            {
                return sub;
            }
        }

        return null;
    }

    private static bool ShowRoot(Panel host, bool animate)
    {
        var sub = OpenSub(host);
        if (sub is null || host.Children[0] is not FrameworkElement root)
        {
            return false;
        }

        sub.Visibility = Visibility.Collapsed;
        root.Visibility = Visibility.Visible;

        var scroll = ScrollOf(host);
        if (scroll is not null)
        {
            var offset = (double)host.GetValue(SavedOffsetProperty);
            scroll.UpdateLayout();
            scroll.ScrollToVerticalOffset(animate ? offset : 0);
        }

        if (animate)
        {
            UiMotion.Enter(root, dx: -12);
            if (sub.GetValue(OpenerProperty) is IInputElement opener && opener is UIElement { IsVisible: true })
            {
                Keyboard.Focus(opener);
            }
        }

        sub.ClearValue(OpenerProperty);
        return true;
    }

    private static ScrollViewer? ScrollOf(DependencyObject element)
    {
        for (var node = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
             node is not null;
             node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer scroll)
            {
                return scroll;
            }
        }

        return null;
    }

    private static IEnumerable<Panel> Hosts(DependencyObject scope)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(scope);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is Panel panel && GetIsHost(panel))
            {
                yield return panel;
            }

            if (node is not Visual and not System.Windows.Media.Media3D.Visual3D)
            {
                continue;
            }

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
            {
                pending.Push(VisualTreeHelper.GetChild(node, i));
            }
        }
    }
}
