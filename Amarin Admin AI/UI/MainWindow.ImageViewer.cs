using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Amarin.Tools;

namespace Amarin.UI
{
    /// <summary>
    /// Full-size view of a picture from the chat: attachments on a message, pending thumbnails
    /// in the composer, and images the assistant produced. Lives inside the window rather than
    /// in one of its own so it inherits the faked DPI that drives UI scaling.
    /// </summary>
    public partial class MainWindow
    {
        private readonly List<BitmapSource> _viewerImages = [];
        private readonly List<string?> _viewerLabels = [];
        private int _viewerIndex;
        private PanZoom? _viewerPanZoom;
        private Point _viewerDragStart;
        private Point _viewerOffsetStart;
        private bool _viewerDragging;

        /// <summary>Opens the viewer on one already-decoded picture.</summary>
        internal void ShowImage(BitmapSource image, string? label)
        {
            if (image is null)
            {
                return;
            }

            OpenViewer([image], [label], 0);
        }

        /// <summary>
        /// Opens the viewer on a set of attachments, starting at <paramref name="index"/>.
        /// Decodes at full resolution: the thumbnails on screen are decoded down to 112 or 192
        /// pixels wide, and blowing one of those up is not "the enlarged version".
        /// </summary>
        internal void ShowAttachments(IReadOnlyList<ImageAttachment> attachments, int index)
        {
            if (attachments is null || attachments.Count == 0)
            {
                return;
            }

            var images = new List<BitmapSource>(attachments.Count);
            var labels = new List<string?>(attachments.Count);
            var start = 0;

            for (var i = 0; i < attachments.Count; i++)
            {
                if (TryDecode(attachments[i], decodePixelWidth: 0) is not { } decoded)
                {
                    continue;
                }

                if (i == index)
                {
                    start = images.Count;
                }

                images.Add(decoded);
                labels.Add(attachments[i].Label);
            }

            OpenViewer(images, labels, start);
        }

        private void OpenViewer(List<BitmapSource> images, List<string?> labels, int index)
        {
            if (images.Count == 0)
            {
                return;
            }

            _viewerImages.Clear();
            _viewerImages.AddRange(images);
            _viewerLabels.Clear();
            _viewerLabels.AddRange(labels);
            _viewerIndex = Math.Clamp(index, 0, images.Count - 1);

            ImageViewerOverlay.Visibility = Visibility.Visible;
            Chat.IsHitTestVisible = false;
            ShowCurrentViewerImage();

            // Focus the overlay so the arrow keys and Escape land here rather than in the composer.
            Dispatcher.BeginInvoke(
                new Action(() => ImageViewerOverlay.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ShowCurrentViewerImage()
        {
            var image = _viewerImages[_viewerIndex];
            ImageViewerImage.Source = image;
            ImageViewerImage.Width = image.PixelWidth;
            ImageViewerImage.Height = image.PixelHeight;

            var many = _viewerImages.Count > 1;
            ImageViewerPrevButton.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            ImageViewerNextButton.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            ImageViewerCounter.Visibility = many ? Visibility.Visible : Visibility.Collapsed;
            ImageViewerCounter.Text = many ? $"{_viewerIndex + 1} / {_viewerImages.Count}" : "";

            ResetViewerFit();
        }

        /// <summary>Rebuilds the pan/zoom state for the current stage size and picture.</summary>
        private void ResetViewerFit()
        {
            if (ImageViewerImage.Source is not BitmapSource image ||
                ImageViewerStage.ActualWidth < 1 ||
                ImageViewerStage.ActualHeight < 1)
            {
                return;
            }

            _viewerPanZoom = new PanZoom(
                0,
                0,
                ImageViewerStage.ActualWidth,
                ImageViewerStage.ActualHeight,
                image.PixelWidth,
                image.PixelHeight,
                cover: false);

            ApplyViewerTransform();
        }

        private void ApplyViewerTransform()
        {
            if (_viewerPanZoom is not { } state)
            {
                return;
            }

            ImageViewerZoom.ScaleX = state.Scale;
            ImageViewerZoom.ScaleY = state.Scale;
            ImageViewerPan.X = state.OffsetX;
            ImageViewerPan.Y = state.OffsetY;
        }

        private void CloseImageViewer()
        {
            ImageViewerOverlay.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
            _viewerImages.Clear();
            _viewerLabels.Clear();
            _viewerPanZoom = null;
            _viewerDragging = false;

            Chat.IsHitTestVisible =
                ConfirmationOverlay.Visibility != Visibility.Visible &&
                DomainOverlay.Visibility != Visibility.Visible;
            FocusMessageInput();
        }

        private void StepViewer(int delta)
        {
            if (_viewerImages.Count < 2)
            {
                return;
            }

            _viewerIndex = (_viewerIndex + delta + _viewerImages.Count) % _viewerImages.Count;
            ShowCurrentViewerImage();
        }

        // ── Handlers ──────────────────────────────────────────────────────────────────

        private void ImageViewerCloseButton_Click(object sender, RoutedEventArgs e) => CloseImageViewer();

        private void ImageViewerPrevButton_Click(object sender, RoutedEventArgs e) => StepViewer(-1);

        private void ImageViewerNextButton_Click(object sender, RoutedEventArgs e) => StepViewer(1);

        private void ImageViewerStage_SizeChanged(object sender, SizeChangedEventArgs e) => ResetViewerFit();

        private void ImageViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    CloseImageViewer();
                    e.Handled = true;
                    break;

                case Key.Left:
                    StepViewer(-1);
                    e.Handled = true;
                    break;

                case Key.Right:
                    StepViewer(1);
                    e.Handled = true;
                    break;

                case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                    CopyViewerImage();
                    e.Handled = true;
                    break;
            }
        }

        private void ImageViewerStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ToggleViewerZoom(e.GetPosition(ImageViewerStage));
                e.Handled = true;
                return;
            }

