using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Возвращает курсор мыши, который WPF спрятал на время набора и забыл показать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// При включённом в Windows «скрывать указатель при вводе текста» поле ввода WPF на первом
    /// же символе зовёт <c>ShowCursor(false)</c> (<c>TextEditorTyping.HideCursor</c>), а обратно
    /// показывает курсор только из <c>MouseMove</c> и <c>MouseLeave</c> <b>того же</b> поля. Если
    /// поле уходит из-под неподвижной мыши — правка сообщения закончилась Enter и пузырь
    /// пересобран, композер сжался после отправки, поверх лёг оверлей, — ни одно из двух событий
    /// до него уже не дойдёт. Счётчик показа у потока интерфейса остаётся отрицательным, и
    /// курсора нет над всеми окнами программы, пока мышь случайно не пройдёт над другим полем.
    /// </para>
    /// <para>
    /// Лечение — сделать то, что поле сделало бы само: на любое движение мыши в программе
    /// позвать тот же WPF-овский <c>_ShowCursor</c>. Он проверяет собственный флаг, так что
    /// лишний вызов ничего не стоит, и счётчик никогда не уезжает в плюс — «прятать при вводе»
    /// продолжает работать как задумано. Метод внутренний, поэтому берётся отражением; если
    /// в новой версии WPF его не окажется, работает запасной путь через <c>GetCursorInfo</c>.
    /// </para>
    /// </remarks>
    internal static class CursorGuard
    {
        private const int CursorShowing = 0x1;
        private const int CursorSuppressed = 0x2;

        /// <summary>Реже этого запасной путь курсор не проверяет: он зовёт систему, а не флаг.</summary>
        private const long ProbeIntervalMs = 150;

        private static Action? _wpfShowCursor;
        private static bool _installed;
        private static long _lastProbe;

        /// <summary>Ставит страж на поток интерфейса. Повторный вызов ничего не делает.</summary>
        public static void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            _wpfShowCursor = FindWpfShowCursor();
            InputManager.Current.PostProcessInput += OnPostProcessInput;
        }

        /// <summary>
        /// Подключает окно: при уходе фокуса и сворачивании курсор возвращается сразу, а
        /// забытые захват мыши и подменённая форма курсора снимаются.
        /// </summary>
        public static void Attach(Window window)
        {
            window.Deactivated += (_, _) => Release();
            window.StateChanged += (_, _) =>
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    Release();
                }
            };
        }

        /// <summary>Показывающий курсор метод WPF; <c>null</c> — в этой версии его нет.</summary>
        internal static Action? FindWpfShowCursor()
        {
            try
            {
                var method = typeof(TextBoxBase).Assembly
                    .GetType("System.Windows.Documents.TextEditorTyping")?
                    .GetMethod("_ShowCursor", BindingFlags.Static | BindingFlags.NonPublic, Type.EmptyTypes);
                return method?.CreateDelegate<Action>();
            }
            catch (Exception ex) when (ex is ArgumentException or MemberAccessException or AmbiguousMatchException)
            {
                return null;
            }
        }

        /// <summary>Мышь сдвинулась над окном программы.</summary>
        internal static void OnMouseMoved()
        {
            if (PerfLog.IsEnabled && IsHidden())
            {
                PerfLog.Write("cursor_restored on_move");
            }

            if (_wpfShowCursor is { } show)
            {
                show();
                return;
            }

            var now = Environment.TickCount64;
            if (now - _lastProbe < ProbeIntervalMs)
            {
                return;
            }

            _lastProbe = now;
            ShowIfHidden();
        }

        private static void OnPostProcessInput(object sender, ProcessInputEventArgs e)
        {
            if (ReferenceEquals(e.StagingItem.Input.RoutedEvent, Mouse.PreviewMouseMoveEvent))
            {
                OnMouseMoved();
            }
        }

        private static void Release()
        {
            _wpfShowCursor?.Invoke();
            if (_wpfShowCursor is null)
            {
                ShowIfHidden();
            }

            Mouse.OverrideCursor = null;
            if (Mouse.Captured is not null)
            {
                Mouse.Capture(null);
            }
        }

        /// <summary>Запасной путь: поднимает счётчик показа ровно до нуля, не выше.</summary>
        private static void ShowIfHidden()
        {
            if (!IsHidden())
            {
                return;
            }

            // Потолок — страховка: каждый вызов прибавляет единицу, и чужой счётчик глубже
            // минус шестнадцати означает, что курсор прячут нарочно.
            for (var i = 0; i < 16 && ShowCursor(true) < 0; i++)
            {
            }
        }

        /// <summary>
        /// Курсор скрыт счётчиком показа. Подавленный сенсорным вводом — не скрыт: его вернёт
        /// первое же движение мыши, и трогать счётчик ради него нельзя.
        /// </summary>
        private static bool IsHidden()
        {
            var info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
            return GetCursorInfo(ref info) &&
                   (info.Flags & (CursorShowing | CursorSuppressed)) == 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int Size;
            public int Flags;
            public IntPtr Cursor;
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorInfo(ref CursorInfo info);

        [DllImport("user32.dll")]
        private static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool show);
    }
}
