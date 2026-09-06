using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Rectangle = System.Windows.Shapes.Rectangle;
using System.Windows.Threading;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// The Appearance settings page: theme presets, the backdrop and glass customisation, the
    /// interface knobs and the compact composer.
    /// </summary>
    public partial class MainWindow
    {
        private const string BackgroundFileStem = "background";

        /// <summary>Ready-made backdrops, so the feature looks good before anyone touches a slider.</summary>
        private static readonly (string Name, string[] Colors, double Angle)[] GradientPresets =
        [
            ("Ночь", ["#0F2027", "#203A43", "#2C5364"], 135),
            ("Закат", ["#3A1C71", "#D76D77", "#FFAF7B"], 120),
            ("Океан", ["#1A2980", "#26D0CE"], 150),
            ("Туманность", ["#0B0B2B", "#41295A", "#2F0743"], 110),
            ("Мох", ["#0F2027", "#1D3A2E", "#375A45"], 140),
            ("Графит", ["#141414", "#2B2B2B", "#0E0E0E"], 135)
        ];

        private AppearanceManager? _appearance;
        private ComposerCompactMode? _compact;
        private BalanceBadge? _balance;

        /// <summary>
        /// Saturation needs a pixel pass over the whole picture, so dragging its slider is
        /// debounced; every other slider applies on the frame.
        /// </summary>
        private readonly DispatcherTimer _appearanceDebounce =
            new() { Interval = TimeSpan.FromMilliseconds(160) };

        private bool _appearanceDebouncePending;

        private void InitializeAppearance()
        {
            _appearance = new AppearanceManager(this, BackdropHost);
            _compact = new ComposerCompactMode(
                this,
                ComposerBorder,
                ComposerRow,
                ComposerLayout,
                ComposerInputRow,
                ComposerInputRowDef,
                ComposerToolbar,
                ComposerPlaceholder,
                MessageTextBox);
            _balance = new BalanceBadge(BalanceBadge, BalanceCoin, BalanceAmount, new BalanceStore());

            _appearanceDebounce.Tick += (_, _) =>
            {
                _appearanceDebounce.Stop();
                if (!_appearanceDebouncePending)
                {
                    return;
                }

                _appearanceDebouncePending = false;
                ApplyAppearance(save: true);
            };

            BuildThemeCards();
            BuildGradientPresets();
            WireAppearanceControls();
        }

        // ───────────────────────── карточки тем ─────────────────────────

        private void BuildThemeCards()
        {
            ThemeCardsHost.Children.Clear();

            // Resolved from the host, not the window: the style lives in the settings Grid's own
            // Resources, which Window.FindResource does not see.
            var style = (Style)ThemeCardsHost.FindResource("ThemeCard");

            foreach (var preset in ThemeCatalog.Presets)
            {
                var card = new RadioButton
                {
                    Style = style,
                    Tag = preset.Theme,
                    ToolTip = preset.DisplayName,
                    Content = ThemePreview(preset)
                };
                card.Checked += ThemeCard_Checked;
                ThemeCardsHost.Children.Add(card);
            }
        }

        /// <summary>
        /// A miniature of the palette: the window ground, a panel on it and an accent bar. Shows
        /// what a theme looks like without having to apply it.
        /// </summary>
        private static UIElement ThemePreview(ThemePresetInfo preset)
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var swatch = new Border
            {
                Margin = new Thickness(8, 8, 8, 4),
                CornerRadius = new CornerRadius(5),
                Background = Fill(preset.Surface),
                BorderThickness = new Thickness(1),
                BorderBrush = Fill(preset.Raised)
            };

            var inner = new Grid { Margin = new Thickness(5, 4, 5, 4) };
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var panel = new Border
            {
                Width = 12,
                CornerRadius = new CornerRadius(3),
                Background = Fill(preset.Raised)
            };
            inner.Children.Add(panel);

            var bars = new StackPanel { Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            bars.Children.Add(new Rectangle
            {
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(0, 0, 6, 3),
                Fill = Fill(preset.Accent)
            });
            bars.Children.Add(new Rectangle
            {
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Margin = new Thickness(0, 0, 12, 0),
                Fill = Fill(preset.Raised)
            });
            Grid.SetColumn(bars, 1);
            inner.Children.Add(bars);

            swatch.Child = inner;
            root.Children.Add(swatch);

            var label = new TextBlock
            {
                Text = preset.DisplayName,
                FontSize = 10.5,
                Margin = new Thickness(0, 0, 0, 7),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            label.SetResourceReference(ForegroundProperty, "Text.Tertiary");
            Grid.SetRow(label, 1);
            root.Children.Add(label);

            return root;
        }

        private static SolidColorBrush Fill(string hex)
        {
            var brush = new SolidColorBrush(AppearanceManager.Parse(hex) ?? Colors.Gray);
            brush.Freeze();
            return brush;
        }

        private void ThemeCard_Checked(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null || sender is not RadioButton { Tag: AppTheme theme })
            {
                return;
            }

            _services.Settings.Theme = theme;
            _services.SettingsStore.Save(_services.Settings);
            ThemeManager.Apply(theme);
        }

        private void ThemeFollowSystemToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            if (ThemeFollowSystemToggle.IsChecked == true)
            {
                _services.Settings.Theme = AppTheme.System;
            }
            else
            {
                // Leaving "follow Windows" keeps whatever is on screen right now, rather than
                // snapping back to a preset the user may never have picked.
                _services.Settings.Theme = ThemeManager.Current.Theme;
            }

            _services.SettingsStore.Save(_services.Settings);
            ThemeManager.Apply(_services.Settings.Theme);
            SyncThemeCards(_services.Settings.Theme);
        }

        private void SyncThemeCards(AppTheme theme)
        {
            var following = theme == AppTheme.System;
            ThemeFollowSystemToggle.IsChecked = following;
            ThemeCardsHost.IsEnabled = !following;

            // While following Windows the grid still shows which palette is actually painted.
            var effective = following ? ThemeManager.Current.Theme : theme;
            foreach (var child in ThemeCardsHost.Children)
            {
                if (child is RadioButton { Tag: AppTheme cardTheme } card)
                {
                    card.IsChecked = cardTheme == effective;
                }
            }
        }

        // ───────────────────────── градиентные пресеты ─────────────────────────

        private void BuildGradientPresets()
        {
            GradientPresetsHost.Children.Clear();
            foreach (var (name, colors, angle) in GradientPresets)
            {
                var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                for (var i = 0; i < colors.Length; i++)
                {
                    brush.GradientStops.Add(new GradientStop(
                        AppearanceManager.Parse(colors[i]) ?? Colors.Gray,
                        (double)i / (colors.Length - 1)));
                }

                brush.Freeze();

                var swatch = new Border
                {
                    Width = 60,
                    Height = 30,
                    CornerRadius = new CornerRadius(6),
                    Background = brush,
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                swatch.SetResourceReference(Border.BorderBrushProperty, "Border.Default");

                var button = new Button
                {
                    Content = swatch,
                    ToolTip = name,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new Thickness(0),
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Template = TransparentButtonTemplate()
                };

                var captured = (colors, angle);
                button.Click += (_, _) => ApplyGradientPreset(captured.colors, captured.angle);
                GradientPresetsHost.Children.Add(button);
            }
        }

        private static ControlTemplate TransparentButtonTemplate()
        {
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            return new ControlTemplate(typeof(Button)) { VisualTree = presenter };
        }

        private void ApplyGradientPreset(string[] colors, double angle)
        {
            if (_services is null)
            {
                return;
            }

            var appearance = _services.Settings.Appearance;
            appearance.GradientColors = [.. colors];
            appearance.GradientAngle = angle;
            LoadAppearanceUi(_services.Settings);
            ApplyAppearance(save: true);
        }

        // ───────────────────────── привязка контролов ─────────────────────────

        private void WireAppearanceControls()
        {
            AccentPicker.ColorChanged += (_, hex) => Edit(a => a.AccentColor = hex);

            GradientColor1.AllowClear = false;
            GradientColor2.AllowClear = false;
            GradientColor1.ColorChanged += (_, _) => CommitGradientColors();
            GradientColor2.ColorChanged += (_, _) => CommitGradientColors();
            GradientColor3.ColorChanged += (_, _) => CommitGradientColors();
            GradientColor4.ColorChanged += (_, _) => CommitGradientColors();

            Bind(GradientAngleSlider, GradientAngleValue, v => $"{v:0}°", (a, v) => a.GradientAngle = v);
            Bind(MotionSpeedSlider, MotionSpeedValue, v => $"{v:0.00}×", (a, v) => a.MotionSpeed = v);
            Bind(ImageBrightnessSlider, ImageBrightnessValue, v => $"{v * 100:0}%", (a, v) => a.ImageBrightness = v);
            Bind(ImageSaturationSlider, ImageSaturationValue, v => $"{v * 100:0}%", (a, v) => a.ImageSaturation = v, debounce: true);
            Bind(ImageBlurSlider, ImageBlurValue, v => $"{v:0}", (a, v) => a.ImageBlur = v, debounce: true);
            Bind(GlassOpacitySlider, GlassOpacityValue, v => $"{v * 100:0}%", (a, v) => a.GlassOpacity = v);
            Bind(GlassFrostSlider, GlassFrostValue, v => $"{v * 100:0}%", (a, v) => a.GlassFrost = v);
            Bind(CornerRadiusSlider, CornerRadiusValue, v => $"{v:0}", (a, v) => a.CornerRadius = v);
            Bind(CompactDelaySlider, CompactDelayValue, v => $"{v / 1000:0.0}с", (a, v) => a.CompactDelayMs = (int)v);
            Bind(CompactWidthSlider, CompactWidthValue, v => $"{v:0}%", (a, v) => a.CompactWidthPercent = v);
            Bind(CompactHoverSlider, CompactHoverValue, v => $"{v:0}", (a, v) => a.CompactHoverRadius = v);
        }

        /// <summary>
        /// Wires one slider: keeps its value label current and writes the value into settings.
        /// <paramref name="debounce"/> is for the two sliders whose effect costs a pixel pass.
        /// </summary>
        private void Bind(
            Slider slider,
            TextBlock label,
            Func<double, string> format,
            Action<AppearanceSettings, double> apply,
            bool debounce = false)
        {
            slider.ValueChanged += (_, e) =>
            {
                label.Text = format(e.NewValue);
                if (_settingsUiLoading || _services is null)
                {
                    return;
                }

                apply(_services.Settings.Appearance, e.NewValue);

                if (debounce)
                {
                    _appearanceDebouncePending = true;
                    _appearanceDebounce.Stop();
                    _appearanceDebounce.Start();
                    return;
                }

                ApplyAppearance(save: true);
            };
        }

        private void CommitGradientColors()
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            var colors = new[]
                {
                    GradientColor1.Hex, GradientColor2.Hex, GradientColor3.Hex, GradientColor4.Hex
                }
                .Where(hex => hex.Length > 0)
                .ToList();

            if (colors.Count < 2)
            {
                // Normalize would pad this back out to two, but with colours the user never
                // chose; better to ignore the edit until the second slot is filled again.
                return;
            }

            _services.Settings.Appearance.GradientColors = colors;
            ApplyAppearance(save: true);
        }

        private void Edit(Action<AppearanceSettings> mutate)
        {
            if (_settingsUiLoading || _services is null)
            {
                return;
            }

            mutate(_services.Settings.Appearance);
            ApplyAppearance(save: true);
        }

        // ───────────────────────── обработчики ─────────────────────────

        private void AppearanceEnabledToggle_Changed(object sender, RoutedEventArgs e) =>
            Edit(a => a.Enabled = AppearanceEnabledToggle.IsChecked == true);

        private void BackdropMode_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is not RadioButton { Tag: string tag } ||
                !Enum.TryParse(tag, ignoreCase: true, out BackdropMode mode))
            {
                return;
            }

            UpdateBackdropPanels(mode);
            Edit(a => a.BackdropMode = mode);
        }

        private void UpdateBackdropPanels(BackdropMode mode)
        {
            GradientPanel.Visibility = mode == BackdropMode.Gradient ? Visibility.Visible : Visibility.Collapsed;
            ImagePanel.Visibility = mode == BackdropMode.Image ? Visibility.Visible : Visibility.Collapsed;
            GlassPanel.Visibility = mode == BackdropMode.None ? Visibility.Collapsed : Visibility.Visible;
        }

        private void MotionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MotionCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
                Enum.TryParse(tag, ignoreCase: true, out BackdropMotion motion))
            {
                Edit(a => a.GradientMotion = motion);
            }
        }

        private void ImageFitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ImageFitCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
                Enum.TryParse(tag, ignoreCase: true, out BackdropFit fit))
            {
                Edit(a => a.ImageFit = fit);
            }
        }

        private void GlassSheenToggle_Changed(object sender, RoutedEventArgs e) =>
            Edit(a => a.GlassSheen = GlassSheenToggle.IsChecked == true);

        private void VignetteToggle_Changed(object sender, RoutedEventArgs e) =>
            Edit(a => a.Vignette = VignetteToggle.IsChecked == true);

        private void AnimationsToggle_Changed(object sender, RoutedEventArgs e) =>
            Edit(a => a.AnimationsEnabled = AnimationsToggle.IsChecked == true);

        private void CompactComposerToggle_Changed(object sender, RoutedEventArgs e) =>
            Edit(a => a.CompactComposer = CompactComposerToggle.IsChecked == true);

        private void FontCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FontCombo.SelectedItem is ComboBoxItem { Tag: string family })
            {
                Edit(a => a.FontFamily = family);
            }
        }

        private void ChatWidthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChatWidthCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
            {
                Edit(a => a.ChatColumnWidth = width);
            }
        }

        private void ChooseBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Фоновое изображение",
                // Same codec caveat as the avatar picker: .webp is not decodable on every install.
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                // Copied into the profile, like the avatar: the backdrop then survives the
                // original being moved or deleted, and travels with the profile folder.
                var root = ProfileStore.DataRootFor(ActiveProfile.Id);
                Directory.CreateDirectory(root);

                var extension = Path.GetExtension(dialog.FileName);
                var fileName = BackgroundFileStem + (string.IsNullOrEmpty(extension) ? ".img" : extension);
                var target = Path.Combine(root, fileName);

                RemoveStoredBackgrounds(root, keep: fileName);
                File.Copy(dialog.FileName, target, overwrite: true);
                AppearanceImageCache.Clear();

                _services.Settings.Appearance.BackgroundImagePath = fileName;
                _services.Settings.Appearance.BackdropMode = BackdropMode.Image;
                _services.Settings.Appearance.Enabled = true;
                LoadAppearanceUi(_services.Settings);
                ApplyAppearance(save: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RemoveBackgroundButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            try
            {
                RemoveStoredBackgrounds(ProfileStore.DataRootFor(ActiveProfile.Id), keep: null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A locked file is no reason to keep pointing the settings at it.
            }

            AppearanceImageCache.Clear();
            _services.Settings.Appearance.BackgroundImagePath = "";
            LoadAppearanceUi(_services.Settings);
            ApplyAppearance(save: true);
        }

        private static void RemoveStoredBackgrounds(string root, string? keep)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(root, BackgroundFileStem + ".*"))
            {
                if (keep is not null && string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                File.Delete(file);
            }
        }

        private void ResetAppearanceButton_Click(object sender, RoutedEventArgs e)
        {
            if (_services is null)
            {
                return;
            }

            var confirmed = MessageBox.Show(
                this,
                "Вернуть оформление к исходному виду? Тема и масштаб интерфейса не изменятся.",
                Title,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);

            if (confirmed != MessageBoxResult.OK)
            {
                return;
            }

            _services.Settings.Appearance = new AppearanceSettings();
            LoadAppearanceUi(_services.Settings);
            ApplyAppearance(save: true);
        }

        // ───────────────────────── загрузка и применение ─────────────────────────

        /// <summary>Pushes persisted values into every control without firing their handlers.</summary>
        private void LoadAppearanceUi(AppSettings settings)
        {
            var appearance = settings.Appearance;

            SyncThemeCards(settings.Theme);

            AppearanceEnabledToggle.IsChecked = appearance.Enabled;
            AccentPicker.Hex = appearance.AccentColor;

            BackdropNone.IsChecked = appearance.BackdropMode == BackdropMode.None;
            BackdropGradient.IsChecked = appearance.BackdropMode == BackdropMode.Gradient;
            BackdropImage.IsChecked = appearance.BackdropMode == BackdropMode.Image;
            UpdateBackdropPanels(appearance.BackdropMode);

            var colors = appearance.GradientColors;
            GradientColor1.Hex = colors.Count > 0 ? colors[0] : "";
            GradientColor2.Hex = colors.Count > 1 ? colors[1] : "";
            GradientColor3.Hex = colors.Count > 2 ? colors[2] : "";
            GradientColor4.Hex = colors.Count > 3 ? colors[3] : "";

            GradientAngleSlider.Value = appearance.GradientAngle;
            SelectByTag(MotionCombo, appearance.GradientMotion.ToString());
            MotionSpeedSlider.Value = appearance.MotionSpeed;

            BackgroundImageName.Text = string.IsNullOrWhiteSpace(appearance.BackgroundImagePath)
                ? "Не выбрано"
                : appearance.BackgroundImagePath;
            SelectByTag(ImageFitCombo, appearance.ImageFit.ToString());
            ImageBrightnessSlider.Value = appearance.ImageBrightness;
            ImageSaturationSlider.Value = appearance.ImageSaturation;
            ImageBlurSlider.Value = appearance.ImageBlur;

            GlassOpacitySlider.Value = appearance.GlassOpacity;
            GlassFrostSlider.Value = appearance.GlassFrost;
            GlassSheenToggle.IsChecked = appearance.GlassSheen;
            VignetteToggle.IsChecked = appearance.Vignette;

            SelectByTag(FontCombo, appearance.FontFamily);
            SelectByTag(ChatWidthCombo, appearance.ChatColumnWidth.ToString(CultureInfo.InvariantCulture));
            CornerRadiusSlider.Value = appearance.CornerRadius;
            AnimationsToggle.IsChecked = appearance.AnimationsEnabled;

            CompactComposerToggle.IsChecked = appearance.CompactComposer;
            CompactDelaySlider.Value = appearance.CompactDelayMs;
            CompactWidthSlider.Value = appearance.CompactWidthPercent;
            CompactHoverSlider.Value = appearance.CompactHoverRadius;

            ApplyAppearance(save: false);
        }

        private static void SelectByTag(ComboBox combo, string tag)
        {
            for (var i = 0; i < combo.Items.Count; i++)
            {
                if (combo.Items[i] is ComboBoxItem item &&
                    string.Equals(Convert.ToString(item.Tag), tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }

            combo.SelectedIndex = 0;
        }

        /// <summary>Repaints the window from the current settings, and optionally persists them.</summary>
        private void ApplyAppearance(bool save)
        {
            if (_services is null)
            {
                return;
            }

            var settings = _services.Settings;
            settings.Appearance.Normalize();

            if (_appearance is not null)
            {
                _appearance.DataRoot = ProfileStore.DataRootFor(ActiveProfile.Id);
                _appearance.Apply(settings.Appearance);
            }

            ApplyInterfaceOptions(settings.Appearance);
            _compact?.Apply(settings.Appearance);

            if (save)
            {
                _services.SettingsStore.Save(settings);
            }
        }

        /// <summary>
        /// The two knobs that are plain properties rather than resources: the window font, which
        /// every control inherits, and the width of the message column.
        /// </summary>
        private void ApplyInterfaceOptions(AppearanceSettings appearance)
        {
            if (string.IsNullOrWhiteSpace(appearance.FontFamily))
            {
                ClearValue(FontFamilyProperty);
            }
            else
            {
                // The families ship with the app as Resource-built TTFs; the trailing fallback is
                // what renders if a face ever fails to load.
                FontFamily = new FontFamily($"pack://application:,,,/Fonts/#{appearance.FontFamily}, Segoe UI");
            }

            MessagesPanel.MaxWidth = appearance.ChatColumnWidth <= 0 ? 1070 : appearance.ChatColumnWidth;
        }
    }
}
