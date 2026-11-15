using System.Net;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Amarin.Core;
using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class KeyRemovalUiTests : IDisposable
{
    private readonly WpfFixture _wpf;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-key-removal-" + Guid.NewGuid().ToString("N"));

    public KeyRemovalUiTests(WpfFixture wpf) => _wpf = wpf;

    public void Dispose()
    {
        if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); }
    }

    [Fact]
    public void The_confirmation_is_visible_and_accepts_input_above_settings()
    {
        _wpf.Ui.Invoke(() =>
        {
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var settings = (FrameworkElement)window.FindSetting("SettingsOverlay");
            var previous = settings.Visibility;
            try
            {
                window.OpenSettings(window.SettingsUi.NavKey);
                window.AskKeyRemoval("Проверка", "S.Key.DeleteDesc", () => { });
                window.UpdateLayout();
                var button = (Button)window.FindSetting("KeyRemoveYesButton");
                Assert.True(button.IsVisible);
                Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0);
                var center = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
                var hit = window.InputHitTest(center) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, button))
                {
                    hit = VisualTreeHelper.GetParent(hit);
                }

                Assert.Same(button, hit);
            }
            finally
            {
                ((FrameworkElement)window.FindSetting("KeyRemoveOverlay")).Visibility = Visibility.Collapsed;
                settings.Visibility = previous;
            }

            return true;
        });
    }

    [Fact]
    public void Confirming_deletion_from_the_key_menu_removes_the_key_after_reload()
    {
        _wpf.Ui.Invoke(() =>
        {
            using var services = UiServices.Build(_root, "", new OfflineHandler());
            var record = services.KeyStore.Add("Удаляемый", "test-key-to-remove", LlmProvider.OpenAI)!;
            services.ApplyActiveKey();
            var window = Application.Current.Windows.OfType<MainWindow>().Single();
            var host = (Panel)window.FindSetting("MessagesPanel");
            var page = new SettingsKeyPage();
            host.Children.Add(page);
            ContextMenu? menu = null;
            try
            {
                page.Attach(services);
                page.RefreshKeyRows();
                window.UpdateLayout();
                var rows = (ItemsControl)page.FindName("KeyRows");
                var row = Assert.Single(rows.Items.OfType<FrameworkElement>());
                page.OpenKeyMenu(row, services.KeyStore.List().Single(), PlacementMode.Bottom);
                menu = PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>()
                    .Select(source => source.RootVisual)
                    .SelectMany(Descendants<ContextMenu>)
                    .Single(open => open.IsOpen);
                var delete = menu.Items.OfType<MenuItem>()
                    .Single(item => Equals(item.Header, Loc.Get("S.Common.Delete")));
                ((IInvokeProvider)new MenuItemAutomationPeer(delete).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                var overlay = (FrameworkElement)window.FindSetting("KeyRemoveOverlay");
                Assert.Equal(Visibility.Visible, overlay.Visibility);
                ((Button)window.FindSetting("KeyRemoveYesButton"))
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(Visibility.Collapsed, overlay.Visibility);
                Assert.Empty(services.KeyStore.List());
                Assert.Empty(rows.Items);
                services.KeyStore.Load();
                Assert.DoesNotContain(services.KeyStore.List(), entry => entry.Id == record.Id);
            }
            finally
            {
                if (menu is not null) { menu.IsOpen = false; }
                ((FrameworkElement)window.FindSetting("KeyRemoveOverlay")).Visibility = Visibility.Collapsed;
                host.Children.Remove(page);
            }

            return true;
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T own) { yield return own; }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
    }
}
