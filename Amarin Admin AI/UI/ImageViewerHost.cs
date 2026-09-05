using System.Windows;
using System.Windows.Media.Imaging;
using Amarin.Tools;

namespace Amarin.UI;

/// <summary>
/// Finds the window that owns a chat element and asks it to open the image viewer.
/// <para>
/// The view builders take their host as a plain <see cref="FrameworkElement"/> — they are also
/// exercised from tests and from the console renderer — so they cannot depend on
/// <see cref="MainWindow"/> directly. Walking up to the owning window keeps the click wiring in
/// one place and makes a missing viewer a no-op rather than a crash.
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
