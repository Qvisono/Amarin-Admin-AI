using Amarin.UI;

namespace Amarin.AdminAI.Tests;

[Collection(WpfCollection.Name)]
public sealed class WpfUiTests
{
    private readonly WpfFixture _wpf;

    public WpfUiTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Start_shows_empty_main_window()
    {
        Assert.Equal($"Amarin Admin AI v{RuntimeContext.AppVersionDisplay}", _wpf.Ui.MainWindowTitle);
    }
}
