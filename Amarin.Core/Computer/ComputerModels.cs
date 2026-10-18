namespace Amarin.Core;

/// <summary>Точка на виртуальном рабочем столе — в физических пикселях, как их видит <c>SendInput</c>.</summary>
internal readonly record struct ScreenPoint(int X, int Y);

/// <summary>Прямоугольник на виртуальном рабочем столе, в физических пикселях.</summary>
/// <remarks>
/// Физических, а не логических: мониторы бывают с разным масштабом, и в логических точках у
/// окна на втором мониторе были бы другие координаты, чем у снимка того же места. Процесс
/// PerMonitorV2, поэтому и Windows, и UI Automation отдают ему настоящие пиксели.
/// </remarks>
internal readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;

    public int Bottom => Top + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public ScreenPoint Center => new(Left + Width / 2, Top + Height / 2);

    public bool Contains(ScreenPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    /// <summary>Общая часть двух прямоугольников; пустая, если они не пересекаются.</summary>
    public ScreenRect Intersect(ScreenRect other)
    {
        var left = Math.Max(Left, other.Left);
        var top = Math.Max(Top, other.Top);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right <= left || bottom <= top ? default : new ScreenRect(left, top, right - left, bottom - top);
    }

    public override string ToString() => $"{Left},{Top} {Width}x{Height}";
}

/// <summary>Монитор: где он на виртуальном столе и какой у него масштаб.</summary>
/// <param name="Index">Номер с единицы, как модель называет монитор.</param>
internal sealed record ComputerMonitor(int Index, ScreenRect Bounds, bool Primary, double Scale = 1.0);

/// <summary>Что за окно — для правил, которые модель не обходит (<see cref="ComputerSafety"/>).</summary>
internal enum ComputerWindowKind
{
    Normal,

    /// <summary>Консоль или терминал: команды идут только через <c>run_powershell</c>.</summary>
    Console,

    /// <summary>Окно «Выполнить» и ему подобные: строка в нём запускает программу.</summary>
    RunDialog,

    /// <summary>Проводник: Delete в нём удаляет файлы.</summary>
    Explorer,

    /// <summary>Окно выбора файла: в нём тоже Проводник.</summary>
    FileDialog
}

/// <summary>Окно верхнего уровня.</summary>
/// <param name="Handle">HWND. По нему окно называется модели (<see cref="Id"/>).</param>
/// <param name="Monitor">Номер монитора, на котором лежит большая часть окна.</param>
/// <param name="Elevated">Процесс окна запущен от администратора.</param>
internal sealed record ComputerWindow(
    long Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    ScreenRect Bounds,
    int Monitor,
    ComputerWindowKind Kind = ComputerWindowKind.Normal,
    bool Elevated = false,
    bool Minimized = false,
    bool Foreground = false)
{
    /// <summary>Имя окна для модели: стабильное, пока окно живо, — от HWND, а не от порядка в списке.</summary>
    public string Id => "w" + Handle.ToString("X", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Разбирает имя окна, которое модель взяла из списка. Null — имя не наше.</summary>
    public static long? ParseId(string? id)
    {
        var text = (id ?? "").Trim();
        if (text.Length < 2 || (text[0] != 'w' && text[0] != 'W'))
        {
            return null;
        }

        return long.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var handle) && handle != 0
            ? handle
            : null;
    }
}

/// <summary>Элемент окна из UI Automation.</summary>
/// <param name="Key">
/// Ключ порта (RuntimeId UI Automation). Модели не показывается: она видит короткий id, который
/// выдаёт <see cref="ComputerSession"/>.
/// </param>
/// <param name="Depth">Глубина в дереве окна, с нуля, — для отступов в выдаче.</param>
/// <param name="Role">Тип элемента: button, edit, list item и так далее.</param>
/// <param name="ClickPoint">
/// Точка, в которую элемент принимает нажатие. Null — у элемента её нет (закрыт или за краем),
/// и тогда нажатие идёт через UI Automation, а не мышью.
/// </param>
internal sealed record ComputerElement(
    string Key,
    int Depth,
    string Role,
    string Name,
    string? Value,
    ScreenRect Bounds,
    bool Enabled = true,
    bool Focused = false,
    bool IsPassword = false,
    bool Offscreen = false,
    ScreenPoint? ClickPoint = null)
{
    /// <summary>Куда нажимать мышью: своя точка элемента или середина прямоугольника.</summary>
    public ScreenPoint? Target => ClickPoint ?? (Offscreen || Bounds.IsEmpty ? null : Bounds.Center);
}

/// <summary>Снимок области экрана.</summary>
/// <param name="Width">Ширина картинки, которую увидит модель, — после ужатия.</param>
/// <param name="Source">Какая область экрана снята, в физических пикселях.</param>
internal sealed record ComputerShot(string PngBase64, int Width, int Height, ScreenRect Source);

/// <summary>Чей сейчас рабочий стол.</summary>
internal enum ComputerDesk
{
    /// <summary>Сеанс человека открыт — управлять можно.</summary>
    Open,

    /// <summary>Windows заблокирована: экран входа, ввод туда не доходит.</summary>
    Locked,

    /// <summary>Защищённый рабочий стол (вопрос UAC): программам он закрыт.</summary>
    Secure
}

/// <summary>Действие указателем.</summary>
internal enum ComputerPointerKind
{
    Click,
    DoubleClick,
    RightClick,
    Scroll,
    Drag
}

/// <summary>Что сделать указателем, куда и насколько.</summary>
/// <param name="To">Конец перетаскивания.</param>
/// <param name="ScrollX">Щелчки колеса по горизонтали: плюс — вправо.</param>
/// <param name="ScrollY">Щелчки колеса по вертикали: плюс — вниз.</param>
internal sealed record ComputerPointer(
    ComputerPointerKind Kind,
    ScreenPoint At,
    ScreenPoint? To = null,
    int ScrollX = 0,
    int ScrollY = 0);

/// <summary>Поручение оператору: сделать самому или показать человеку.</summary>
internal enum ComputerMode
{
    Do,

    /// <summary>Нажимает человек; оператор только смотрит и подсвечивает (<c>show</c>).</summary>
    Show
}

/// <summary>С чем начинается управление — для плашки и отсчёта.</summary>
/// <param name="Unattended">Человека рядом нет: перед началом нужен отсчёт с отменой.</param>
internal sealed record ComputerStart(ComputerMode Mode, bool Unattended, int MaxSteps, string Task);
