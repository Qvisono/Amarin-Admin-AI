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

        /// <summary>
        /// Ready-made backdrops, so the feature looks good before anyone touches a slider. One per
        /// shipped palette, in <see cref="ThemeCatalog.Presets"/> order: a deep glow band between
        /// two near-black ends on the dark themes, a soft vaulted wash on the light ones. Every
        /// stop stays inside the palette's own luminance range so the backdrop reads as an
        /// extension of the window rather than a panel fighting it, and each hue span is kept
        /// short to avoid the grey mud a purple-to-orange interpolation lands in.
        /// </summary>
        private static readonly (string Name, string[] Colors, double Angle)[] GradientPresets =
        [
            ("Light",    ["#F1F1F4", "#FBFBFC", "#F4F3F1"], 135),
            ("Dark",     ["#0E0E10", "#1E1E24", "#080809"], 135),
            ("Obsidian", ["#000000", "#141418", "#050505"], 135),
            ("Graphite", ["#161719", "#232A38", "#101115"], 140),
            ("Matte",    ["#141518", "#1F2329", "#101113"], 138),
            ("Midnight", ["#080B11", "#182448", "#0A0E1B"], 140),
            ("Nord",     ["#0E141A", "#1D2E39", "#101C24"], 150),
            ("Cobalt",   ["#050F1A", "#0E2E48", "#07161F"], 145),
            ("Ocean",    ["#04121A", "#0D3646", "#061820"], 145),
            ("Slate",    ["#0D0F12", "#1C232C", "#101519"], 130),
            ("Amethyst", ["#0E0A12", "#241640", "#120D1A"], 125),
            ("Plum",     ["#120810", "#3A1030", "#180C16"], 122),
            ("Neon",     ["#07060B", "#2A1040", "#0C0A12"], 125),
            ("Rosé",     ["#1C1416", "#3E2229", "#241A1C"], 120),
            ("Quartz",   ["#100E11", "#301826", "#0C0B0D"], 118),
            ("Crimson",  ["#130C0C", "#3B161A", "#1A0E0E"], 118),
            ("Ember",    ["#130F0A", "#3B2010", "#1A1109"], 112),
            ("Ochre",    ["#1B1915", "#3E3016", "#171511"], 115),
            ("Rust",     ["#110F0E", "#3A1C0C", "#0C0B0A"], 112),
            ("Emerald",  ["#0A100F", "#0F3328", "#0C1614"], 150),
            ("Forest",   ["#0B0D0C", "#12301F", "#0E1110"], 148),
            ("Terminal", ["#0E100F", "#0E3220", "#080C0A"], 150),
            // Семейство Edge: концы нейтральные, цвет только в средней полосе — тем же
            // правилом, по которому в самих палитрах он сидит в акценте, а не в заливке.
            ("Edge Blue",    ["#0C0C0E", "#141A2A", "#08080A"], 135),
            ("Edge Lime",    ["#0C0C0E", "#17200E", "#08080A"], 135),
            ("Edge Amber",   ["#0C0C0E", "#221A0C", "#08080A"], 135),
            ("Edge Magenta", ["#0C0C0E", "#22101B", "#08080A"], 135),
            ("Silver",   ["#E9EEF6", "#FAFBFC", "#EFF0F5"], 140),
            ("Matte Light", ["#E8EAEE", "#F6F7F9", "#ECEEF1"], 138),
            ("Steel",    ["#E6EBF1", "#F9FBFC", "#EDF1F5"], 142),
            ("Frost",    ["#E2EEF7", "#F7FBFD", "#EBF3F8"], 150),
            ("Sakura",   ["#F5E5EC", "#FDF7F9", "#F8EEF2"], 125),
            ("Mint",     ["#E4F2EA", "#F6FCF9", "#EDF7F2"], 150),
            ("Paper",    ["#E4DCCC", "#F2ECE1", "#E9E1D3"], 135),
            ("Sand",     ["#F0E7D6", "#FBF6EC", "#F4EEE1"], 132),
            ("Sepia",    ["#EEE1D0", "#F8F2E9", "#F1E8DA"], 128),
            ("Ink",      ["#EBE6DB", "#FBF8F2", "#F2EEE5"], 130),
            ("Contrast", ["#ECECEC", "#FFFFFF", "#F4F4F4"], 140)
        ];

        private AppearanceManager? _appearance;
        private GrainOverlay? _grain;
        private ComposerCompactMode? _compact;
        private BalanceBadge? _balance;
        private ContextRing? _context;

        /// <summary>
        /// Saturation needs a pixel pass over the whole picture, so dragging its slider is
        /// debounced; every other slider applies on the frame.
        /// </summary>
        private readonly DispatcherTimer _appearanceDebounce =
            new() { Interval = TimeSpan.FromMilliseconds(160) };

        private bool _appearanceDebouncePending;

        /// <summary>Имя семейства, которое уже стоит на окне. null — ещё ни разу не ставили.</summary>
        private string? _fontFamily;

        private void InitializeAppearance()
        {
            _appearance = new AppearanceManager(this, BackdropHost);
            _grain = new GrainOverlay(this, GrainLayer);
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

            // Ссылку не храним намеренно: объект жив, пока живы подписки на SizeChanged области
            // чата, а тех держит само окно. Поле пришлось бы читать ради предупреждения CS0414.
            _ = new ComposerHeightLimiter(
                Chat, ComposerBorder, ComposerLayout, ComposerInputRow, ComposerToolbar,
                AttachmentsHost, MessageTextBox);

            _balance = new BalanceBadge(BalanceBadge, BalanceCoin, BalanceAmount, new BalanceStore());
            _context = new ContextRing(ContextBadge, ContextTrack, ContextProgress, ContextAmount);

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

            // Перекрашиваем раньше записи: WriteAtomic пишет временный файл и переименовывает
            // его, и при живом антивирусе эти десятки миллисекунд стояли прямо перед перекраской.
            ThemeManager.Apply(theme);

            // Сам слой зерна обновит EffectiveThemeChanged, но ползунок про это не знает: пока
            // человек его не трогал, он обязан показывать то, что тема действительно нарисовала.
            if (_services.Settings.Appearance.Grain is null)
            {
                _settingsUiLoading = true;
                GrainSlider.Value = GrainOverlay.Effective(_services.Settings.Appearance);
                _settingsUiLoading = false;
            }

            _services.SettingsStore.Save(_services.Settings);
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

            ThemeManager.Apply(_services.Settings.Theme);
            SyncThemeCards(_services.Settings.Theme);
            _services.SettingsStore.Save(_services.Settings);
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
                // Preview along the preset's own axis, matching how the backdrop is painted, so
                // the swatch is an honest thumbnail rather than a fixed diagonal.
                var (start, end) = PreviewAxis(angle);
                var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
                for (var i = 0; i < colors.Length; i++)
                {
                    brush.GradientStops.Add(new GradientStop(
                        AppearanceManager.Parse(colors[i]) ?? Colors.Gray,
                        (double)i / (colors.Length - 1)));
                }

                brush.Freeze();

                var swatch = new Border
                {
                    Width = 64,
                    Height = 34,
                    CornerRadius = new CornerRadius(7),
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

        /// <summary>
        /// Endpoints for a gradient angle inside the unit square, matching
        /// <c>AppearanceManager.AxisFor</c> so the preset swatch previews the real backdrop.
        /// </summary>
        private static (Point Start, Point End) PreviewAxis(double degrees)
        {
            const double radius = 0.5;
            var radians = degrees * Math.PI / 180.0;
            var dx = Math.Cos(radians) * radius;
            var dy = Math.Sin(radians) * radius;
            return (new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
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
            Bind(GrainSlider, GrainValue, v => $"{v * 100:0}%", (a, v) => a.Grain = v);
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

        private void CompactHoverToggle_Changed(object sender, RoutedEventArgs e) =>
            Edit(a => a.CompactHoverEnabled = CompactHoverToggle.IsChecked == true);

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

            // Через «Все файлы» выбрать можно что угодно, но архив данных забирает картинки
            // оформления только по закрытому списку расширений. Файл вне его тихо не доехал бы
            // до второй машины, и человек узнал бы об этом, только развернув там копию.
            if (!DataBundle.IsSupportedImageName(dialog.FileName))
            {
                MessageBox.Show(
                    this,
                    Loc.Format("S.Appearance.BackgroundBadFormat", string.Join(", ", DataBundle.ImageExtensions)),
                    Title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Copied into the profile, like the avatar: the backdrop then survives the
                // original being moved or deleted, and travels with the profile folder.
                var root = ProfileStore.DataRootFor(ActiveProfile.Id);
                Directory.CreateDirectory(root);

                var fileName = BackgroundFileStem + Path.GetExtension(dialog.FileName);
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

            // Пустое значение означает «как хочет тема», и ползунку надо показать именно то,
            // что человек увидит на экране, а не ноль.
            GrainSlider.Value = GrainOverlay.Effective(appearance);
            AnimationsToggle.IsChecked = appearance.AnimationsEnabled;

            CompactComposerToggle.IsChecked = appearance.CompactComposer;
            CompactHoverToggle.IsChecked = appearance.CompactHoverEnabled;
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
            _grain?.Apply(settings.Appearance);
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
            // У FontFamily нет равенства по значению: новый объект с тем же именем WPF считает
            // изменением наследуемого свойства и перемеряет всё дерево окна вместе с лентой чата.
            // А заходят сюда на каждое открытие настроек, где шрифт почти всегда тот же самый.
            var family = appearance.FontFamily ?? "";
            if (!string.Equals(family, _fontFamily, StringComparison.Ordinal))
            {
                _fontFamily = family;
                if (string.IsNullOrWhiteSpace(family))
                {
                    ClearValue(FontFamilyProperty);
                }
                else
                {
                    // The families ship with the app as Resource-built TTFs; the trailing fallback
                    // is what renders if a face ever fails to load.
                    FontFamily = new FontFamily($"pack://application:,,,/Fonts/#{family}, Segoe UI");
                }
            }

            MessagesPanel.MaxWidth = appearance.ChatColumnWidth <= 0 ? 1070 : appearance.ChatColumnWidth;
        }
    }
}
