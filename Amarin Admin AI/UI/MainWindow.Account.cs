using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
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

        /// <summary>
        /// Side of the stored avatar. Generous for a 36px chip, but cheap, and it leaves room
        /// for a larger rendering later without asking the user to pick the photo again.
        /// </summary>
        private const int AvatarPixels = 512;

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
            var isDefault = ProfileStore.IsDefault(profile.Id);
            AccountModeText.Text = Loc.Get(isDefault ? "S.Account.ModeMain" : "S.Account.ModeExtra");
            SidebarAccountName.Text = profile.Name;
            SidebarAccountMode.Text = Loc.Get(
                isDefault ? "S.Account.LocalMode" : "S.Account.ExtraProfileShort");

            AccountPasswordHint.Text = Loc.Get(
                profile.HasPassword ? "S.Account.PasswordSet" : "S.Account.PasswordNotSet");
            RemovePasswordButton.IsEnabled = profile.HasPassword;
            ChangePasswordButton.Content = Loc.Get(
                profile.HasPassword ? "S.Common.Change" : "S.Account.SetPassword");
            LockOnStartupToggle.IsChecked = profile.LockOnStartup;
            LockOnStartupToggle.IsEnabled = profile.HasPassword;

            ApplyAvatar(profile);
        }

        /// <summary>
        /// Repaints both places the avatar appears. Every path that changes the picture goes
        /// through here — the settings tab, "change" and "remove" — so the sidebar can never
        /// drift out of step with the account panel again.
        /// </summary>
        private void ApplyAvatar(UserProfile profile)
        {
            var path = AvatarPath(profile);
            var source = path is not null && File.Exists(path) ? LoadAvatar(path) : null;

            if (source is not null)
            {
                AccountAvatarImage.Source = source;
                AccountAvatarImage.Visibility = Visibility.Visible;
                AccountAvatarLetter.Visibility = Visibility.Collapsed;
                RemoveAvatarButton.IsEnabled = true;
            }
            else
            {
                AccountAvatarImage.Source = null;
                AccountAvatarImage.Visibility = Visibility.Collapsed;
                AccountAvatarLetter.Visibility = Visibility.Visible;
                AccountAvatarLetter.Text = FirstLetter(profile.Name);
                RemoveAvatarButton.IsEnabled = false;
            }

            ApplySidebarAvatar(profile, source);
        }

        private void ApplySidebarAvatar(UserProfile profile, BitmapImage? source)
        {
            if (SidebarAvatarImage is null || SidebarAvatarLetter is null)
            {
                return;
            }

            SidebarAvatarImage.Source = source;
            SidebarAvatarImage.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
            SidebarAvatarLetter.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
            SidebarAvatarLetter.Text = FirstLetter(profile.Name);
        }

        private string? AvatarPath(UserProfile profile) =>
            string.IsNullOrWhiteSpace(profile.AvatarFileName)
                ? null
                : Path.Combine(ProfileStore.DataRootFor(profile.Id), profile.AvatarFileName);

        private static string FirstLetter(string? name) =>
            string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpperInvariant();

        /// <summary>Последний раскодированный аватар и отпечаток файла, из которого он взят.</summary>
        /// <remarks>
        /// Кэш WPF ниже отключён намеренно: файл аватара переписывается на месте, и по одному и
        /// тому же URI показалась бы старая картинка. Свой ключ со временем записи и размером
        /// обходит ту же ловушку честно — а заходят сюда на каждое открытие настроек.
        /// </remarks>
        private static (string Path, DateTime Written, long Length, BitmapImage Image)? _avatar;

        /// <summary>Loads the file fully into memory so it is not left locked on disk.</summary>
        private static BitmapImage? LoadAvatar(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (_avatar is { } cached &&
                    string.Equals(cached.Path, path, StringComparison.OrdinalIgnoreCase) &&
                    cached.Written == info.LastWriteTimeUtc &&
                    cached.Length == info.Length)
                {
                    return cached.Image;
                }

                var image = LoadFrozen(path, AvatarDecodeWidth);
                if (image is not null)
                {
                    _avatar = (path, info.LastWriteTimeUtc, info.Length, image);
                }

                return image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>В каком размере держать аватар: он показывается плиткой, не во весь экран.</summary>
        private const int AvatarDecodeWidth = 144;

        /// <summary>
        /// Читает картинку в память целиком и замораживает.
        /// </summary>
        /// <remarks>
        /// <c>OnLoad</c> и <c>IgnoreImageCache</c> вместе: без первого файл остаётся открытым и
        /// его нельзя переписать, без второго WPF отдаёт по тому же пути прежнюю картинку — и
        /// сменённый аватар не менялся бы на экране. Нулевая ширина означает «в исходном размере».
        /// </remarks>
        private static BitmapImage? LoadFrozen(string path, int decodePixelWidth)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                image.UriSource = new Uri(path);
                if (decodePixelWidth > 0)
                {
                    image.DecodePixelWidth = decodePixelWidth;
                }

                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex)
                when (ex is IOException or NotSupportedException or ArgumentException or UriFormatException)
            {
                return null;
            }
        }

        // ───────────────────────── Avatar ─────────────────────────

        private void ChangeAvatarButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = Loc.Get("S.Account.Avatar"),
                // No .webp: the WIC codec for it is not present on every Windows install, and a
                // missing one surfaces as an unhelpful decoder error rather than a refusal here.
                // Фильтр собирается из кусков: перевод целиком сломал бы разметку «имя|маска».
                Filter = Loc.Get("S.Account.Images") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|" +
                         Loc.Get("S.Common.AllFiles") + "|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var profile = ActiveProfile;
            var target = Path.Combine(ProfileStore.DataRootFor(profile.Id), AvatarFileName);
            try
            {
                var original = LoadForCrop(dialog.FileName);
                if (original is null)
                {
                    MessageBox.Show(
                        this,
                        Loc.Get("S.Account.ImageUnreadable"),
                        Title,
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                if (AvatarCropWindow.Choose(original, this) is not { } selection)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // Store exactly the square the user framed, at a size that suits a 36px chip —
                // not the original, which for a phone photo is several megapixels of nothing.
                using (var stream = File.Create(target))
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(SquareAvatar(original, selection)));
                    encoder.Save(stream);
                }

                profile.AvatarFileName = AvatarFileName;
                SaveProfiles();
                ApplyAvatar(profile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Decodes the picked file at full size. WPF imaging throughout, so the pixels the crop
        /// dialog shows are the pixels that get saved — GDI+ ignores the EXIF orientation that
        /// WPF honours, and mixing the two would rotate the crop out from under the user.
        /// </summary>
        private static BitmapSource? LoadForCrop(string path) => LoadFrozen(path, decodePixelWidth: 0);

        /// <summary>Cuts <paramref name="selection"/> out and squares it off at <see cref="AvatarPixels"/>.</summary>
        private static BitmapSource SquareAvatar(BitmapSource source, Int32Rect selection)
        {
            var cropped = new CroppedBitmap(source, selection);
            var scale = AvatarPixels / (double)selection.Width;
            var scaled = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
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

        // ───────────────────────── Name ─────────────────────────

        private void ChangeNameButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;
            OpenNameDialog(
                Loc.Get("S.Account.UserName"),
                Loc.Get("S.Account.UserNameDesc"),
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
                NameError.Text = Loc.Get("S.Account.NameEmpty");
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

        // ───────────────────────── Password ─────────────────────────

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
                Loc.Get("S.Account.PasswordSaved"),
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
                    Loc.Get("S.Account.SetPasswordFirst"),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            profile.LockOnStartup = wanted;
            SaveProfiles();
        }

        // ───────────────────────── Profiles ─────────────────────────

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
                Loc.Get("S.Account.NewProfile"),
                Loc.Get("S.Account.NewProfileDesc"),
                Loc.Get("S.Account.NewProfile"),
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
                Margin = new Thickness(0, 0, 10, 0)
            };
            chip.SetResourceReference(Border.BackgroundProperty, "Bg.Selected");
            RoundedClip.SetRadius(chip, 6);

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
                notes.Add(Loc.Get("S.Account.ProfileCurrent"));
            }

            if (profile.HasPassword)
            {
                notes.Add(Loc.Get("S.Account.ProfileHasPassword"));
            }

            if (ProfileStore.IsDefault(profile.Id))
            {
                notes.Add(Loc.Get("S.Account.ProfileMain"));
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
                    Content = Loc.Get("S.Common.Open"),
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
                    Content = Loc.Get("S.Common.Delete"),
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
                Loc.Format("S.Account.DeleteProfileConfirm", profile.Name),
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
            // Здесь занята программа, а не чат: UseProfile перекореняет ChatStore, и ход,
            // идущий в фоне, сохранил бы свой чат уже в папку нового профиля.
            if (_services is null || AnyTurnRunning)
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
            ClearPendingAttachments();
            StartNewSession(persist: false);
            RefreshChatList();
            LoadSettingsUi();
        }

        private void SaveProfiles() => ProfileStore.Save(_services!.ProfileRegistry);
    }
}
