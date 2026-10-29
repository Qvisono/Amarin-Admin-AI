using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>Окно браузера агента (1.37.1): ожидание человека кончается вместе с окном.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class AgentBrowserWindowTests
{
    private readonly WpfFixture _wpf;

    public AgentBrowserWindowTests(WpfFixture wpf) => _wpf = wpf;

    /// <summary>
    /// До 1.37.1 закрытое посреди handoff окно оставляло агента ждать «Готово» до срока, десять
    /// минут. Окно здесь не показывается: просьба вывела бы его вперёд и забрала фокус у человека.
    /// </summary>
    [Fact]
    public void Closing_the_window_answers_the_waiting_handoff_at_once() => _wpf.Ui.Invoke(() =>
    {
        var window = new AgentBrowserWindow();
        var answer = window.AskHuman("Sign in to the site, then press Done.");
        Assert.False(answer.IsCompleted);

        window.Close();

        Assert.True(answer.IsCompleted);
        Assert.False(answer.Result);
        return true;
    });
}
