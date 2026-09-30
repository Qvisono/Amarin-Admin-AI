using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Экран блокировки поверх главного окна: пароль профиля, и больше ничего.
/// </summary>
/// <remarks>
/// Слоем окна, а не отдельным окном, как экран входа: отдельное окно легло бы рядом, и
/// переписка под ним осталась бы на экране. Проверку пароля ведёт хозяин
/// (<see cref="UnlockRequested"/>): профиль и его хэш — его забота.
/// </remarks>
public partial class LockScreen : UserControl
{
    public LockScreen() => InitializeComponent();

    /// <summary>Человек ввёл пароль и просит открыть.</summary>
    public event EventHandler<string>? UnlockRequested;

    public event EventHandler? MinimizeRequested;

    public event EventHandler? MaximizeRequested;

    public event EventHandler? CloseRequested;

    /// <summary>Готовит экран к показу: чистое поле, без прошлой ошибки.</summary>
    public void Prepare(string profileName)
    {
        SubtitleText.Text = Loc.Format("S.Lock.Subtitle", profileName);
        PasswordInput.Clear();
        ErrorText.Visibility = Visibility.Collapsed;
        SetBusy(false);
    }

    /// <summary>Фокус в поле пароля — после того, как слой показан.</summary>
    public void FocusPassword() =>
        Dispatcher.BeginInvoke(new Action(() => PasswordInput.Focus()), DispatcherPriority.Input);

    /// <summary>Пока пароль проверяется, второй раз не отправить.</summary>
    public void SetBusy(bool busy)
    {
        UnlockButton.IsEnabled = !busy;
        PasswordInput.IsEnabled = !busy;
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        PasswordInput.Clear();
        SetBusy(false);
        FocusPassword();
    }

    /// <summary>Поле пароля — для проверок и для возврата фокуса окном.</summary>
    internal PasswordBox Password => PasswordInput;

    private void Submit()
    {
        if (!UnlockButton.IsEnabled || PasswordInput.Password.Length == 0)
        {
            return;
        }

        SetBusy(true);
        UnlockRequested?.Invoke(this, PasswordInput.Password);
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e) => Submit();

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => MinimizeRequested?.Invoke(this, EventArgs.Empty);

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => MaximizeRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
