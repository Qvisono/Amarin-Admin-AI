using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Обновление на главном экране: значок в шапке и его попап. До 1.30.0 о новой версии знала одна
/// страница настроек, а фоновую загрузку нечем было остановить, кроме галки автообновления.
/// </summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class UpdateBadgeUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-update-badge-" + Guid.NewGuid().ToString("N"));

    public UpdateBadgeUiTests(WpfFixture wpf) => _wpf = wpf;

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
    public void The_badge_shows_only_when_there_is_something_to_say()
    {
        var shown = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var badge = (ToggleButton)window.FindSetting("UpdateBadgeButton")!;
            var track = (FrameworkElement)window.FindSetting("UpdateBadgeTrack")!;
            var release = UpdateTestKit.Release("99.0.0");
            var states = new (string Name, Func<UpdateState, UpdateState> Change)[]
            {
                ("nothing", state => UpdateState.Initial),
                ("check failed", state => UpdateState.Initial with { LastCheckError = "нет сети" }),
                ("found", state => UpdateState.Initial with { Latest = release }),
                ("downloading", state => UpdateState.Initial with
                {
                    Latest = release,
                    Download = new UpdateDownload(release, UpdateDownloadOrigin.Background, false, false, 0.4, 1, null),
                    DownloadGeneration = 1
                }),
                ("ready", state => UpdateState.Initial with { Latest = release, Staged = UpdateTestKit.Staged(release) })
            };

            var result = new List<(string, Visibility, Visibility)>();
            try
            {
                foreach (var (name, change) in states)
                {
                    window.Updates.Seed(change);
                    result.Add((name, badge.Visibility, track.Visibility));
                }
            }
            finally
            {
                window.Updates.Seed(_ => UpdateState.Initial);
            }

            return result;
        });

        Assert.Equal(
            [
                ("nothing", Visibility.Collapsed, Visibility.Collapsed),
                ("check failed", Visibility.Collapsed, Visibility.Collapsed),
                ("found", Visibility.Visible, Visibility.Collapsed),
                ("downloading", Visibility.Visible, Visibility.Visible),
                ("ready", Visibility.Visible, Visibility.Collapsed)
            ],
            shown);
    }

    /// <summary>
    /// «Отменить загрузку» в попапе обрывает фоновую загрузку, и та не начинается заново со
    /// следующей проверкой: версия отложена до перезапуска.
    /// </summary>
    [Fact]
    public async Task Cancelling_from_the_badge_holds_until_the_next_start()
    {
        var outcome = await WithFakes(async (window, source, files, _) =>
        {
            var release = UpdateTestKit.Release("99.0.0");
            window.Updates.Start();
            source.Answer(UpdateTestKit.Found(release));
            await Until(() => files.Downloads.Count == 1);

            var cancel = (Button)window.FindSetting("UpdateBadgeCancelButton")!;
            var offered = cancel.Visibility;
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => window.Updates.State.Download is null);

            window.Updates.CheckNow();
            source.Answer(UpdateTestKit.Found(release));
            await Until(() => !window.Updates.State.CheckRunning);
            return (offered, files.Downloads[0].Token.IsCancellationRequested, files.Downloads.Count,
                window.Updates.State.Postponed, ((FrameworkElement)window.FindSetting("UpdateBadgeButton")!).Visibility,
                cancel.Visibility);
        });

        Assert.Equal(Visibility.Visible, outcome.offered);
        Assert.True(outcome.IsCancellationRequested);
        Assert.Equal(1, outcome.Count);
        Assert.Equal(UpdateTestKit.Release("99.0.0").Release, outcome.Postponed);

        // Значок остаётся — версия доступна, просто не качается сама; отменять больше нечего.
        Assert.Equal(Visibility.Visible, outcome.Item5);
        Assert.Equal(Visibility.Collapsed, outcome.Item6);
    }

    [Fact]
    public async Task Update_from_the_badge_joins_the_background_download()
    {
        var outcome = await WithFakes(async (window, source, files, app) =>
        {
            var release = UpdateTestKit.Release("99.0.0");
            window.Updates.Start();
            source.Answer(UpdateTestKit.Found(release));
            await Until(() => files.Downloads.Count == 1);

            var badge = (ToggleButton)window.FindSetting("UpdateBadgeButton")!;
            badge.IsChecked = true;
            ((Button)window.FindSetting("UpdateBadgeActionButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var popupClosed = badge.IsChecked != true;
            ((Button)window.FindSetting("UpdateConfirmApplyButton")!).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Until(() => window.Updates.State.Download is { InstallRequested: true });

            files.Downloads[0].Finish();
            await Until(() => app.Restarts.Count > 0);
            return (popupClosed, Downloads: files.Downloads.Count, Cancelled: files.Downloads[0].Token.IsCancellationRequested, Restarts: app.Restarts.Count);
        });

        Assert.True(outcome.popupClosed, "попап остался поверх вопроса");
        Assert.Equal(1, outcome.Downloads);
        Assert.False(outcome.Cancelled);
        Assert.Equal(1, outcome.Restarts);
    }

    /// <summary>
    /// Карточка в настройках предлагает ту же отмену: «Обновить» у фоновой загрузки к ней
    /// присоединяется, и другой кнопки остановить её там не было.
    /// </summary>
    [Fact]
    public void The_settings_card_offers_to_cancel_a_background_download_only()
    {
        var shown = _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var cancel = (FrameworkElement)window.FindSetting("UpdateCancelDownloadButton")!;
            var release = UpdateTestKit.Release("99.0.0");
            try
            {
                window.Updates.Seed(_ => UpdateState.Initial with
                {
                    Latest = release,
                    Download = new UpdateDownload(release, UpdateDownloadOrigin.Background, false, false, 0.4, 1, null),
                    DownloadGeneration = 1
                });
                var background = cancel.Visibility;
                window.Updates.Seed(state => state with { Download = state.Download! with { InstallRequested = true } });
                var wanted = cancel.Visibility;
                window.Updates.Seed(_ => UpdateState.Initial with { Latest = release });
                return (background, wanted, cancel.Visibility);
            }
            finally
            {
                window.Updates.Seed(_ => UpdateState.Initial);
            }
        });

        Assert.Equal((Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed), shown);
    }

    [Fact]
    public void The_download_ring_grows_clockwise_from_the_top()
    {
        var (quarter, half, full) = _wpf.Ui.Invoke(() =>
        {
            static Point End(Geometry arc) => ((ArcSegment)((PathGeometry)arc).Figures[0].Segments[0]).Point;
            return (End(MainWindow.BadgeArc(0.25)), End(MainWindow.BadgeArc(0.5)), MainWindow.BadgeArc(1) is EllipseGeometry);
        });

        Assert.Equal(17, quarter.X, 3);
        Assert.Equal(9, quarter.Y, 3);
        Assert.Equal(9, half.X, 3);
        Assert.Equal(17, half.Y, 3);
        Assert.True(full);
    }

    private Task<T> WithFakes<T>(Func<MainWindow, FakeUpdateSource, FakeUpdateFiles, FakeUpdateApp, Task<T>> body) => _wpf.Ui.Invoke(async () =>
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
            return await body(window, source, files, app);
        }
        finally
        {
            window.Updates.Stop();
            window.Close();
        }
    });

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "не дождались состояния");
    }
}
