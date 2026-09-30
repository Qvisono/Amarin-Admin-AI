using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Amarin.UI;

/// <summary>
/// Панель поиска по открытому чату (D1): поле, счётчик «3/17», стрелки, Esc.
/// </summary>
/// <remarks>
/// Поиск идёт с низа переписки вверх, как листают чат: Enter — к более раннему совпадению,
/// Shift+Enter — к более позднему. Набор ждёт паузы в 200 мс, чтобы длинный чат не пересчитывался
/// на каждую букву.
/// </remarks>
public partial class ChatFindBar : UserControl
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    public ChatFindBar()
    {
        InitializeComponent();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            QueryChanged?.Invoke(QueryBox.Text);
        };
    }

    internal event Action<string>? QueryChanged;

    /// <summary>К более раннему совпадению (вверх).</summary>
    internal event Action? Older;

    /// <summary>К более позднему (вниз).</summary>
    internal event Action? Newer;

    internal event Action? CloseRequested;

    internal string Query => QueryBox.Text;

    /// <summary>Фокус в поле с выделенным текстом — повторный Ctrl+F начинает новый поиск.</summary>
    internal void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    /// <summary>«3/17», «нет совпадений» или пусто, пока нечего искать.</summary>
    internal void ShowCount(int current, int total)
    {
        CountText.Text = QueryBox.Text.Trim().Length == 0
            ? ""
            : total == 0
                ? Core.Loc.Get("S.Find.None")
                : $"{current}/{total}";
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Bar_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseRequested?.Invoke();
                break;
            case Key.Enter:
                e.Handled = true;
                // Набранное ещё не ушло в поиск — отправляем сразу, иначе Enter листал бы старые находки.
                if (_debounce.IsEnabled)
                {
                    _debounce.Stop();
                    QueryChanged?.Invoke(QueryBox.Text);
                }
                else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    Newer?.Invoke();
                }
                else
                {
                    Older?.Invoke();
                }

                break;
        }
    }

    private void Older_Click(object sender, RoutedEventArgs e) => Older?.Invoke();

    private void Newer_Click(object sender, RoutedEventArgs e) => Newer?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
