using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Окно отчёта о работе (D7): готовый Markdown, копирование и сохранение. Факты видны сразу,
/// выводы модели дописываются, когда придут.
/// </summary>
public partial class WorkReportOverlay : UserControl
{
    private FrameworkElement? _host;

    public WorkReportOverlay() => InitializeComponent();

    /// <summary>Окно закрыто: разбор, если ещё идёт, больше не нужен.</summary>
    internal event Action? Closed;

    /// <summary>Сохранить отчёт — хозяин открывает меню форматов у кнопки.</summary>
    internal event Action<string, FrameworkElement>? SaveRequested;

    internal string Markdown { get; private set; } = "";

    internal void Show(FrameworkElement host, string markdown, string status)
    {
        _host = host;
        Visibility = Visibility.Visible;
        Update(markdown, status);
        Dispatcher.BeginInvoke(() => CopyButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    internal void Update(string markdown, string status)
    {
        Markdown = markdown;
        SetStatus(status);
        if (_host is not null)
        {
            ChatMarkdown.Write(Body, _host, markdown, 13, 20, fillAvailableWidth: false);
        }
    }

    internal void SetStatus(string status)
    {
        StatusText.Text = status;
        StatusText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
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

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = Markdown;
        SetStatus(ClipboardWrite.Try(() => Clipboard.SetText(text))
            ? Loc.Get("S.Report.Copied")
            : Loc.Get("S.Report.CopyFailed"));
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(Markdown, SaveButton);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
