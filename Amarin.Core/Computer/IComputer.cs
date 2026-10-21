namespace Amarin.Core;

/// <summary>
/// Экран, окна, мышь и клавиатура этого ПК — порт для инструмента <c>computer</c>.
/// </summary>
/// <remarks>
/// Исполняет его приложение: UI Automation живёт в сборках рабочего стола Windows, а ядро
/// собрано без WPF. Поэтому здесь только простые типы, а решения — что можно трогать, куда
/// нажимать, когда отказать — принимает ядро (<see cref="ComputerSafety"/>,
/// <see cref="Tools.ComputerTool"/>). Порт ничего не решает и ничего не запрещает сам.
/// </remarks>
internal interface IComputer
{
    /// <summary>Открыт ли сеанс человека или на экране вход в Windows и вопрос UAC.</summary>
    ComputerDesk Desk();

    IReadOnlyList<ComputerMonitor> Monitors();

    /// <summary>Видимые окна верхнего уровня, сверху вниз.</summary>
    IReadOnlyList<ComputerWindow> Windows();

    ComputerWindow? Window(long handle);

    /// <summary>Окно верхнего уровня, которому достанется нажатие в эту точку.</summary>
    ComputerWindow? WindowAt(ScreenPoint point);

    ComputerWindow? Foreground();

    /// <summary>Элемент с фокусом клавиатуры. Null — его нет или он не виден UI Automation.</summary>
    ComputerElement? Focused();

    /// <summary>
    /// Дерево окна сверху вниз: не больше <paramref name="limit"/> элементов и не дольше
    /// <paramref name="budget"/>. Упёрлось в любое из двух — дерево помечено обрезанным.
    /// </summary>
    /// <remarks>
    /// Срок нужен браузеру: в его окне тысячи элементов, и полное дерево на каждое нажатие
    /// стоило секунды. Обрезанное дерево модель дочитывает поиском или снимком.
    /// </remarks>
    Task<ComputerTree> InspectAsync(long window, int limit, TimeSpan budget, CancellationToken cancellationToken);

    /// <summary>Тот же элемент сейчас: свежее место на экране. Null — его больше нет.</summary>
    Task<ComputerElement?> ElementAsync(long window, string key, CancellationToken cancellationToken);

    /// <summary>Снимок области, ужатый до <paramref name="maxSide"/> по длинной стороне.</summary>
    Task<ComputerShot> CaptureAsync(ScreenRect area, int maxSide, CancellationToken cancellationToken);

    Task PointerAsync(ComputerPointer action, CancellationToken cancellationToken);

    /// <summary>Печатает текст туда, где фокус клавиатуры.</summary>
    Task TypeAsync(string text, CancellationToken cancellationToken);

    /// <summary>Сочетание клавиш: имена из <see cref="ComputerKeys"/>, модификаторы первыми.</summary>
    Task KeysAsync(IReadOnlyList<string> chord, CancellationToken cancellationToken);

    Task<bool> FocusWindowAsync(long window, CancellationToken cancellationToken);

    Task<bool> FocusElementAsync(long window, string key, CancellationToken cancellationToken);

    /// <summary>Нажатие через UI Automation — для элемента без точки на экране.</summary>
    Task<bool> InvokeAsync(long window, string key, CancellationToken cancellationToken);

    /// <summary>Ждёт, пока окно перестанет меняться после действия, но не дольше <paramref name="max"/>.</summary>
    Task SettleAsync(TimeSpan max, CancellationToken cancellationToken);

    /// <summary>Текст в буфере обмена — для правила о вставке (<see cref="ComputerSafety.Paste"/>). Null — текста нет.</summary>
    string? ClipboardText();

    /// <summary>
    /// Держит систему и экран включёнными, пока результат не отпустят, и будит погасший экран.
    /// </summary>
    /// <remarks>
    /// Снимок погасшего монитора выходит чёрным, а уснувший посреди работы ПК оборвал бы задачу,
    /// оставленную человеком без присмотра. Держит только сеанс управления, не дольше.
    /// </remarks>
    IDisposable Hold();
}

/// <summary>
/// Что видит человек, пока агент держит мышь: плашка со шагом и «Стоп», подсветка «Показать»,
/// отсчёт перед началом без человека.
/// </summary>
/// <remarks>
/// Отдельно от <see cref="IComputer"/>: тот — операционная система, этот — окна самой программы.
/// В тестах без окна его нет вовсе, и тогда управление идёт без плашки, а «Показать» отказывает:
/// показывать некому.
/// </remarks>
internal interface IComputerStage
{
    /// <summary>
    /// Начало управления: окно программы уходит в плашку, без человека сперва идёт отсчёт.
    /// </summary>
    /// <returns>false — человек отменил на отсчёте.</returns>
    Task<bool> BeginAsync(ComputerStart start, CancellationToken cancellationToken);

    /// <summary>Текущий шаг на плашке.</summary>
    void Step(int step, string text);

    /// <summary>Плашка уходит от точки, куда сейчас нажмут: она поверх всех и забрала бы нажатие.</summary>
    void Avoid(ScreenPoint point);

    /// <summary>Рамка с подписью у области; ждёт «Готово» от человека.</summary>
    /// <returns>true — человек нажал «Готово», false — вышло время.</returns>
    Task<bool> ShowAsync(ScreenRect area, string caption, TimeSpan wait, CancellationToken cancellationToken);

    /// <summary>Конец управления: плашка уходит, окно программы возвращается, если его сворачивали.</summary>
    void End();
}