            _viewerDragStart = e.GetPosition(ImageViewerStage);
            _viewerOffsetStart = new Point(ImageViewerPan.X, ImageViewerPan.Y);
            _viewerDragging = ImageViewerStage.CaptureMouse();
            e.Handled = true;
        }

        private void ImageViewerStage_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_viewerDragging || _viewerPanZoom is not { } state)
            {
                return;
            }

            var now = e.GetPosition(ImageViewerStage);
            state.SetOffset(
                _viewerOffsetStart.X + (now.X - _viewerDragStart.X),
                _viewerOffsetStart.Y + (now.Y - _viewerDragStart.Y));
            ApplyViewerTransform();
        }

        private void ImageViewerStage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_viewerDragging)
            {
                return;
            }

            _viewerDragging = false;
            ImageViewerStage.ReleaseMouseCapture();
        }

        private void ImageViewerStage_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_viewerPanZoom is not { } state)
            {
                return;
            }

            state.ZoomByWheel(e.Delta, e.GetPosition(ImageViewerStage));
            ApplyViewerTransform();
            e.Handled = true;
        }

        /// <summary>Double click flips between "whole picture" and actual pixels.</summary>
        private void ToggleViewerZoom(Point anchor)
        {
            if (_viewerPanZoom is not { } state)
            {
                return;
            }

            var atFit = Math.Abs(state.Scale - state.FitScale) < 0.001;
            state.ZoomTo(atFit ? 1.0 : state.FitScale, anchor);
            ApplyViewerTransform();
        }

        private void ImageViewerSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ImageViewerImage.Source is not BitmapSource image)
            {
                return;
            }

            var suggested = _viewerLabels.ElementAtOrDefault(_viewerIndex);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить изображение",
                Filter = "PNG|*.png|Все файлы|*.*",
                DefaultExt = ".png",
                FileName = SafeFileName(string.IsNullOrWhiteSpace(suggested) ? "image" : suggested) + ".png"
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                using var stream = File.Create(dialog.FileName);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImageViewerCopyButton_Click(object sender, RoutedEventArgs e) => CopyViewerImage();

        private void CopyViewerImage()
        {
            if (ImageViewerImage.Source is not BitmapSource image)
            {
                return;
            }

            // The clipboard is a shared OS resource and another process can hold it for a moment;
            // same retry shape as TrySetClipboardText.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetImage(image);
                    return;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    Thread.Sleep(60);
                }
            }

            MessageBox.Show(
                this,
                "Не удалось записать изображение в буфер обмена — его удерживает другое приложение.",
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
