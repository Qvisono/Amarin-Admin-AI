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

    /// <summary>Ширина, которую сообщение получило на последней раскладке.</summary>
    private double _measuredWidth = double.NaN;

    /// <summary>Ширина, на которой сообщение расставлено последний раз.</summary>
    private double _arrangedWidth = double.NaN;

    /// <summary>Ширина, которую держит замороженное сообщение; NaN — не заморожено.</summary>
    private double _frozenWidth = double.NaN;

    private double _frozenArrangeWidth = double.NaN;

    /// <summary>Сообщение держит прежнюю ширину, пока окно меняет размер (см. <see cref="Freeze"/>).</summary>
    public bool IsFrozen => !double.IsNaN(_frozenWidth);

    /// <summary>
    /// Ширина, на которой разложен текст сообщения, — по ней запоминается его высота.
    /// </summary>
    public double LaidOutWidth => IsFrozen ? _frozenWidth : _measuredWidth;

    /// <summary>
    /// Держать сообщение на прежней ширине, что бы ни случилось с лентой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Каждое сообщение — <c>RichTextBox</c> с целым документом, и на смене ширины окна WPF
    /// перекладывает их все: на чате в 1200 сообщений шаг перетаскивания края занимал 1,7
    /// секунды. Замороженное сообщение получает от хоста ровно ту же ширину, что и в прошлый
    /// раз, и WPF пропускает его раскладку целиком — тот же размер, ничего не пересчитывается,
    /// ни <c>SizeChanged</c>, ни новой ширины страницы.
    /// </para>
    /// <para>
    /// Высота замороженного не меняется, поэтому замораживать можно всё, чего человек сейчас не
    /// видит: то, что он читает, остаётся на месте. Отпускает <see cref="Thaw"/>.
    /// </para>
    /// </remarks>
    /// <returns><c>true</c> — заморозили; недостроенное и ни разу не разложенное не замораживается.</returns>
    public bool Freeze()
    {
        if (Child is null || IsFrozen || double.IsNaN(_measuredWidth) || double.IsNaN(_arrangedWidth))
        {
            return false;
        }

        _frozenWidth = _measuredWidth;
        _frozenArrangeWidth = _arrangedWidth;
        return true;
    }

    /// <summary>Вернуть сообщению ширину ленты: следующая раскладка переложит его по ней.</summary>
    public bool Thaw()
    {
        if (!IsFrozen)
        {
            return false;
        }

        _frozenWidth = double.NaN;
        _frozenArrangeWidth = double.NaN;
        InvalidateMeasure();
        return true;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            return base.MeasureOverride(constraint);
        }

        if (IsFrozen)
        {
            // Тот же размер, что в прошлый раз, — и UIElement.Measure вернётся сразу.
            child.Measure(new Size(_frozenWidth, constraint.Height));
            return new Size(Math.Min(child.DesiredSize.Width, constraint.Width), child.DesiredSize.Height);
        }

        _measuredWidth = constraint.Width;
        child.Measure(constraint);
        return child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        if (Child is not { } child)
        {
            return base.ArrangeOverride(arrangeSize);
        }

        if (IsFrozen)
        {
            child.Arrange(new Rect(0, 0, _frozenArrangeWidth, arrangeSize.Height));
            return arrangeSize;
        }

        _arrangedWidth = arrangeSize.Width;
        child.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
