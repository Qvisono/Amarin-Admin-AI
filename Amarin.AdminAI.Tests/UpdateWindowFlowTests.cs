using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Обновление целиком через кнопки окна, на подставных GitHub и файлах: то, что автомат
/// проверяет без окна, здесь проходит плашка, вопрос «Обновить до версии…» и его кнопка.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class UpdateWindowFlowTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-update-flow-" + Guid.NewGuid().ToString("N"));

    public UpdateWindowFlowTests(WpfFixture wpf) => _wpf = wpf;

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

    /// <summary>
    /// До 1.30.0 кнопка у фоновой загрузки называлась «Отмена» и обрывала её. Теперь «Обновить»
    /// присоединяется к идущей загрузке: та не начинается заново, а по готовности версия ставится
    /// и программа перезапускается.
    /// </summary>
    [Fact]
    public async Task Update_during_a_background_download_waits_for_it_and_installs()
    {
        var outcome = await _wpf.Ui.Invoke(async () =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            services.Settings.AutoCheckUpdates = false;
            var window = new MainWindow();
            window.AttachServices(services);
            var source = new FakeUpdateSource();
            var files = new FakeUpdateFiles();
            var app = new FakeUpdateApp();
            window.UseUpdatePorts(source, files, app);
            try
            {
                var release = UpdateTestKit.Release("99.0.0");
                window.Updates.Start();
                source.Answer(UpdateTestKit.Found(release));
                await Until(() => files.Downloads.Count == 1);
                var background = files.Downloads[0];
                var button = (Button)window.FindName("UpdateNowButton")!;
                var before = (string)button.Content;

                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var asked = ((FrameworkElement)window.FindName("UpdateConfirmOverlay")!).Visibility;
                ((Button)window.FindName("UpdateConfirmApplyButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Until(() => window.Updates.State.Download is { InstallRequested: true });

                var joined = (
                    Cancelled: background.Token.IsCancellationRequested,
                    Downloads: files.Downloads.Count,
                    Label: (string)button.Content,
                    Status: ((TextBlock)window.FindName("UpdateStatusText")!).Text);

                background.Finish();
                await Until(() => app.Restarts.Count > 0);
                return (before, asked, joined, files.Swaps.Count, app.Restarts.Count);
            }
            finally
            {
                window.Updates.Stop();
                window.Close();
            }
        });

        Assert.Equal(Loc.Get("S.Updates.Update"), outcome.before);
        Assert.Equal(Visibility.Visible, outcome.asked);
        Assert.False(outcome.joined.Cancelled);
        Assert.Equal(1, outcome.joined.Downloads);
        Assert.Equal(Loc.Get("S.Common.Cancel"), outcome.joined.Label);
        Assert.StartsWith(Loc.Format("S.Updates.DownloadingToInstall", "99.0.0", 0)[..20], outcome.joined.Status, StringComparison.Ordinal);
        Assert.Equal(1, outcome.Item4);
        Assert.Equal(1, outcome.Item5);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "не дождались состояния");
    }
}
