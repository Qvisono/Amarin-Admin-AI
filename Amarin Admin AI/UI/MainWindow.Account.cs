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
    /// Настройки → Аккаунт: локальный профиль (имя, аватар, пароль) и переключение профилей.
    /// У каждого профиля своя папка данных; у профиля по умолчанию это корень программы, поэтому
    /// старые чаты никуда не переезжают.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        private const string AvatarFileName = "avatar.png";

        /// <summary>
        /// Сторона сохраняемого аватара. С запасом для плашки в 36 точек, но дёшево — и позволит
        /// показать его крупнее, не прося выбрать фото заново.
        /// </summary>
        private const int AvatarPixels = 512;

        /// <summary>What the name dialog does when it is confirmed.</summary>
        private Action<string>? _nameDialogCommit;
        private Action? _nameDialogDelete;

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
        /// Перерисовывает аватар в обоих местах. Все пути смены картинки — вкладка настроек,
        /// «Сменить» и «Убрать» — идут через этот метод, и боковая панель не расходится с
        /// панелью аккаунта.
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

        /// <summary>Читает файл в память целиком, чтобы не держать его занятым на диске.</summary>
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
                // Без .webp: кодека WIC для него нет на каждой Windows, а его отсутствие дало бы
                // невнятную ошибку декодера вместо понятного отказа.
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
                    Inform(Loc.Get("S.Account.ImageFailedTitle"), Loc.Get("S.Account.ImageUnreadable"));
                    return;
                }

                if (AvatarCropWindow.Choose(original, this) is not { } selection)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // Сохраняем ровно выбранный квадрат в размере для плашки в 36 точек, а не оригинал —
                // у фото с телефона это мегапиксели впустую.
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
                Inform(Loc.Get("S.Account.ImageFailedTitle"), ex.Message);
            }
        }

        /// <summary>
        /// Разбирает выбранный файл в полном размере. Везде графика WPF: что показало окно выбора
        /// кадра, то и сохранится. GDI+ не учитывает ориентацию из EXIF, которую учитывает WPF, и
        /// смесь двух повернула бы кадр из-под рук человека.
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
                // Профиль на него уже не ссылается; лишний файл безвреден.
            }
        }

        // ───────────────────────── Name ─────────────────────────

        private void ChangeNameButton_Click(object sender, RoutedEventArgs e)
        {
            var profile = ActiveProfile;
            OpenNameDialog(
                Loc.Get("S.Account.UserName"),
                profile.Name,
                name =>
                {
                    profile.Name = name;
                    SaveProfiles();
                    LoadAccountUi();
                });
        }

        /// <summary>Окно с одним полем: название чата, папки, тега, профиля, имя, язык.</summary>
        /// <param name="action">Подпись главной кнопки; по умолчанию «Сохранить», для нового — «Создать».</param>
        /// <param name="placeholder">Подсказка в пустом поле.</param>
        /// <param name="hint">Строка под полем — только когда без неё не понять, что вводить.</param>
        private void OpenNameDialog(
            string title,
            string current,
            Action<string> commit,
            string? action = null,
            string? placeholder = null,
            string? hint = null)
        {
            _nameDialogCommit = commit;
            NameDialogTitle.Text = title;
            NameInput.Text = current;
            NameInput.Tag = placeholder;
            NameDialogHint.Text = hint ?? "";
            NameDialogHint.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
            NameSaveButton.Content = action ?? Loc.Get("S.Common.Save");
            NameError.Text = "";

            // Цвет и «Удалить» — только у тега; их включает OpenTagDialog после этого вызова.
            NameColorRow.Visibility = Visibility.Collapsed;
            NameDeleteButton.Visibility = Visibility.Collapsed;
            _nameDialogDelete = null;
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
            _nameDialogDelete = null;
        }

        private void NameDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var delete = _nameDialogDelete;
            NameCancelButton_Click(sender, e);
            delete?.Invoke();
        }

        private void NameSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var name = NameInput.Text.Trim();
            if (name.Length == 0)
            {
                NameError.Text = Loc.Get("S.Account.NameEmpty");
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

            // Сменить пароль можно, только назвав нынешний.
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
            Inform(Loc.Get("S.Account.PasswordSavedTitle"), Loc.Get("S.Account.PasswordSaved"), NoticeTone.Info);
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
                // Сверять не с чем — возвращаем переключатель и объясняем почему.
                LockOnStartupToggle.IsChecked = false;
                Inform(Loc.Get("S.Account.SetPasswordFirstTitle"), Loc.Get("S.Account.SetPasswordFirst"));
                return;
            }

            profile.LockOnStartup = wanted;
            SaveProfiles();
        }

        // ───────────────────────── Profiles ─────────────────────────

        private void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshProfileList();
            ProfileLockNowButton.Visibility = ActiveProfileHasPassword ? Visibility.Visible : Visibility.Collapsed;
            ProfileOverlay.Visibility = Visibility.Visible;
        }

        private void ProfileLockNowButton_Click(object sender, RoutedEventArgs e)
        {
            ProfileOverlay.Visibility = Visibility.Collapsed;
            LockNow();
        }

        private void ProfileCloseButton_Click(object sender, RoutedEventArgs e) =>
            ProfileOverlay.Visibility = Visibility.Collapsed;

        private void CreateProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ProfileOverlay.Visibility = Visibility.Collapsed;
            OpenNameDialog(
                Loc.Get("S.Account.NewProfile"),
                "",
                name =>
                {
                    var created = ProfileStore.Create(_services!.ProfileRegistry, name);
                    SwitchToProfile(created.Id);
                },
                Loc.Get("S.Common.Create"),
                Loc.Get("S.Account.ProfileNamePlaceholder"));
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

            // Папка профиля по умолчанию — общий корень программы: её удаление унесло бы настройки
            // и все остальные профили, поэтому удалить его нельзя.
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

        private void DeleteProfile(UserProfile profile) => Detached.Run(DeleteProfileAsync(profile), "delete_profile");

        private async Task DeleteProfileAsync(UserProfile profile)
        {
            var confirmed = await ShowNoticeAsync(
                Loc.Format("S.Account.DeleteProfileTitle", profile.Name),
                Loc.Get("S.Account.DeleteProfileConfirm"),
                Loc.Get("S.Common.Delete"),
                Loc.Get("S.Common.Cancel"),
                NoticeTone.Danger);
            if (!confirmed || _services is null)
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

            // Сохраняем чат уходящего профиля раньше, чем под ним сменятся пути.
            PersistCurrent();
            StashDraft();
            FlushDraft();

            registry.ActiveProfileId = target.Id;
            ProfileStore.Save(registry);
            _services.UseProfile(ProfileStore.DataRootFor(target.Id));
            StartTextIndexBuild();

            ProfileOverlay.Visibility = Visibility.Collapsed;
            ClearPendingAttachments();
            StartNewSession(persist: false);
            ResetChatListView();
            SetSidebarCollapsed(_services.Settings.SidebarCollapsed);
            RestoreDraft(_session);
            RefreshChatList();
            LoadSettingsUi();
            InstructionsPage.ResetForProfile();
        }

        private void SaveProfiles() => ProfileStore.Save(_services!.ProfileRegistry);
    }
}
