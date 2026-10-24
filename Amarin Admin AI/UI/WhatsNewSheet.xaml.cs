using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// «Что нового» (1.37.0): заметки к версии одной карточкой поверх окна, раз на версию.
/// </summary>
/// <remarks>
/// Прежде заметки шли в общее уведомление: шириной в четыреста точек, рамкой в рамке и второй
/// прокруткой внутри первой — читать их было неудобно. Создаётся при первом показе, как и
/// остальные редкие окна: на первом кадре её не видно.
/// </remarks>
public partial class WhatsNewSheet : UserControl
{
    public WhatsNewSheet() => InitializeComponent();

    /// <summary>Карточку закрыли — «Понятно», крестиком, Esc или щелчком мимо.</summary>
    internal event Action? Closed;

    /// <param name="host">Окно, у которого <see cref="ChatMarkdown"/> берёт ресурсы ссылок и кода.</param>
    /// <param name="englishInterface">Интерфейс по-английски — тогда оговорка о языке заметок лишняя.</param>
    internal void Show(FrameworkElement host, ReleaseVersion version, string notes, bool englishInterface)
    {
        VersionText.Text = "v" + version;
        ChatMarkdown.Write(Notes, host, ReleaseNotesText.Compact(notes), 12.5, 19, fillAvailableWidth: false);
        Notes.ScrollToHome();
        LanguageNote.Visibility = englishInterface ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(() => OkButton.Focus(), DispatcherPriority.Input);
    }

    internal void Close()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        Visibility = Visibility.Collapsed;
        Closed?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Scrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Sheet_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter)
        {
            e.Handled = true;
            Close();
        }
    }
}
