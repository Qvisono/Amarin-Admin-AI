using System.Text;
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

    /// <summary>
    /// 1.37.0-beta.8, чат «Прорешать тесты»: варианты вопроса Moodle называются своими подписями, а не
    /// общим атрибутом name; спрятанный «Очистить мой выбор», отмеченный, пока ответа нет, в список не
    /// идёт, и нажатие отмечает выбранный вариант. Флажок, нарисованный подписью, остаётся и нажимается
    /// ею. Разметка — как у Moodle; страница — data:, сеть и вход не нужны, форма не отправляется.
    /// </summary>
    [Fact]
    public async Task The_real_browser_names_options_by_their_labels_and_lists_nothing_the_person_cannot_see()
    {
        if (Environment.GetEnvironmentVariable("AMARIN_BROWSER_LIVE") != "1")
        {
            return;
        }

        const string html = """
            <!doctype html><html><head><meta charset="utf-8"><style>
            .sr-only{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}
            </style></head><body><form>
            <fieldset><legend class="sr-only">Выберите один ответ:</legend><div class="answer">
            <div><input type="radio" name="q1:20_answer" value="0" id="q1:20_answer0" aria-labelledby="q1:20_answer0_label"><div id="q1:20_answer0_label"><span>a. </span><div>опричники</div></div></div>
            <div><input type="radio" name="q1:20_answer" value="1" id="q1:20_answer1" aria-labelledby="q1:20_answer1_label"><div id="q1:20_answer1_label"><span>b. </span><div>тысяцкие</div></div></div>
            <div><input type="radio" name="q1:20_answer" value="2" id="q1:20_answer2" aria-labelledby="q1:20_answer2_label"><div id="q1:20_answer2_label"><span>c. </span><div>рекруты</div></div></div>
            <div><input type="radio" name="q1:20_answer" value="3" id="q1:20_answer3" aria-labelledby="q1:20_answer3_label"><div id="q1:20_answer3_label"><span>d. </span><div>баскаки</div></div></div>
            </div>
            <div class="qtype_multichoice_clearchoice sr-only"><input type="radio" name="q1:20_answer" id="q1:20_answer-1" value="-1" class="sr-only" checked><label for="q1:20_answer-1"><a tabindex="-1" role="button" href="#">Очистить мой выбор</a></label></div>
            </fieldset>
            <div aria-hidden="true"><button type="button">Спрятанная кнопка</button></div>
            <p><label><input type="checkbox" class="sr-only" name="remember"> Запомнить меня</label></p>
            <input type="submit" name="next" value="Следующая страница">
            </form></body></html>
            """;

        var profile = Path.Combine(Path.GetTempPath(), "amarin-browser-live-" + Guid.NewGuid().ToString("N"));
        var dispatcher = _wpf.Ui.Invoke(() => Application.Current.Dispatcher);
        var browser = new WebViewBrowser(dispatcher, profile, 10 * 1024 * 1024, _ => { });
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            var page = await browser.OpenAsync(
                new Uri("data:text/html;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(html))), timeout.Token);

            var radios = page.Elements.Where(element => element.Role == "radio").ToList();
            Assert.Equal(["a. опричники", "b. тысяцкие", "c. рекруты", "d. баскаки"], radios.Select(radio => radio.Name));
            Assert.All(radios, radio => Assert.Equal("unchecked", radio.Value));
            Assert.DoesNotContain(page.Elements, element =>
                element.Name.Contains("Очистить", StringComparison.Ordinal) || element.Name.Contains("Спрятанная", StringComparison.Ordinal));
            Assert.Contains(page.Elements, element => element.Role == "submit" && element.Name == "Следующая страница");

            Assert.True(await browser.ClickAsync(radios[3].Id, timeout.Token));
            var answered = await browser.ReadAsync(2000, 50, timeout.Token);
            Assert.Equal("checked", answered.Elements.Single(element => element.Name == "d. баскаки").Value);

            var remember = answered.Elements.Single(element => element.Role == "checkbox");
            Assert.Equal("Запомнить меня", remember.Name);
            Assert.True(await browser.ClickAsync(remember.Id, timeout.Token));
            Assert.Equal("checked", (await browser.FindAsync(remember.Id, timeout.Token))?.Value);
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
