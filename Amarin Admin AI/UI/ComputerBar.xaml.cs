using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>Что сейчас на плашке.</summary>
internal enum ComputerBarMode
{
    /// <summary>Агент работает: текущий шаг и «Стоп».</summary>
    Step,

    /// <summary>Без человека: через сколько секунд агент возьмёт мышь, и «Отмена».</summary>
    Countdown,

    /// <summary>Вопрос о нажатии: «Да» и «Нет».</summary>
    Question,

    /// <summary>«Показать»: что нажать человеку, «Готово» и «Стоп».</summary>
    Show
}

/// <summary>
/// Плашка управления компьютером: пока агент держит мышь, окно программы свёрнуто в неё.
/// </summary>
/// <remarks>
/// <para>
/// Не активируется (<c>WS_EX_NOACTIVATE</c>): нажатие на «Стоп» или «Да» не отнимает фокус у окна,
/// в котором агент печатает. И не попадает в снимки (<c>WDA_EXCLUDEFROMCAPTURE</c>): иначе модель
/// видела бы плашку поверх окна и пыталась бы в неё нажать.
/// </para>
/// <para>
/// Ставится в физических пикселях через <c>SetWindowPos</c>, а не свойствами <c>Left</c>/<c>Top</c>: у
/// мониторов разный масштаб, а плашка переезжает на тот, где работает агент.
/// </para>
/// </remarks>
public partial class ComputerBar : Window
{
    private const int EdgeGap = 10;

    private ScreenRect _work;
    private bool _bottom;

    public ComputerBar()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Place();
    }

    internal ComputerBarMode Mode { get; private set; }

    /// <summary>Нажата главная кнопка: «Да» или «Готово».</summary>
    internal event Action? Primary;

    /// <summary>Нажата вторая кнопка: «Стоп», «Отмена» или «Нет».</summary>
    internal event Action? Secondary;

    /// <summary>Стоит ли плашка у нижнего края — чтобы тест и <see cref="Avoid"/> знали, где она.</summary>
    internal bool AtBottom => _bottom;

    internal void ShowStep(int step, string text)
    {
        Mode = ComputerBarMode.Step;
        Fill(Loc.Get("S.ComputerBar.Title"), step > 0 ? Loc.Format("S.ComputerBar.Step", step) : "", text,
            primary: null, secondary: Loc.Get("S.ComputerBar.Stop"));
    }

    internal void ShowCountdown(int seconds, string task)
    {
        Mode = ComputerBarMode.Countdown;
        Fill(Loc.Format("S.ComputerBar.Countdown", seconds), "", task, primary: null, secondary: Loc.Get("S.Common.Cancel"));
    }

    internal void ShowQuestion(string line)
    {
        Mode = ComputerBarMode.Question;
        Fill(Loc.Get("S.ComputerBar.Ask"), "", line, Loc.Get("S.Common.Yes"), Loc.Get("S.Common.No"));
    }

    internal void ShowHint(string caption)
    {
        Mode = ComputerBarMode.Show;
        Fill(Loc.Get("S.ComputerBar.ShowTitle"), "", caption, Loc.Get("S.ComputerBar.Done"), Loc.Get("S.ComputerBar.Stop"));
    }

    private void Fill(string heading, string step, string body, string? primary, string secondary)
    {
        HeadingText.Text = heading;
        StepText.Text = step;
        BodyText.Text = body;
        PrimaryButton.Content = primary;
        PrimaryButton.Visibility = primary is null ? Visibility.Collapsed : Visibility.Visible;
        SecondaryButton.Content = secondary;
    }

    /// <summary>Встать у верхнего края этой рабочей области (физические пиксели).</summary>
    internal void PlaceOn(ScreenRect work)
    {
        _work = work;
        _bottom = false;
        Place();
    }

    /// <summary>
    /// Уходит от точки, куда сейчас нажмут: к другому краю, а если точка на другом мониторе —
    /// на его верхний край. Иначе плашка поверх всех окон забрала бы нажатие себе.
    /// </summary>
    internal void Avoid(ScreenPoint point, ScreenRect workOfPoint)
    {
        if (workOfPoint != _work && !workOfPoint.IsEmpty)
        {
            PlaceOn(workOfPoint);
        }

        if (Bounds() is { IsEmpty: false } bounds && bounds.Contains(point))
        {
            _bottom = !_bottom;
            Place();
        }
    }

    /// <summary>Где плашка сейчас, в физических пикселях.</summary>
    internal ScreenRect Bounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle != IntPtr.Zero && GetWindowRect(handle, out var rect)
            ? new ScreenRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : default;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        ToastScreen.MakeQuiet(handle);

        // Не в снимках оператора. До Windows 10 2004 значения нет — тогда плашка просто видна.
        _ = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
        Place();
    }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || _work.IsEmpty || !GetWindowRect(handle, out var rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = _work.Left + (_work.Width - width) / 2;
        var y = _bottom ? _work.Bottom - height - EdgeGap : _work.Top + EdgeGap;
        _ = SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e) => Primary?.Invoke();

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => Secondary?.Invoke();

    private const uint WdaExcludeFromCapture = 0x11;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
