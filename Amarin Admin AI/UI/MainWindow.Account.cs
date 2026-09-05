using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.Tools;
using Bitmap = System.Drawing.Bitmap;
using Image = System.Windows.Controls.Image;

namespace Amarin.UI
{
    /// <summary>
    /// Settings → Account: the local profile (name, avatar, password lock) and switching
    /// between profiles. Each profile owns a data directory; the default profile's directory
    /// is the original app root, so existing chats are never moved.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private const string AvatarFileName = "avatar.png";

        /// <summary>What the name dialog does when it is confirmed.</summary>
        private Action<string>? _nameDialogCommit;

        private ProfileStore ProfileStore => _services!.Profiles;

        private UserProfile ActiveProfile => ProfileStore.Active(_services!.ProfileRegistry);

        private void LoadAccountUi()
        {
            if (_services is null)
            {
                return;
            }

            var profile = ActiveProfile;
            AccountNameText.Text = profile.Name;
            AccountNameHint.Text = profile.Name;
            AccountModeText.Text = ProfileStore.IsDefault(profile.Id)
                ? "Локальный режим · основной профиль"
                : "Локальный режим · дополнительный профиль";

            AccountPasswordHint.Text = profile.HasPassword ? "Задан" : "Не задан";
            RemovePasswordButton.IsEnabled = profile.HasPassword;
            ChangePasswordButton.Content = profile.HasPassword ? "Изменить" : "Задать";
            LockOnStartupToggle.IsChecked = profile.LockOnStartup;
            LockOnStartupToggle.IsEnabled = profile.HasPassword;

            ApplyAvatar(profile);
        }

        private void ApplyAvatar(UserProfile profile)
        {
            var path = AvatarPath(profile);
            if (path is not null && File.Exists(path) && LoadAvatar(path) is { } source)
            {
                AccountAvatarImage.Source = source;
                AccountAvatarImage.Visibility = Visibility.Visible;
                AccountAvatarLetter.Visibility = Visibility.Collapsed;
                RemoveAvatarButton.IsEnabled = true;
                return;
            }

            AccountAvatarImage.Source = null;
            AccountAvatarImage.Visibility = Visibility.Collapsed;
            AccountAvatarLetter.Visibility = Visibility.Visible;
            AccountAvatarLetter.Text = FirstLetter(profile.Name);
            RemoveAvatarButton.IsEnabled = false;
        }

        private string? AvatarPath(UserProfile profile) =>
            string.IsNullOrWhiteSpace(profile.AvatarFileName)
                ? null
                : Path.Combine(ProfileStore.DataRootFor(profile.Id), profile.AvatarFileName);

        private static string FirstLetter(string? name) =>
            string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

