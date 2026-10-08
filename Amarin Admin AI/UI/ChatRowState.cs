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

    /// <summary>
    /// Место строки в карточке раскрытой папки (или архива): заголовок, середина, низ. Шаблон
    /// рисует по нему свой кусок общей подложки — вместе куски дают одну скруглённую карточку.
    /// </summary>
    /// <remarks>
    /// Строки остаются прямыми детьми списка, а не уходят в контейнер папки: на этом стоят
    /// подсветка открытого чата, выбор диапазона Shift-щелчком и переходы по Ctrl+Tab. До 1.28.0
    /// чаты папки стояли вровень с остальными, потом — с отступом и волосяной линией, и в обоих
    /// случаях было не видно, где папка кончается.
    /// </remarks>
    public static readonly DependencyProperty BandProperty =
        DependencyProperty.RegisterAttached(
            "Band",
            typeof(FolderBand),
            typeof(ChatRowState),
            new PropertyMetadata(FolderBand.None));

    public static void SetBand(DependencyObject element, FolderBand value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BandProperty, value);
    }

    public static FolderBand GetBand(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (FolderBand)element.GetValue(BandProperty);
    }

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

    /// <summary>Чат выбран для пакетного действия (Ctrl/Shift+щелчок, D5).</summary>
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.RegisterAttached("IsSelected", typeof(bool), typeof(ChatRowState), new PropertyMetadata(false));

    public static void SetIsSelected(DependencyObject element, bool value) => Set(element, IsSelectedProperty, value);

    public static bool GetIsSelected(DependencyObject element) => Get(element, IsSelectedProperty);

    /// <summary>
    /// Колонка едет сама (инерция колеса): строка не откликается на наведение — ни подсветкой,
    /// ни кнопкой «⋯», ни подсказкой (1.32.0).
    /// </summary>
    /// <remarks>
    /// Под неподвижной мышью строки проезжают одна за другой, и каждая раскладывала заново
    /// заголовок и кнопку: прокрутка над строками дёргалась, а над пустым местом у полосы шла
    /// гладко. Ставит его окно по <see cref="SmoothScroll.IsInMotionProperty"/> колонки.
    /// </remarks>
    public static readonly DependencyProperty ScrollingProperty =
        DependencyProperty.RegisterAttached("Scrolling", typeof(bool), typeof(ChatRowState), new PropertyMetadata(false));

    public static void SetScrolling(DependencyObject element, bool value) => Set(element, ScrollingProperty, value);

    public static bool GetScrolling(DependencyObject element) => Get(element, ScrollingProperty);

    /// <summary>
    /// Кисти тегов строки — до трёх точек перед названием (D5). Кисть ставится ссылкой на ресурс
    /// (<c>SetResourceReference</c>), поэтому точки перекрашиваются вместе с темой.
    /// </summary>
    public static readonly DependencyProperty Tag1Property =
        DependencyProperty.RegisterAttached("Tag1", typeof(System.Windows.Media.Brush), typeof(ChatRowState), new PropertyMetadata(null));

    public static readonly DependencyProperty Tag2Property =
        DependencyProperty.RegisterAttached("Tag2", typeof(System.Windows.Media.Brush), typeof(ChatRowState), new PropertyMetadata(null));

    public static readonly DependencyProperty Tag3Property =
        DependencyProperty.RegisterAttached("Tag3", typeof(System.Windows.Media.Brush), typeof(ChatRowState), new PropertyMetadata(null));

    public static System.Windows.Media.Brush? GetTag1(DependencyObject element) => (System.Windows.Media.Brush?)element.GetValue(Tag1Property);

    public static void SetTag1(DependencyObject element, System.Windows.Media.Brush? value) => element.SetValue(Tag1Property, value);

    public static System.Windows.Media.Brush? GetTag2(DependencyObject element) => (System.Windows.Media.Brush?)element.GetValue(Tag2Property);

    public static void SetTag2(DependencyObject element, System.Windows.Media.Brush? value) => element.SetValue(Tag2Property, value);

    public static System.Windows.Media.Brush? GetTag3(DependencyObject element) => (System.Windows.Media.Brush?)element.GetValue(Tag3Property);

    public static void SetTag3(DependencyObject element, System.Windows.Media.Brush? value) => element.SetValue(Tag3Property, value);

    /// <summary>
    /// Раздел, в который попадёт чат, брошенный на эту строку или заголовок (<see cref="Core.ChatDropTarget"/>).
    /// Пусто — сюда не бросают (заголовок «Папки», выдача поиска).
    /// </summary>
    public static readonly DependencyProperty DropTargetProperty =
        DependencyProperty.RegisterAttached("DropTarget", typeof(object), typeof(ChatRowState), new PropertyMetadata(null));

    internal static Core.ChatDropTarget? GetDropTarget(DependencyObject element) =>
        element.GetValue(DropTargetProperty) as Core.ChatDropTarget?;

    internal static void SetDropTarget(DependencyObject element, Core.ChatDropTarget? value) =>
        element.SetValue(DropTargetProperty, value);

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

/// <summary>Кусок карточки раскрытой папки, который рисует строка.</summary>
public enum FolderBand
{
    /// <summary>Строка вне карточки.</summary>
    None,

    /// <summary>Заголовок раскрытой папки — верх карточки.</summary>
    Top,

    /// <summary>Чат в середине папки.</summary>
    Middle,

    /// <summary>Последний чат папки — низ карточки.</summary>
    Bottom,

    /// <summary>Заголовок свёрнутой или пустой папки — карточка из одной строки.</summary>
    Single
}
