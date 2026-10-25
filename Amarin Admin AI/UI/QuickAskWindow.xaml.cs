using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Быстрый вопрос (1.37.0): маленькое окно поверх всех по глобальной клавише — строка вопроса,
/// под ней ответ и «Открыть в чате».
/// </summary>
/// <remarks>
/// Окно только показывает: ход ведёт главное окно (<c>MainWindow.QuickAsk.cs</c>) обычным
/// ходом чата во временной сессии, а сюда отдаёт текст. Своё окно, а не слой главного: главное
/// может быть свёрнуто или спрятано в трей, а вопрос задают поверх той программы, где работают.
/// Закрытие только прячет окно — второй вызов клавишей показывает его мгновенно.
/// </remarks>
public partial class QuickAskWindow : Window
{
    /// <summary>Шаг, с которым окно подбирает дописанный текст ответа.</summary>
    private static readonly TimeSpan PollStep = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _poll = new() { Interval = PollStep };
    private string _shown = "";
    private bool _final;

    public QuickAskWindow()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();

        // Alt+F4 и закрытие из панели задач только прячут: окно живёт до конца программы.
        Closing += (_, e) =>
        {
            if (!_disposing)
            {
                e.Cancel = true;
                HideByUser?.Invoke();
            }
        };
    }

    private bool _disposing;

    /// <summary>Человек задал вопрос (Enter).</summary>
    internal event Action<string>? Asked;

    /// <summary>«Открыть в чате».</summary>
    internal event Action? OpenInChatRequested;

    /// <summary>Esc, крестик или Alt+F4.</summary>
    internal event Action? HideByUser;

    /// <summary>Текст идущего ответа; null — ответа нет.</summary>
    internal Func<string?>? PendingText { get; set; }

    /// <summary>Ждёт ли ход разрешения человека в главном окне.</summary>
    internal Func<bool>? WaitingForApproval { get; set; }

    /// <summary>Показывает окно на мониторе, где человек работает, на трети его высоты.</summary>
    /// <param name="workArea">Рабочая область монитора в пикселях устройства; null — основной.</param>
    internal void Present(int uiScalePercent, Rect? workArea)
    {
        var factor = Math.Clamp(uiScalePercent, 50, 300) / 100.0;
        Root.LayoutTransform = Math.Abs(factor - 1.0) > 0.001 ? new ScaleTransform(factor, factor) : Transform.Identity;

        _ = new WindowInteropHelper(this).EnsureHandle();
        var area = ToastScreen.InDips(this, workArea);
        var width = Width * factor;
        Left = area.Left + ((area.Width - width) / 2);
        Top = area.Top + (area.Height * 0.22);

        Show();
        _ = Activate();
        Input.Focus();
        Input.SelectAll();
    }

    /// <summary>Вопрос ушёл — ждём первых слов.</summary>
    internal void ShowWaiting()
    {
        _final = false;
        _shown = "";
        Answer.Document = new System.Windows.Documents.FlowDocument();
        Answer.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        SetStatus(Loc.Get("S.QuickAsk.Thinking"), danger: false);
        AnswerArea.Visibility = Visibility.Visible;
        _poll.Start();
    }

    /// <summary>Строка вместо ответа: отказ или ошибка — ход кончился, подбирать больше нечего.</summary>
    internal void ShowStatus(string text, bool danger)
    {
        _poll.Stop();
        AnswerArea.Visibility = Visibility.Visible;
        SetStatus(text, danger);
    }

    /// <summary>Ответ закрыт: весь текст набело, подвал с моделью и ценой.</summary>
    internal void ShowFinal(string text, string meta)
    {
        _poll.Stop();
        _final = true;
        _shown = text;
        if (text.Length > 0)
        {
            ChatMarkdown.Write(Answer, this, text, 13, 20, fillAvailableWidth: false);
            Answer.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }

        MetaText.Text = meta;
        AnswerArea.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
    }

    /// <summary>Прячет окно и забывает показанный ответ.</summary>
    internal void Reset()
    {
        _poll.Stop();
        _final = false;
        _shown = "";
        Input.Clear();
        AnswerArea.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        Hide();
    }

    /// <summary>Закрывает окно насовсем — программа выходит.</summary>
    internal void Shutdown()
    {
        _poll.Stop();
        _disposing = true;
        Close();
    }

    /// <summary>Текст ответа, как его показывает окно, — для «Копировать» и тестов.</summary>
    internal string ShownText => _shown;

    private void Poll()
    {
        if (_final)
        {
            _poll.Stop();
            return;
        }

        if (WaitingForApproval?.Invoke() == true)
        {
            SetStatus(Loc.Get("S.QuickAsk.NeedsApproval"), danger: false);
        }
        else if (_shown.Length > 0)
        {
            StatusText.Visibility = Visibility.Collapsed;
        }

        var text = PendingText?.Invoke() ?? "";
        if (text.Length == 0 || string.Equals(text, _shown, StringComparison.Ordinal))
        {
            return;
        }

        _shown = text;
        ChatMarkdown.Write(Answer, this, text, 13, 20, fillAvailableWidth: false, streaming: true);
        Answer.Visibility = Visibility.Visible;
        Answer.ScrollToEnd();
    }

    private void SetStatus(string text, bool danger)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, danger ? "Status.Danger" : "Text.Faint");
        StatusText.Visibility = Visibility.Visible;
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        var text = Input.Text.Trim();
        if (text.Length > 0)
        {
            Asked?.Invoke(text);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideByUser?.Invoke();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideByUser?.Invoke();

    private void OpenInChat_Click(object sender, RoutedEventArgs e) => OpenInChatRequested?.Invoke();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_shown.Length == 0)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_shown);
        }
        catch (ExternalException)
        {
            // Буфер держит другая программа — копирование просто не случилось.
        }
    }

    // Окно без рамки двигают за карточку. Поле, ответ и кнопки нажатие забирают сами, сюда
    // доходит только пустое место карточки.
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Кнопку отпустили раньше, чем окно начало движение.
            }
        }
    }
}
