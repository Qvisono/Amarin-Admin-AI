using System.Reflection;
using System.Windows.Controls.Primitives;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Курсор, спрятанный полем ввода на время набора, возвращается любым движением мыши в
/// программе, а не только над тем полем.
/// </summary>
/// <remarks>
/// Прятанье и показ держит флаг потока внутри WPF (<c>TextEditor._ThreadLocalStore.HideCursor</c>).
/// Поле, ушедшее из-под неподвижной мыши после Enter, больше не получает <c>MouseMove</c>, и флаг
/// вместе со счётчиком показа оставался взведённым — курсор пропадал над всей программой.
/// </remarks>
[Collection(WpfCollection.Name)]
public sealed class CursorGuardTests
{
    private readonly WpfFixture _wpf;

    public CursorGuardTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void The_wpf_method_that_shows_the_cursor_is_found()
    {
        // Сторож на обновление WPF: метод внутренний, и если его переименуют, страж молча
        // перейдёт на запасной путь — лучше узнать об этом тестом.
        Assert.NotNull(CursorGuard.FindWpfShowCursor());
    }

    [Fact]
    public void A_mouse_move_anywhere_clears_the_hidden_flag()
    {
        var cleared = _wpf.Ui.Invoke(() =>
        {
            var store = ThreadLocalStore();
            var flag = store.GetType().GetProperty("HideCursor", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

            // Ровно то состояние, в котором поле оставляет поток после набора: флаг взведён,
            // счётчик показа опущен. Счётчик опускаем сами, чтобы _ShowCursor было что вернуть.
            flag.SetValue(store, true);
            ShowCursor(false);
            CursorGuard.Install();

            CursorGuard.OnMouseMoved();

            return !(bool)flag.GetValue(store)!;
        });

        Assert.True(cleared);
    }

    private static object ThreadLocalStore()
    {
        var editor = typeof(TextBoxBase).Assembly.GetType("System.Windows.Documents.TextEditor")!;
        var property = editor.GetProperty("_ThreadLocalStore", BindingFlags.Static | BindingFlags.NonPublic)!;
        return property.GetValue(null)!;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);
}
