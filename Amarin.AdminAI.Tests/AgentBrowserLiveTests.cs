using System.Windows;
using Amarin.Browser;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Живой прогон встроенного браузера агента (1.37.0): настоящий WebView2, настоящая страница.
/// </summary>
/// <remarks>
/// Только с <c>AMARIN_BROWSER_LIVE=1</c>: окно браузера встаёт на экран, а страница грузится из
/// сети. Только чтение и переход по ссылке — ничего не отправляет. Снимок страницы кладётся в
/// <c>AMARIN_BROWSER_LIVE_OUT</c>, если папка задана.
/// </remarks>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class AgentBrowserLiveTests
{
    private readonly WpfFixture _wpf;

    public AgentBrowserLiveTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public async Task The_real_browser_opens_reads_clicks_and_captures_a_page()
    {
        if (Environment.GetEnvironmentVariable("AMARIN_BROWSER_LIVE") != "1")
        {
            return;
        }

        var profile = Path.Combine(Path.GetTempPath(), "amarin-browser-live-" + Guid.NewGuid().ToString("N"));
        var dispatcher = _wpf.Ui.Invoke(() => Application.Current.Dispatcher);
        var browser = new WebViewBrowser(dispatcher, profile, 10 * 1024 * 1024, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var page = await browser.OpenAsync(new Uri("https://example.com/"), timeout.Token);
            Assert.Contains("Example Domain", page.Title, StringComparison.Ordinal);
            var link = Assert.Single(page.Elements, element => element.Role == "link");
            Assert.NotNull(await browser.FindAsync(link.Id, timeout.Token));

            await browser.ClickAsync(link.Id, timeout.Token);
            var next = await browser.ReadAsync(2000, 50, timeout.Token);
            Assert.NotEqual(page.Url, next.Url);

            // Прежнее чтение больше не действует: следующее сняло его метки.
            Assert.Null(await browser.FindAsync(link.Id, timeout.Token));

            var shot = await browser.ScreenshotAsync(timeout.Token);
            Assert.NotNull(shot);
            if (Environment.GetEnvironmentVariable("AMARIN_BROWSER_LIVE_OUT") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(Path.Combine(folder, "agent-browser.jpg"), shot, timeout.Token);
            }

            Assert.True(await browser.BackAsync(timeout.Token));
            Assert.Contains("Example Domain", (await browser.ReadAsync(2000, 50, timeout.Token)).Title, StringComparison.Ordinal);
        }
        finally
        {
            browser.Close();
        }
    }

    /// <summary>Ввод доверенным событием: текст доходит до поля. Форма не отправляется.</summary>
    [Fact]
    public async Task The_real_browser_types_into_a_field()
    {
        if (Environment.GetEnvironmentVariable("AMARIN_BROWSER_LIVE") != "1")
        {
            return;
        }

        var profile = Path.Combine(Path.GetTempPath(), "amarin-browser-live-" + Guid.NewGuid().ToString("N"));
        var dispatcher = _wpf.Ui.Invoke(() => Application.Current.Dispatcher);
        var browser = new WebViewBrowser(dispatcher, profile, 10 * 1024 * 1024, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var page = await browser.OpenAsync(new Uri("https://html.duckduckgo.com/html/"), timeout.Token);
            var field = page.Elements.First(element => element.Role == "textbox" && !element.Password);

            await browser.TypeAsync(field.Id, "amarin test", replace: true, timeout.Token);

            Assert.Equal("amarin test", (await browser.FindAsync(field.Id, timeout.Token))?.Value);
        }
        finally
        {
            browser.Close();
        }
    }
}
