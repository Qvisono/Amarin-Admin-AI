using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Amarin.Core;
using Amarin.Tools;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Черновик у каждого чата (D12) на живом окне: набранное и вложения остаются со своим чатом при
/// переключении. С 1.30.0 открытие чата берёт черновик из памяти, не дожидаясь очереди записи, —
/// проверяем, что ничего не потерялось и не переехало.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class DraftSwitchUiTests : IDisposable
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-draft-switch-ui-" + Guid.NewGuid().ToString("N"));

    public DraftSwitchUiTests(WpfFixture wpf) => _wpf = wpf;

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
    public void Text_and_attachments_stay_with_their_chat_across_switches()
    {
        var (leftEmpty, backToFirst, backToSecond, slowest) = _wpf.Ui.Invoke(() =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            services.Settings.AutoCheckUpdates = false;
            Save(services, "first");
            Save(services, "second");

            var window = new MainWindow();
            window.AttachServices(services);
            try
            {
                var box = (TextBox)window.FindName("MessageTextBox")!;
                var files = (List<FileAttachment>)typeof(MainWindow).GetField("_pendingFiles", Hidden)!.GetValue(window)!;
                var watch = new Stopwatch();
                var slowest = 0.0;
                void Open(string id)
                {
                    watch.Restart();
                    Call(window, "OpenChat", id);
                    slowest = Math.Max(slowest, watch.Elapsed.TotalMilliseconds);
                }

                Open("first");
                box.Text = "черновик первого";
                files.Add(new FileAttachment("aGVsbG8=", "text/plain", "notes.txt", 5));

                Open("second");
                var empty = (box.Text, files.Count);
                box.Text = "черновик второго";

                Open("first");
                var first = (box.Text, files.Select(file => file.FileName).ToList());

                Open("second");
                var second = (box.Text, files.Count);
                return (empty, first, second, slowest);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(("", 0), leftEmpty);
        Assert.Equal("черновик первого", backToFirst.Text);
        Assert.Equal(["notes.txt"], backToFirst.Item2);
        Assert.Equal(("черновик второго", 0), backToSecond);

        // Открытие не ждёт очереди записи черновиков: до 1.30.0 здесь бывало до двух секунд.
        Assert.True(slowest < 1500, $"открытие чата заняло {slowest:0} мс");
    }

    private static void Save(AppServices services, string id)
    {
        services.ChatStore.Save(new ChatSession { Id = id, Title = id, CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now });
        services.ChatStore.Flush();
    }

    private static object? Call(MainWindow window, string method, params object?[] args) =>
        typeof(MainWindow)
            .GetMethods(Hidden)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length)
            .Invoke(window, args);
}
