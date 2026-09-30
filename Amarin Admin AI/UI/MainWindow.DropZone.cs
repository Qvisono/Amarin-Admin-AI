using System.Windows;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Перетаскивание файлов на всё окно (D15): прикрепить можно, бросив файл куда угодно, а не
    /// только в поле ввода. Логика прикрепления та же — <see cref="AddAttachments"/>, и
    /// неподходящий файл получает ту же понятную строку под полосой вложений.
    /// </summary>
    public partial class MainWindow
    {
        private void WireDropZone()
        {
            AllowDrop = true;
            DragEnter += Window_DragEnter;
        }

        private void Window_DragEnter(object sender, DragEventArgs e) => ShowDropZone(e.Data);

        /// <summary>
        /// Показывает зону приёма, если бросать есть что и есть куда. Под настройками, вопросом
        /// или блокировкой файлам не место: там их некуда прикрепить.
        /// </summary>
        internal bool ShowDropZone(IDataObject data)
        {
            if (_services is null || ChatCoveredByOverlay() || IsNoticeOpen || !HasDroppableAttachment(data))
            {
                return false;
            }

            DropOverlay.Visibility = Visibility.Visible;
            return true;
        }

        private void DropOverlay_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = HasDroppableAttachment(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        /// <summary>Курсор ушёл из окна или перетаскивание отменили (Esc).</summary>
        private void DropOverlay_DragLeave(object sender, DragEventArgs e) =>
            DropOverlay.Visibility = Visibility.Collapsed;

        private void DropOverlay_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            AcceptDrop(e.Data);
        }

        /// <summary>Брошенное — во вложения, тем же путём, что и из поля ввода.</summary>
        internal void AcceptDrop(IDataObject data)
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                AddAttachments(paths);
            }
            else
            {
                AddImage(ClipboardImages.TryRead(data, Loc.Get("S.Attach.Dropped")));
            }

            FocusMessageInput();
        }
    }
}
