using System.Windows;
using System.Windows.Media.Imaging;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Находит окно, которому принадлежит элемент чата, и просит его открыть просмотр картинки.
/// <para>
/// Сборщики ленты получают хозяина простым <see cref="FrameworkElement"/> (их гоняют и тесты) и
/// зависеть от <see cref="MainWindow"/> напрямую не могут. Подъём к окну держит обработку щелчка в
/// одном месте, а отсутствие просмотра делает тихим ничем, а не аварией.
/// </para>
/// </summary>
internal static class ImageViewerHost
{
    public static void Open(FrameworkElement host, IReadOnlyList<ImageAttachment> images, int index) =>
        Find(host)?.ShowAttachments(images, index);

    public static void Open(FrameworkElement host, BitmapSource image, string? label) =>
        Find(host)?.ShowImage(image, label);

    private static MainWindow? Find(FrameworkElement host) =>
        host as MainWindow
        ?? Window.GetWindow(host) as MainWindow
        ?? Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
}
