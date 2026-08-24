using Amarin.UI;

namespace Amarin.AdminAI.Tests;

public sealed class WpfUiTests
{
    [Fact]
    public void Start_shows_empty_main_window()
    {
        using var ui = WpfUi.Start();
        Assert.Equal($"Amarin Admin AI v{RuntimeContext.AppVersion}", ui.MainWindowTitle);
    }
}
