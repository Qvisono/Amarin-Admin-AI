using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;

namespace Amarin.UI;

/// <summary>
/// Two jobs, one window: unlock a profile at launch, and set or change a password. Both are
/// modal and return through <see cref="Password"/>.
/// </summary>
public partial class PasswordWindow : Window
{
    private UserProfile? _verifyAgainst;
    private readonly bool _confirmTwice;
    private int _attempts;

    private ProfileStore? _profiles;
    private ProfileRegistry? _registry;

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

    /// <summary>
    /// Профиль, которым в итоге вошли. Отличается от активного, когда на экране входа
    /// выбрали другого пользователя.
    /// </summary>
    public UserProfile? UnlockedProfile { get; private set; }

    /// <summary>Launch-time unlock. Returns true when the right password was entered.</summary>
    public static bool Unlock(UserProfile profile, Window? owner = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var window = new PasswordWindow(profile, confirmTwice: false)
        {
            HeadingText = { Text = Loc.Get("S.Password.Prompt") },
            SubtitleText = { Text = Loc.Format("S.Password.Protected", profile.Name) },
            OkButton = { Content = Loc.Get("S.Password.SignIn") }
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
            HeadingText = { Text = Loc.Get("S.Password.CurrentTitle") },
            SubtitleText = { Text = Loc.Get("S.Password.CurrentDesc") },
            OkButton = { Content = Loc.Get("S.Password.ConfirmAction") }
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
            HeadingText = { Text = Loc.Get("S.Password.NewTitle") },
            SubtitleText = { Text = Loc.Get("S.Password.NewDesc") },
            OkButton = { Content = Loc.Get("S.Common.Save") }
        };
        window.SecondBox.Tag = "confirm";

        return window.ShowDialog() == true ? window.Password : null;
    }

    /// <summary>
    /// Экран входа при запуске. В отличие от <see cref="Unlock"/> здесь можно выбрать другого
    /// пользователя: профиль без пароля открывается сразу, профиль с паролем просто занимает
    /// место того, чей пароль спрашивают. Возвращает выбранный профиль или <c>null</c>,
    /// если вход отменили.
    /// </summary>
    public static UserProfile? UnlockAtStartup(ProfileStore profiles, ProfileRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(registry);

        var active = profiles.Active(registry);
        var window = new PasswordWindow(active, confirmTwice: false)
        {
            OkButton = { Content = Loc.Get("S.Password.SignIn") }
        };
        window.EnableProfileSwitching(profiles, registry);
        window.SelectProfile(active);

        return window.ShowDialog() == true ? window.UnlockedProfile ?? active : null;
    }

    /// <summary>Показывает «Сменить пользователя», когда профилей больше одного.</summary>
    private void EnableProfileSwitching(ProfileStore profiles, ProfileRegistry registry)
    {
        _profiles = profiles;
        _registry = registry;
        SwitchUserButton.Visibility = registry.Profiles.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SwitchUserButton_Click(object sender, RoutedEventArgs e)
    {
        if (_registry is null)
        {
            return;
        }

        if (ProfileSwitchPanel.Visibility == Visibility.Visible)
        {
            ProfileSwitchPanel.Visibility = Visibility.Collapsed;
            return;
        }

        BuildProfileList();
        ProfileSwitchPanel.Visibility = Visibility.Visible;
    }

    private void BuildProfileList()
    {
        if (_registry is null || _profiles is null)
        {
            return;
        }

        ProfileSwitchList.Children.Clear();
        foreach (var profile in _registry.Profiles)
        {
            ProfileSwitchList.Children.Add(BuildProfileRow(profile));
        }
    }

    private Button BuildProfileRow(UserProfile profile)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var chip = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 0, 10, 0),
            Background = Brush("Bg.Selected", Color.FromRgb(0x2A, 0x2A, 0x2A)),
            Clip = new RectangleGeometry(new Rect(0, 0, 26, 26), 6, 6)
        };

        if (AvatarFor(profile) is { } avatar)
        {
            chip.Child = new Image { Source = avatar, Stretch = Stretch.UniformToFill };
        }
        else
        {
            chip.Child = new TextBlock
            {
                Text = FirstLetter(profile.Name),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("Text.Secondary", Color.FromRgb(0xB0, 0xB0, 0xB0))
            };
        }

        grid.Children.Add(chip);

        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = profile.Name,
            FontSize = 12.5,
            Foreground = Brush("Text.Body", Color.FromRgb(0xDC, 0xDC, 0xDC))
        });

        var note = Loc.Get(profile.IsLocked ? "S.Password.Required" : "S.Password.NoPassword");
        if (_verifyAgainst is not null && profile.Id == _verifyAgainst.Id)
        {
            note += " · " + Loc.Get("S.Password.Chosen");
        }

        labels.Children.Add(new TextBlock
        {
            Text = note,
            FontSize = 10.5,
            Foreground = Brush("Text.Faint", Color.FromRgb(0x7A, 0x7A, 0x7A))
        });

        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        var row = new Button
        {
            Style = (Style)FindResource("ProfileRowButton"),
            Content = grid
        };
        row.Click += (_, _) => ChooseProfile(profile);
        return row;
    }

    private static string FirstLetter(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

    private BitmapImage? AvatarFor(UserProfile profile)
    {
        if (_profiles is null || string.IsNullOrWhiteSpace(profile.AvatarFileName))
        {
            return null;
        }

        var path = Path.Combine(_profiles.DataRootFor(profile.Id), profile.AvatarFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 72;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Профиль без пароля открывается сразу — спрашивать нечего. У остальных меняется тот,
    /// чей пароль проверяется.
    /// </summary>
    private void ChooseProfile(UserProfile profile)
    {
        ProfileSwitchPanel.Visibility = Visibility.Collapsed;

        if (!profile.IsLocked)
        {
            UnlockedProfile = profile;
            Password = null;
            DialogResult = true;
            return;
        }

        SelectProfile(profile);
    }

    private void SelectProfile(UserProfile profile)
    {
        _verifyAgainst = profile;
        _attempts = 0;
        UnlockedProfile = profile;
        HeadingText.Text = Loc.Format("S.Password.LoginTitle", profile.Name);
        SubtitleText.Text = Loc.Format("S.Password.Protected", profile.Name);
        ErrorText.Visibility = Visibility.Collapsed;
        FirstBox.Clear();
        FirstBox.Focus();
    }

    private SolidColorBrush Brush(string key, Color fallback) =>
        TryFindResource(key) as SolidColorBrush ?? new SolidColorBrush(fallback);

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
                ShowError(Loc.Get("S.Password.TooShort"));
                return;
            }

            if (!string.Equals(password, SecondBox.Password, StringComparison.Ordinal))
            {
                ShowError(Loc.Get("S.Password.Mismatch"));
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
                ShowError(Loc.Get("S.Password.TooManyAttempts"));
                DialogResult = false;
                return;
            }

            ShowError(Loc.Format("S.Password.Wrong", MaxAttempts - _attempts));
            return;
        }

        Password = password;
        UnlockedProfile = _verifyAgainst ?? UnlockedProfile;
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

        foreach (var border in new[] { FirstFieldBorder, SecondFieldBorder, ProfileSwitchPanel })
        {
            border.Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
        }
    }
}
