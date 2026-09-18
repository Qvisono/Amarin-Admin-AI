using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Место одного сообщения в ленте: сперва только занятая высота, потом — построенная вьюшка.
/// </summary>
/// <remarks>
/// Лента — обычный <see cref="StackPanel"/> без виртуализации, и открытие чата строило все
/// сообщения разом: на один ответ это несколько десятков объектов, <c>RichTextBox</c> с целым
/// <c>FlowDocument</c> и ряд кнопок с иконками. На длинной переписке переключение чата
/// подвисало именно здесь.
/// <para>
/// Хост занимает столько же места, сколько займёт сообщение, поэтому полоса прокрутки честна
/// с первого кадра, а построенное сообщение не сдвигает соседей. Высота берётся из памяти
/// окна, если это сообщение уже показывали, и оценивается по длине текста, если нет.
/// </para>
/// </remarks>
internal sealed class ChatMessageHost : Decorator
{
    public required ChatDisplayMessage Message { get; init; }

    /// <summary>Действия того чата, которому сообщение принадлежит.</summary>
    public required MessageActions Actions { get; init; }

    public string Id => Message.Id ?? "";

    /// <summary>Вьюшка уже построена.</summary>
    public bool IsMaterialized => Child is not null;

    /// <summary>
    /// Сколько места было занято под непостроенное сообщение. Отдельным полем, потому что
    /// <see cref="FrameworkElement.Height"/> после постройки становится <c>NaN</c>.
    /// </summary>
    public double Reserved { get; private set; }

    /// <summary>Занять место, ничего не строя.</summary>
    public void Reserve(double height)
    {
        Reserved = Math.Max(1, height);
        Height = Reserved;
    }

    /// <summary>Поставить построенную вьюшку и отпустить резерв.</summary>
    public void Fill(FrameworkElement view)
    {
        Child = view;
        ClearValue(HeightProperty);
    }
}
