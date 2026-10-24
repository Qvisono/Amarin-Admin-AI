using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Расшифровка записи идёт минутами, и Esc за это время нужен и другим: слой поверх чата
/// закрывается им, как обычно, а расшифровку Esc отменяет, только когда больше никому не нужен.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class TranscriptionEscapeTests
{
    private static readonly FieldInfo TranscribeCancel =
        typeof(MainWindow).GetField("_transcribeCancel", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly WpfFixture _wpf;

    public TranscriptionEscapeTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Escape_closes_an_open_journal_and_leaves_the_transcription_running()
    {
        var (journal, cancelled) = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            using var transcription = new CancellationTokenSource();
            TranscribeCancel.SetValue(window, transcription);
            try
            {
                typeof(MainWindow).GetMethod("OpenJournal", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, []);
                var overlay = (Grid)window.FindName("JournalOverlay")!;
                Escape(window, overlay);
                return (overlay.Visibility, transcription.IsCancellationRequested);
            }
            finally
            {
                TranscribeCancel.SetValue(window, null);
            }
        });

        // До правки отмена расшифровки стояла раньше всех и забирала Esc: журнал оставался открытым.
        Assert.NotEqual(Visibility.Visible, journal);
        Assert.False(cancelled);
    }

    [Fact]
    public void Escape_with_nothing_else_to_do_cancels_the_transcription()
    {
        var cancelled = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            using var transcription = new CancellationTokenSource();
            TranscribeCancel.SetValue(window, transcription);
            try
            {
                Escape(window, window);
                return transcription.IsCancellationRequested;
            }
            finally
            {
                TranscribeCancel.SetValue(window, null);
            }
        });

        Assert.True(cancelled);
    }

    private static void Escape(Window window, UIElement target) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window) ?? PresentationSource.FromVisual(target), 0, Key.Escape)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent
        });
}
