using System.Windows;

namespace Amarin.UI;

/// <summary>
/// Признак «в этом чате доспел ответ, пока смотрели другой» — на самой строке списка.
/// </summary>
/// <remarks>
/// Присоединённое свойство, а не поле модели, потому что список чатов собирается кодом
/// (<c>RefreshChatList</c>) из голых <see cref="System.Windows.Controls.Button"/>: привязываться
/// не к чему, а шаблону нужен триггер. Само состояние живёт не здесь: строки пересоздаются при
/// каждой перерисовке панели, поэтому набор «ждущих внимания» чатов держит окно
/// (<c>MainWindow._attention</c>), а сюда значение только переносится.
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

    public static void SetNeedsAttention(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(NeedsAttentionProperty, value);
    }

    public static bool GetNeedsAttention(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(NeedsAttentionProperty);
    }
}