        /// <summary>Loads the file fully into memory so it is not left locked on disk.</summary>
        private static BitmapImage? LoadAvatar(string path)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path);
                image.DecodePixelWidth = 144;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        // ── Avatar ────────────────────────────────────────────────────────────────────

        private void ChangeAvatarButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Изображение профиля",
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var profile = ActiveProfile;
            var target = Path.Combine(ProfileStore.DataRootFor(profile.Id), AvatarFileName);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // Store a downscaled copy rather than the original — an 8 MP photo has no
                // business sitting in the profile folder to render a 36px chip.
                using (var stream = File.OpenRead(dialog.FileName))
                using (var original = new Bitmap(stream))
                using (var resized = ImageHelpers.Downscale(original))
                {
                    resized.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                }

                profile.AvatarFileName = AvatarFileName;
                SaveProfiles();
                ApplyAvatar(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveAvatarButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;
            var path = AvatarPath(profile);
            profile.AvatarFileName = null;
            SaveProfiles();
            ApplyAvatar(profile);

            try
            {
                if (path is not null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The profile no longer references it; a stray file is harmless.
            }
        }

        // ── Name ──────────────────────────────────────────────────────────────────────

        private void ChangeNameButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;
            OpenNameDialog(
                "Имя пользователя",
                "Отображается в настройках. Хранится только на этом компьютере.",
                profile.Name,
                name =>
                {
                    profile.Name = name;
                    SaveProfiles();
                    LoadAccountUi();
                });
        }

        private void OpenNameDialog(string title, string subtitle, string current, Action<string> commit)
        {
            _nameDialogCommit = commit;
            NameDialogTitle.Text = title;
            NameDialogSubtitle.Text = subtitle;
            NameInput.Text = current;
            NameError.Visibility = Visibility.Collapsed;
            NameOverlay.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                NameInput.Focus();
                NameInput.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void NameCancelButton_Click(object sender, RoutedEventArgs e)
        {
            NameOverlay.Visibility = Visibility.Collapsed;
            _nameDialogCommit = null;
        }

        private void NameSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameInput.Text.Trim();
            if (name.Length == 0)
            {
                NameError.Text = "Имя не может быть пустым.";
                NameError.Visibility = Visibility.Visible;
                return;
            }

            var commit = _nameDialogCommit;
            NameOverlay.Visibility = Visibility.Collapsed;
            _nameDialogCommit = null;
            commit?.Invoke(name);
        }

        private void NameInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                NameSaveButton_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                NameCancelButton_Click(sender, e);
            }
        }

        // ── Password ──────────────────────────────────────────────────────────────────

        private void ChangePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;

            // Changing an existing password requires proving you know the current one.
            if (profile.HasPassword && !PasswordWindow.Confirm(profile, this))
            {
                return;
            }

            var password = PasswordWindow.SetNew(this);
            if (password is null)
            {
                return;
            }

            var (hash, salt) = PasswordHash.Create(password);
            profile.PasswordHash = hash;
            profile.PasswordSalt = salt;
            SaveProfiles();
            LoadAccountUi();

            MessageBox.Show(
                this,
                "Пароль сохранён.\n\nОн блокирует вход в приложение, но не шифрует чаты — " +
                "файлы в папке приложения остаются доступными для чтения.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void RemovePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;
            if (!profile.HasPassword || !PasswordWindow.Confirm(profile, this))
            {
                return;
            }

            profile.PasswordHash = null;
            profile.PasswordSalt = null;
            profile.LockOnStartup = false;
            SaveProfiles();
            LoadAccountUi();
        }

        private void LockOnStartupToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var profile = ActiveProfile;
            var wanted = LockOnStartupToggle.IsChecked == true;
            if (wanted && !profile.HasPassword)
            {
                // Nothing to check against — bounce the toggle and say why.
                LockOnStartupToggle.IsChecked = false;
                MessageBox.Show(
                    this,
                    "Сначала задайте пароль.",
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            profile.LockOnStartup = wanted;
            SaveProfiles();
        }

        // ── Profiles ──────────────────────────────────────────────────────────────────

        private void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshProfileList();
            ProfileOverlay.Visibility = Visibility.Visible;
        }

        private void ProfileCloseButton_Click(object sender, RoutedEventArgs e) =>
            ProfileOverlay.Visibility = Visibility.Collapsed;

        private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ProfileOverlay.Visibility = Visibility.Collapsed;
            OpenNameDialog(
                "Новый профиль",
                "У нового профиля будет свой пустой список чатов и свои настройки.",
                "Новый профиль",
                name =>
                {
                    var created = ProfileStore.Create(_services!.ProfileRegistry, name);
                    SwitchToProfile(created.Id);
                });
        }

        private void RefreshProfileList()
        {
            ProfileList.Children.Clear();
            var registry = _services!.ProfileRegistry;
            foreach (var profile in registry.Profiles)
            {
                ProfileList.Children.Add(CreateProfileRow(profile, profile.Id == registry.ActiveProfileId));
            }
        }

        private FrameworkElement CreateProfileRow(UserProfile profile, bool active)
        {
            var grid = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chip = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 10, 0)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Bg.Selected");

            var path = AvatarPath(profile);
            if (path is not null && File.Exists(path) && LoadAvatar(path) is { } source)
            {
                chip.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
            }
            else
            {
                var letter = new TextBlock
                {
                    Text = FirstLetter(profile.Name),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                letter.SetResourceReference(TextBlock.ForegroundProperty, "Text.Secondary");
                chip.Child = letter;
            }

            grid.Children.Add(chip);

            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = profile.Name, FontSize = 12.5 };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Text.Body");
            labels.Children.Add(name);

            var notes = new List<string>();
            if (active)
            {
                notes.Add("текущий");
            }

            if (profile.HasPassword)
            {
                notes.Add("с паролем");
            }

            if (ProfileStore.IsDefault(profile.Id))
            {
                notes.Add("основной");
            }

            if (notes.Count > 0)
            {
                var hint = new TextBlock { Text = string.Join(" · ", notes), FontSize = 10.5 };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "Text.Faint");
                labels.Children.Add(hint);
            }

            Grid.SetColumn(labels, 1);
            grid.Children.Add(labels);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (!active)
            {
                var open = new Button
                {
                    Content = "Открыть",
                    Style = (Style)FindResource("DialogSecondaryButton"),
                    Margin = new Thickness(0, 0, 6, 0)
                };
                open.Click += (_, _) => SwitchToProfile(profile.Id);
                buttons.Children.Add(open);
            }

            // The default profile's folder is the shared app root — deleting it would take
            // settings and every other profile with it, so it is never removable.
            if (!ProfileStore.IsDefault(profile.Id))
            {
                var delete = new Button
                {
                    Content = "Удалить",
                    Style = (Style)FindResource("DialogSecondaryButton")
                };
                delete.Click += (_, _) => DeleteProfile(profile);
                buttons.Children.Add(delete);
            }

            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            return grid;
        }

        private void DeleteProfile(UserProfile profile)
        {
            var confirm = MessageBox.Show(
                this,
                $"Удалить профиль «{profile.Name}» вместе со всеми его чатами?\n\nЭто необратимо.",
                Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var wasActive = profile.Id == _services!.ProfileRegistry.ActiveProfileId;
            ProfileStore.Delete(_services.ProfileRegistry, profile.Id);
            if (wasActive)
            {
                SwitchToProfile(ProfileStore.DefaultProfileId);
                return;
            }

            RefreshProfileList();
        }

        private void SwitchToProfile(string profileId)
        {
            if (_services is null || _busy)
            {
                return;
            }

            var registry = _services.ProfileRegistry;
            var target = registry.Profiles.FirstOrDefault(p => p.Id == profileId);
            if (target is null || target.Id == registry.ActiveProfileId)
            {
                ProfileOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            if (target.IsLocked && !PasswordWindow.Unlock(target, this))
            {
                return;
            }

            // Flush the outgoing profile's chat before any path changes underneath it.
            PersistCurrent();

            registry.ActiveProfileId = target.Id;
            ProfileStore.Save(registry);
            _services.UseProfile(ProfileStore.DataRootFor(target.Id));

            ProfileOverlay.Visibility = Visibility.Collapsed;
            ClearPendingImages();
            StartNewSession(persist: false);
            RefreshChatList();
            LoadSettingsUi();
        }

        private void SaveProfiles() => ProfileStore.Save(_services!.ProfileRegistry);
    }
}
