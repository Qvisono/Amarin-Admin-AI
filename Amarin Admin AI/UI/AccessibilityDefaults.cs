using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Amarin.UI;

/// <summary>
/// Доступность по умолчанию (I1): имя для экранного диктора и рамка фокуса — всем, у кого их нет.
/// </summary>
/// <remarks>
/// <para>
/// Кнопок-иконок в программе больше сотни, и половина собирается кодом (действия под сообщением,
/// шапки блоков кода, строки списков). Проставлять каждой имя руками значило бы забыть
/// следующую. Поэтому имя берётся из подсказки: у кнопки-иконки она есть почти всегда, и это
/// ровно то, что человек прочитал бы глазами. Привязкой, а не копией: подсказка сменит язык —
/// сменится и имя.
/// </para>
/// <para>
/// Рамка фокуса — только там, где её не задали: элемент со своей рамкой (или с явным «без
/// рамки») решил это сам.
/// </para>
/// </remarks>
internal static class AccessibilityDefaults
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnButtonLoaded));
        EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnControlLoaded));
    }

    /// <summary>
    /// То же, что делает <c>Loaded</c>, — обходом уже собранного дерева. Для окна, которое
    /// собрано, но не показано (так живёт окно в оконных тестах): <c>Loaded</c> у него не приходит.
    /// </summary>
    internal static void ApplyTree(DependencyObject root)
    {
        if (root is ButtonBase button)
        {
            NameFromToolTip(button);
        }

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            ApplyTree(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void OnButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ButtonBase button)
        {
            NameFromToolTip(button);
        }
    }

    /// <summary>Имя из подсказки — если своего имени и текста у кнопки нет.</summary>
    internal static void NameFromToolTip(ButtonBase button)
    {
        if (!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) ||
            BindingOperations.IsDataBound(button, AutomationProperties.NameProperty) ||
            HasOwnText(button.Content) ||
            button.ToolTip is null)
        {
            return;
        }

        button.SetBinding(AutomationProperties.NameProperty, new Binding(nameof(FrameworkElement.ToolTip))
        {
            RelativeSource = RelativeSource.Self,
            Converter = ToolTipText.Instance
        });
    }

    private static bool HasOwnText(object? content) => content switch
    {
        string text => !string.IsNullOrWhiteSpace(text),
        TextBlock block => !string.IsNullOrWhiteSpace(block.Text),
        AccessText access => !string.IsNullOrWhiteSpace(access.Text),
        _ => false
    };

    private static void OnControlLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            ApplyFocusFrame(control);
        }
    }

    /// <summary>Рамка фокуса цветом акцента — элементу, у которого своей нет.</summary>
    /// <remarks>
    /// Поле с текстом рамки не получает (1.32.0): фокус в нём видно по каретке, а рамка вокруг
    /// поля ввода всплывала, стоило фокусу прийти туда с клавиатуры, — например, пробелом,
    /// набранным, пока фокус был на колонке чатов или в ленте, — и закрывала край поля. Тексты
    /// ответов в ленте — тоже поля (только для чтения), и рамка обводила бы ответ целиком.
    /// </remarks>
    internal static void ApplyFocusFrame(Control control)
    {
        if (!control.Focusable)
        {
            return;
        }

        var source = DependencyPropertyHelper.GetValueSource(control, FrameworkElement.FocusVisualStyleProperty).BaseValueSource;
        if (source is not (BaseValueSource.Default or BaseValueSource.DefaultStyle or BaseValueSource.DefaultStyleTrigger))
        {
            return;
        }

        if (control is TextBoxBase or PasswordBox)
        {
            control.FocusVisualStyle = null;
            return;
        }

        control.SetResourceReference(FrameworkElement.FocusVisualStyleProperty, "AppFocusVisual");
    }

    /// <summary>Текст подсказки, чем бы она ни была: строкой или <see cref="ToolTip"/> со строкой внутри.</summary>
    private sealed class ToolTipText : IValueConverter
    {
        public static readonly ToolTipText Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
        {
            string text => text,
            ToolTip { Content: string text } => text,
            _ => ""
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
