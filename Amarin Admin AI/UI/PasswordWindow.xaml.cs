using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Two jobs, one window: unlock a profile at launch, and set or change a password. Both are
/// modal and return through <see cref="Password"/>.
/// </summary>
public partial class PasswordWindow : Window
{
    private readonly UserProfile? _verifyAgainst;
    private readonly bool _confirmTwice;
    private int _attempts;

    private const int MaxAttempts = 5;

    /// <summary>Use the <see cref="Unlock"/> / <see cref="Confirm"/> / <see cref="SetNew"/> entry points.</summary>
    internal PasswordWindow(UserProfile? verifyAgainst = null, bool confirmTwice = false)
    {
        InitializeComponent();
        ApplyFallbackBrushes();
        _verifyAgainst = verifyAgainst;
        _confirmTwice = confirmTwice;
        SecondFieldBorder.Visibility = confirmTwice ? Visibility.Visible : Visibility.Collapsed;
        Loaded += (_, _) => FirstBox.Focus();
    }

    /// <summary>The accepted password; null when the dialog was cancelled.</summary>
    public string? Password { get; private set; }

    /// <summary>Launch-time unlock. Returns true when the right password was entered.</summary>
    public static bool Unlock(UserProfile profile, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var window = new PasswordWindow(profile, confirmTwice: false)
        {
            HeadingText = { Text = "Введите пароль" },
            SubtitleText = { Text = $"Профиль «{profile.Name}» защищён паролем." },
            OkButton = { Content = "Войти" }
        };
        if (owner is not null)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return window.ShowDialog() == true;
    }

    /// <summary>Asks for the current password before a change. Null result means cancelled.</summary>
    public static bool Confirm(UserProfile profile, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var window = new PasswordWindow(profile, confirmTwice: false)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            HeadingText = { Text = "Текущий пароль" },
            SubtitleText = { Text = "Подтвердите текущий пароль, чтобы изменить настройки входа." },
            OkButton = { Content = "Подтвердить" }
        };

        return window.ShowDialog() == true;
    }

    /// <summary>Sets a new password. Returns the plain text, or null if cancelled.</summary>
    public static string? SetNew(Window owner)
    {
        var window = new PasswordWindow(verifyAgainst: null, confirmTwice: true)
        {
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            HeadingText = { Text = "Новый пароль" },
            SubtitleText =
            {
                Text = "Пароль блокирует вход в приложение. Он не шифрует чаты — файлы на диске " +
                       "остаются доступными для чтения. Забытый пароль восстановить нельзя."
            },
            OkButton = { Content = "Сохранить" }
        };
        window.SecondBox.Tag = "confirm";

        return window.ShowDialog() == true ? window.Password : null;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Submit();

    private void Box_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_confirmTwice && ReferenceEquals(sender, FirstBox))
        {
            SecondBox.Focus();
            return;
        }

        Submit();
    }

    private void Submit()
    {
        var password = FirstBox.Password;

        if (_confirmTwice)
        {
            if (password.Length < 4)
            {
                ShowError("Пароль должен содержать не менее 4 символов.");
                return;
            }

            if (!string.Equals(password, SecondBox.Password, StringComparison.Ordinal))
            {
                ShowError("Пароли не совпадают.");
                SecondBox.Clear();
                SecondBox.Focus();
                return;
            }

            Password = password;
            DialogResult = true;
            return;
        }

        if (_verifyAgainst is null ||
            !PasswordHash.Verify(password, _verifyAgainst.PasswordHash, _verifyAgainst.PasswordSalt))
        {
            _attempts++;
            FirstBox.Clear();
            FirstBox.Focus();

            // Not a security boundary — a local file is readable anyway — just a stop for
            // idle guessing so the dialog cannot be hammered forever.
            if (_attempts >= MaxAttempts)
            {
                ShowError("Слишком много неудачных попыток.");
                DialogResult = false;
                return;
            }

            ShowError($"Неверный пароль. Осталось попыток: {MaxAttempts - _attempts}.");
            return;
        }

        Password = password;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Borderless window: the card itself is the title bar.
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// This window can open before ThemeManager has run (it gates startup), and then every
    /// DynamicResource resolves to null on a transparent window — i.e. an invisible dialog.
    /// </summary>
    private void ApplyFallbackBrushes()
    {
        if (TryFindResource("Bg.Panel") is not null)
        {
            return;
        }

        Card.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        Card.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        HeadingText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));
        SubtitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        ErrorText.Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
        foreach (var box in new[] { FirstBox, SecondBox })
        {
            box.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
            box.CaretBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        }

        foreach (var border in new[] { FirstFieldBorder, SecondFieldBorder })
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        }
    }
}
