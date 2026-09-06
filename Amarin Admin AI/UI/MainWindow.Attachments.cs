using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.Tools;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;

namespace Amarin.UI
{
    /// <summary>
    /// Image attachments for the composer: drag &amp; drop, paste, the Actions menu picker,
    /// and the thumbnail strip that grows the input border.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public partial class MainWindow
    {
        /// <summary>Venice bills per image and long content arrays derail small models.</summary>
        internal const int MaxAttachedImages = 10;

        private const double ThumbnailSize = 56;

        private readonly List<ImageAttachment> _pendingImages = [];

        private void AttachImageButton_Click(object sender, RoutedEventArgs e)
        {
            ActionsPopup.IsOpen = false;
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите изображения",
                Multiselect = true,
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.tif;*.tiff|Все файлы|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                AddImageFiles(dialog.FileNames);
            }
        }

        private void Composer_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasDroppableImage(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Composer_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddImageFiles(paths);
                return;
            }

            if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                AddImageFromBitmapSource(bitmap, "Перетащенное изображение");
            }
        }

        private static bool HasDroppableImage(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.Bitmap))
            {
                return true;
            }

            return data.GetData(DataFormats.FileDrop) is string[] paths &&
                   paths.Any(ImageHelpers.IsImageFile);
        }

        /// <summary>Ctrl+V from the message box: images first, otherwise let the TextBox paste text.</summary>
        private bool TryPasteImageFromClipboard()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var paths = Clipboard.GetFileDropList().Cast<string?>()
                        .Where(path => path is not null && ImageHelpers.IsImageFile(path))
                        .Select(path => path!)
                        .ToArray();
                    if (paths.Length > 0)
                    {
                        AddImageFiles(paths);
                        return true;
                    }
                }

                if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
                {
                    AddImageFromBitmapSource(image, "Вставленное изображение");
                    return true;
                }
            }
            catch (ExternalException)
            {
                // Another process is holding the clipboard — fall through to a normal text paste.
            }

            return false;
        }

        private void AddImageFiles(IEnumerable<string> paths)
        {
            var rejected = 0;
            foreach (var path in paths)
            {
                if (!ImageHelpers.IsImageFile(path))
                {
                    rejected++;
                    continue;
                }

                if (_pendingImages.Count >= MaxAttachedImages)
                {
                    rejected++;
                    continue;
                }

                try
                {
                    var attachment = ImageHelpers.FromFile(path);
                    if (attachment is null)
                    {
                        rejected++;
                        continue;
                    }

                    _pendingImages.Add(attachment);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
                {
                    rejected++;
                }
            }

            RefreshAttachedImages(rejected);
        }

        private void AddImageFromBitmapSource(BitmapSource source, string label)
        {
            if (_pendingImages.Count >= MaxAttachedImages)
            {
                RefreshAttachedImages(rejected: 1);
                return;
            }

            try
            {
                using var stream = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(source));
                encoder.Save(stream);
                stream.Position = 0;

                using var bitmap = new Bitmap(stream);
                _pendingImages.Add(ImageHelpers.FromBitmap(bitmap, label));
                RefreshAttachedImages(rejected: 0);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or ExternalException)
            {
                RefreshAttachedImages(rejected: 1);
            }
        }

        private void RemovePendingImage(ImageAttachment attachment)
        {
            _pendingImages.Remove(attachment);
            RefreshAttachedImages(rejected: 0);
        }

        private void ClearPendingImages()
        {
            if (_pendingImages.Count == 0)
            {
                return;
            }

            _pendingImages.Clear();
            RefreshAttachedImages(rejected: 0);
        }

        private void RefreshAttachedImages(int rejected)
        {
            AttachedImagesPanel.Items.Clear();
            foreach (var attachment in _pendingImages)
            {
                AttachedImagesPanel.Items.Add(CreateThumbnail(attachment));
            }

            AttachedImagesHost.Visibility = _pendingImages.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;

            // Единственное место, где меняется наличие вложений — компактный режим слушает его
            // отсюда, а не с каждой точки добавления (перетаскивание, вставка, диалог файла).
            _compact?.SetHasAttachments(_pendingImages.Count > 0);

            UpdateAttachmentWarning(rejected);
        }

        private void UpdateAttachmentWarning(int rejected)
        {
            var notes = new List<string>();
            if (rejected > 0)
            {
                notes.Add(_pendingImages.Count >= MaxAttachedImages
                    ? $"Можно прикрепить не больше {MaxAttachedImages} изображений — лишние пропущены."
                    : $"Пропущено файлов: {rejected} (не изображение или не читается).");
            }

            // The model can change after the images are attached, so re-check on every refresh.
            if (_pendingImages.Count > 0 && !CurrentModelSupportsVision())
            {
                notes.Add($"Модель {VeniceModelCatalog.GetDisplayName(CurrentModelId())} " +
                          "не поддерживает изображения — выберите модель с поддержкой vision.");
            }

            AttachedImagesWarning.Text = string.Join(" ", notes);
            AttachedImagesWarning.Visibility = notes.Count == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private bool CurrentModelSupportsVision()
        {
            var modelId = CurrentModelId();
            if (string.IsNullOrWhiteSpace(modelId) || VeniceModelCatalog.IsAuto(modelId))
            {
                // "auto" resolves at send time; warning about a model nobody picked is noise.
                return true;
            }

            var catalog = _services?.Models.Cached;
            var info = catalog?.FirstOrDefault(
                item => item.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));

            // Unknown model: stay quiet rather than cry wolf on a catalog that has not loaded.
            return info is null || VeniceModelCatalog.HasVision(info);
        }

        private FrameworkElement CreateThumbnail(ImageAttachment attachment)
        {
            var host = new Grid { Margin = new Thickness(0, 0, 6, 0) };

            var frame = new Border
            {
                Width = ThumbnailSize,
                Height = ThumbnailSize,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = attachment.Label
            };
            RoundedClip.SetRadius(frame, 6);
            frame.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                ShowAttachments(_pendingImages, _pendingImages.IndexOf(attachment));
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Border.Default");
            frame.SetResourceReference(Border.BackgroundProperty, "Bg.Card");

            if (TryDecode(attachment) is { } source)
            {
                frame.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
            }

            host.Children.Add(frame);
            host.Children.Add(CreateRemoveButton(attachment));
            return host;
        }

        private Button CreateRemoveButton(ImageAttachment attachment)
        {
            var remove = new Button
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, -4, -4, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Cursor = Cursors.Hand,
                ToolTip = "Убрать изображение",
                Content = new TextBlock
                {
                    Text = "✕",
                    FontSize = 8,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Template = BuildRemoveButtonTemplate()
            };
            remove.Click += (_, _) => RemovePendingImage(attachment);
            return remove;
        }

        private static ControlTemplate BuildRemoveButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A)));
            border.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)));
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        /// <summary>Decodes a stored attachment for display, at thumbnail resolution.</summary>
        internal static BitmapImage? TryDecode(ImageAttachment attachment, int decodePixelWidth = 112)
        {
            try
            {
                var bytes = Convert.FromBase64String(attachment.Base64);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                if (decodePixelWidth > 0)
                {
                    image.DecodePixelWidth = decodePixelWidth;
                }

                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>Measured size of a thumbnail row, used by the message bubbles.</summary>
        internal static Size ThumbnailBox => new(ThumbnailSize, ThumbnailSize);
    }
}
