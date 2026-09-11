using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Карточка документа в композере. У документа нет предпросмотра, поэтому вместо снимка на
/// плитке стоят расширение, имя и размер — и это единственное, по чему человек узнаёт, что
/// именно уйдёт модели.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AttachmentCardTests
{
    private readonly WpfFixture _wpf;

    public AttachmentCardTests(WpfFixture wpf) => _wpf = wpf;

    private static MainWindow Window() =>
        Application.Current.Windows.OfType<MainWindow>().Single();

    private static List<FileAttachment> PendingFiles(MainWindow window) =>
        (List<FileAttachment>)typeof(MainWindow)
            .GetField("_pendingFiles", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static void Invoke(MainWindow window, string method) =>
        typeof(MainWindow)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, []);

    private static ItemsControl Panel(MainWindow window) =>
        (ItemsControl)window.FindName("AttachmentsPanel")!;

    private static FrameworkElement Host(MainWindow window) =>
        (FrameworkElement)window.FindName("AttachmentsHost")!;

    private static FileAttachment Doc(string name = "договор.pdf", long size = 2048) =>
        new(Convert.ToBase64String("%PDF-1.4"u8.ToArray()), "application/pdf", name, size);

    /// <summary>Все тексты карточки — вложенные окно даёт их одним списком.</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var found = new List<string>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                found.Add(text.Text);
            }

            found.AddRange(Texts(child));
        }

        return found;
    }

    private T WithFile<T>(Func<MainWindow, T> read) => _wpf.Ui.Invoke(() =>
    {
        var window = Window();
        try
        {
            PendingFiles(window).Add(Doc());
            Invoke(window, "RefreshAttachments");
            window.UpdateLayout();
            return read(window);
        }
        finally
        {
            // Окно общее на всю коллекцию — состояние обязано вернуться.
            Invoke(window, "ClearPendingAttachments");
            window.UpdateLayout();
        }
    });

    [Fact]
    public void The_card_names_the_file_and_its_size()
    {
        var texts = WithFile(window =>
        {
            var card = (FrameworkElement)Panel(window).Items[0]!;
            card.Measure(new Size(400, 100));
            card.Arrange(new Rect(0, 0, 400, 100));
            card.UpdateLayout();
            return Texts(card);
        });

        Assert.Contains("PDF", texts);
        Assert.Contains("договор.pdf", texts);
        Assert.Contains("2 КБ", texts);
    }

    [Fact]
    public void The_strip_appears_with_a_document_and_folds_away_after_it()
    {
        var shown = WithFile(window => Host(window).Visibility);
        var hidden = _wpf.Ui.Invoke(() => Host(Window()).Visibility);

        Assert.Equal(Visibility.Visible, shown);
        Assert.Equal(Visibility.Collapsed, hidden);
    }

    [Fact]
    public void The_cross_removes_the_document()
    {
        var left = _wpf.Ui.Invoke(() =>
        {
            var window = Window();
            try
            {
                PendingFiles(window).Add(Doc());
                Invoke(window, "RefreshAttachments");
                window.UpdateLayout();

                var card = (Grid)Panel(window).Items[0]!;
                var cross = card.Children.OfType<Button>().Single();
                cross.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return PendingFiles(window).Count;
            }
            finally
            {
                Invoke(window, "ClearPendingAttachments");
                window.UpdateLayout();
            }
        });

        Assert.Equal(0, left);
    }

    [Fact]
    public void The_card_is_actually_painted_in_both_palettes()
    {
        // Карточка целиком на ресурсах темы: захардкоженная кисть не пережила бы смену палитры.
        foreach (var theme in new[] { Amarin.Core.AppTheme.Dark, Amarin.Core.AppTheme.Light })
        {
            var painted = _wpf.Ui.Invoke(() =>
            {
                var previous = ThemeManager.Current.Theme;
                var window = Window();
                try
                {
                    ThemeManager.Apply(theme);
                    PendingFiles(window).Add(Doc());
                    Invoke(window, "RefreshAttachments");

                    var card = (FrameworkElement)Panel(window).Items[0]!;
                    card.Measure(new Size(400, 100));
                    card.Arrange(new Rect(0, 0, 400, 100));
                    card.UpdateLayout();

                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(card.ActualWidth),
                        (int)Math.Ceiling(card.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(card);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);

                    var opaque = 0;
                    for (var i = 3; i < pixels.Length; i += 4)
                    {
                        if (pixels[i] > 0)
                        {
                            opaque++;
                        }
                    }

                    return opaque;
                }
                finally
                {
                    Invoke(window, "ClearPendingAttachments");
                    ThemeManager.Apply(previous);
                    window.UpdateLayout();
                }
            });

            Assert.True(painted > 500, $"в теме {theme} карточка почти прозрачна: {painted} пикселей");
        }
    }

    [Fact]
    public void The_badge_is_the_extension_in_capitals()
    {
        Assert.Equal("PDF", MainWindow.FileBadge("отчёт.pdf"));
        Assert.Equal("XLSX", MainWindow.FileBadge("таблица.XLSX"));
        Assert.Equal("ФАЙЛ", MainWindow.FileBadge("makefile"));
    }
}
