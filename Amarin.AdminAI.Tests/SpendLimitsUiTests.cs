using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Лимиты трат (E1) на живом окне: блок настроек и вопрос посреди хода.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class SpendLimitsUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-limits-ui-" + Guid.NewGuid().ToString("N"));

    public SpendLimitsUiTests(WpfFixture wpf) => _wpf = wpf;

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

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in Find<T>(child))
            {
                yield return nested;
            }
        }
    }

    [Fact]
    public void Typing_a_daily_limit_saves_a_fresh_limits_object_and_junk_keeps_the_old_value()
    {
        var (saved, replaced, afterJunk, shown) = _wpf.Ui.Invoke(() =>
        {
            var services = UiServices.Build(_root, "k", new HttpClientHandler());
            var before = services.Settings.SpendLimits;
            var block = new SpendLimitsBlock();
            block.Load(services);
            block.Measure(new Size(520, 600));
            block.Arrange(new Rect(0, 0, 520, 600));
            block.UpdateLayout();

            var name = Loc.Get("S.Limit.Profile") + " · " + Loc.Get("S.Limit.PerDay");
            var box = Find<TextBox>(block).Single(item => AutomationProperties.GetName(item) == name);
            box.Text = "$5";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            var day = services.Settings.SpendLimits.DayUsd;

            box.Text = "five dollars";
            box.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));

            return (day, !ReferenceEquals(before, services.Settings.SpendLimits), services.Settings.SpendLimits.DayUsd, box.Text);
        });

        Assert.Equal(5m, saved);
        Assert.True(replaced);
        Assert.Equal(5m, afterJunk);
        Assert.Equal("5.00", shown);
    }

    [Fact]
    public async Task The_question_answers_continue_and_change_limits_answers_no_and_opens_the_page()
    {
        var question = new SpendQuestion(new SpendBreach(SpendLimitKind.ProfileDay, 1m, 1.2m), 0.4m);
        var method = typeof(MainWindow).GetMethod("AskSpendOnUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MainWindow? window = null;
        try
        {
            var first = _wpf.Ui.Invoke(() =>
            {
                window = new MainWindow();
                window.AttachServices(UiServices.Build(_root, "k", new HttpClientHandler()));
                return (Task<bool>)method.Invoke(window, [question, CancellationToken.None])!;
            });
            _wpf.Ui.Invoke(() =>
            {
                ((Button)window!.FindName("NoticePrimaryButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return 0;
            });
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)));

            var second = _wpf.Ui.Invoke(() => (Task<bool>)method.Invoke(window, [question, CancellationToken.None])!);
            _wpf.Ui.Invoke(() =>
            {
                var extra = (ContentControl)window!.FindName("NoticeExtra");
                ((Button)extra.Content).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return 0;
            });
            Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Visibility.Visible, _wpf.Ui.Invoke(() => ((FrameworkElement)window!.FindName("SettingsOverlay")).Visibility));
        }
        finally
        {
            _wpf.Ui.Invoke(() =>
            {
                window?.Close();
                return 0;
            });
        }
    }

    [Fact]
    public async Task Stopping_the_turn_closes_an_open_question_as_no()
    {
        var question = new SpendQuestion(new SpendBreach(SpendLimitKind.Turn, 0.5m, 0.6m), 0.6m);
        var method = typeof(MainWindow).GetMethod("AskSpendOnUiAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var stop = new CancellationTokenSource();
        MainWindow? window = null;
        try
        {
            var answer = _wpf.Ui.Invoke(() =>
            {
                window = new MainWindow();
                window.AttachServices(UiServices.Build(_root, "k", new HttpClientHandler()));
                return (Task<bool>)method.Invoke(window, [question, stop.Token])!;
            });

            stop.Cancel();

            Assert.False(await answer.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(Visibility.Collapsed, _wpf.Ui.Invoke(() => ((FrameworkElement)window!.FindName("NoticeOverlay")).Visibility));
        }
        finally
        {
            _wpf.Ui.Invoke(() =>
            {
                window?.Close();
                return 0;
            });
        }
    }
}
