using System.Reflection;
using System.Windows;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Перетаскивание на всё окно (D15).</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DropZoneUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-drop-ui-" + Guid.NewGuid().ToString("N"));

    public DropZoneUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_file_dropped_anywhere_is_attached_and_an_unknown_one_is_explained()
    {
        Directory.CreateDirectory(_root);
        var text = Path.Combine(_root, "notes.txt");
        var binary = Path.Combine(_root, "tool.exe");
        File.WriteAllText(text, "hello");
        File.WriteAllBytes(binary, [1, 2, 3]);

        var (shown, hidden, files, notes) = _wpf.Ui.Invoke(() =>
        {
            var services = UiServices.Build(Path.Combine(_root, "data"), "k", new HttpClientHandler());
            var window = new MainWindow();
            window.AttachServices(services);
            try
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { text, binary });
                var overlay = (FrameworkElement)window.FindName("DropOverlay");
                var visible = window.ShowDropZone(data) && overlay.Visibility == Visibility.Visible;
                window.AcceptDrop(data);
                var pending = (List<FileAttachment>)typeof(MainWindow)
                    .GetField("_pendingFiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var explained = (List<string>)typeof(MainWindow)
                    .GetField("_attachmentNotes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                return (visible, overlay.Visibility, pending.Select(file => file.FileName).ToList(), explained.Count);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.True(shown);
        Assert.Equal(Visibility.Collapsed, hidden);
        Assert.Equal(["notes.txt"], files);
        Assert.Equal(1, notes);
    }
}
