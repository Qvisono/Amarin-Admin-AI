using Amarin.Browser;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Встроенный браузер агента (1.37.0): окно ставит фабрику его окон, без которой агент
    /// инструмента <c>browser</c> не получает.
    /// </summary>
    /// <remarks>
    /// Только в настоящей программе (<see cref="IsRealApp"/>), по образцу плашки мыши: оконные
    /// тесты поднимают это же окно, и браузер встал бы поверх того, чем занят человек.
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>Открытые окна браузера: закрытие программы закрывает и их.</summary>
        private readonly List<WebViewBrowser> _agentBrowsers = [];

        private void AttachAgentBrowser(AppServices? previous, AppServices services)
        {
            if (previous?.Browser is { } old)
            {
                old.Factory = null;
            }

            if (!IsRealApp || services.Browser is not { } hub)
            {
                return;
            }

            // Агент зовёт фабрику со своего потока, а список окон живёт на потоке окна. Профиль
            // браузера — того профиля программы, что открыт в миг первого действия агента.
            hub.Factory = () => Dispatcher.Invoke(() =>
            {
                var browser = new WebViewBrowser(
                    Dispatcher,
                    Path.Combine(services.Profile.DataRoot, BrowserHub.FolderName),
                    services.Options.Download.MaxSizeBytes,
                    closed => _agentBrowsers.Remove(closed));
                _agentBrowsers.Add(browser);
                return browser;
            });
        }

        private void DetachAgentBrowser()
        {
            if (_services?.Browser is { } hub)
            {
                hub.Factory = null;
            }

            foreach (var browser in _agentBrowsers.ToList())
            {
                browser.Close();
            }

            _agentBrowsers.Clear();
        }
    }
}
