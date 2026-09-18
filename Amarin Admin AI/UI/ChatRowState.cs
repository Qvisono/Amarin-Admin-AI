using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Состояние строки в списке чатов: открыт ли этот чат, идёт ли в нём ход, доспел ли ответ.
/// </summary>
/// <remarks>
/// Присоединённые свойства, а не поля модели, потому что список чатов собирается кодом
/// (<c>RefreshChatList</c>) из голых <see cref="System.Windows.Controls.Button"/>: привязываться
/// не к чему, а шаблону нужен триггер. Само состояние живёт не здесь — его держит окно, а сюда
/// значения только переносятся.
/// <para>
/// Ходов может идти до трёх, и завершившийся в фоне ответ до сих пор было видно только по тосту —
/// а его глушат, когда окно в фокусе. Человек возвращался в список и гадал, какой из чатов ответил.
/// </para>
/// </remarks>
public static class ChatRowState
{
    public static readonly DependencyProperty NeedsAttentionProperty =
        DependencyProperty.RegisterAttached(
            "NeedsAttention",
            typeof(bool),
            typeof(ChatRowState),
            new PropertyMetadata(false));

    /// <summary>Этот чат сейчас открыт. Прежде это была вторая копия стиля строки.</summary>
    /// <remarks>
    /// Отдельный стиль означал, что переключение чата меняет стиль двум кнопкам — а так как
    /// панель пересобиралась целиком по любому расхождению подписи, на деле пересоздавались
    /// все строки со всеми их шаблонами. Признаком в шаблоне это стоит одного булева значения.
    /// </remarks>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.RegisterAttached(
            "IsActive",
            typeof(bool),
            typeof(ChatRowState),
            new PropertyMetadata(false));

    /// <summary>В этом чате идёт ход. Зажигает пульсирующую точку.</summary>
    /// <remarks>
    /// Пульс заводится и гасится триггером на этом признаке. Прежде он висел на
    /// <c>Loaded</c> самой точки — а <c>Loaded</c> случается и у свёрнутой, так что вечная
    /// анимация запускалась на каждой строке списка, и после каждой перерисовки панели
    /// заводился новый их комплект.
    /// </remarks>
    public static readonly DependencyProperty IsWorkingProperty =
        DependencyProperty.RegisterAttached(
            "IsWorking",
            typeof(bool),
            typeof(ChatRowState),
            new PropertyMetadata(false));

    /// <summary>Чат закреплён. Признак читает меню действий строки.</summary>
    /// <remarks>
    /// На строке, а не в замыкании обработчика: обработчик теперь один на всю панель, и узнать
    /// закрепление ему неоткуда, кроме самой строки.
    /// </remarks>
    public static readonly DependencyProperty IsPinnedProperty =
        DependencyProperty.RegisterAttached(
            "IsPinned",
            typeof(bool),
            typeof(ChatRowState),
            new PropertyMetadata(false));

    public static void SetIsPinned(DependencyObject element, bool value) =>
        Set(element, IsPinnedProperty, value);

    public static bool GetIsPinned(DependencyObject element) =>
        Get(element, IsPinnedProperty);

    public static void SetNeedsAttention(DependencyObject element, bool value) =>
        Set(element, NeedsAttentionProperty, value);

    public static bool GetNeedsAttention(DependencyObject element) =>
        Get(element, NeedsAttentionProperty);

    public static void SetIsActive(DependencyObject element, bool value) =>
        Set(element, IsActiveProperty, value);

    public static bool GetIsActive(DependencyObject element) =>
        Get(element, IsActiveProperty);

    public static void SetIsWorking(DependencyObject element, bool value) =>
        Set(element, IsWorkingProperty, value);

    public static bool GetIsWorking(DependencyObject element) =>
        Get(element, IsWorkingProperty);

    private static void Set(DependencyObject element, DependencyProperty property, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(property, value);
    }

    private static bool Get(DependencyObject element, DependencyProperty property)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(property);
    }
}
