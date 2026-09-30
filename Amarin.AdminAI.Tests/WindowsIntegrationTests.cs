using System.Windows.Media;
using System.Windows.Media.Imaging;
using Amarin.Core;
using Amarin.UI;
using Microsoft.Win32;

namespace Amarin.AdminAI.Tests;

/// <summary>Интеграция с Windows (G): аргументы, передача второму запуску, сочетания, реестр.</summary>
public sealed class WindowsIntegrationTests
{
    [Fact]
    public void Jump_list_and_explorer_arguments_become_actions()
    {
        Assert.Equal(StartupAction.NewChat, StartupArgs.Parse(["--new-chat"]).Action);
        Assert.Equal(StartupAction.Health, StartupArgs.Parse(["--health"]).Action);
        Assert.Equal(StartupAction.Tray, StartupArgs.Parse(["--tray"]).Action);

        var path = OperatingSystem.IsWindows() ? @"C:\Users\me\report.txt" : "/home/me/report.txt";
        var ask = StartupArgs.Parse(["--ask-path", path]);
        Assert.Equal(StartupAction.AskPath, ask.Action);
        Assert.Equal(path, ask.AskPath);

        // Относительный путь и строка с переводом строки — не путь из Проводника.
        Assert.Equal(StartupAction.None, StartupArgs.Parse(["--ask-path", "report.txt"]).Action);
        Assert.Equal(StartupAction.None, StartupArgs.Parse(["--ask-path", path + "\nrm -rf"]).Action);
    }

    [Fact]
    public void A_second_launch_hands_over_its_action_even_without_text()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            SingleInstanceHandoff.Write(root, null, action: StartupAction.Health);
            SingleInstanceHandoff.Write(root, "text", chatId: "abc123");
            SingleInstanceHandoff.Write(root, null);

            var taken = SingleInstanceHandoff.TryTakeAll(root);

            Assert.Equal(2, taken.Count);
            Assert.Equal("abc123", HandoffRequest.LatestAction(taken)!.ChatId);
            Assert.Equal("text", HandoffRequest.Latest(taken)!.Prompt);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Handoff_requests_are_taken_in_the_order_they_were_written()
    {
        // Имена файлов были случайными GUID, а порядок — порядком имён: «последним» оказывался
        // любой из запросов, и предыдущий тест проходил через раз.
        var root = Path.Combine(Path.GetTempPath(), "amarin-handoff-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (var i = 0; i < 40; i++)
            {
                SingleInstanceHandoff.Write(root, "p" + i, chatId: "c" + i);
            }

            var taken = SingleInstanceHandoff.TryTakeAll(root);

            Assert.Equal(Enumerable.Range(0, 40).Select(i => "c" + i), taken.Select(item => item.ChatId));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("Win+Shift+A", GlobalHotkeys.ModWin | GlobalHotkeys.ModShift, 'A')]
    [InlineData("ctrl + alt + F5", GlobalHotkeys.ModControl | GlobalHotkeys.ModAlt, 0x74)]
    [InlineData("Ctrl+Space", GlobalHotkeys.ModControl, 0x20)]
    [InlineData("Alt+7", GlobalHotkeys.ModAlt, '7')]
    public void A_global_shortcut_is_parsed_for_RegisterHotKey(string gesture, uint modifiers, int key)
    {
        Assert.True(GlobalHotkeys.TryParse(gesture, out var parsedModifiers, out var parsedKey));
        Assert.Equal(modifiers, parsedModifiers);
        Assert.Equal((uint)key, parsedKey);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Hyper+A")]
    [InlineData("Ctrl+Enter")]
    [InlineData("")]
    public void A_bare_key_or_unknown_part_is_not_a_global_shortcut(string gesture) =>
        Assert.False(GlobalHotkeys.TryParse(gesture, out _, out _));

    [Fact]
    public void Only_show_hide_has_a_factory_shortcut_and_an_empty_field_turns_it_off()
    {
        var settings = new WindowsIntegrationSettings();
        Assert.Equal("Win+Shift+A", GlobalHotkeys.Effective(settings, GlobalHotkeys.ShowHide));
        Assert.Null(GlobalHotkeys.Effective(settings, GlobalHotkeys.NewWithScreenshot));

        settings.Hotkeys[GlobalHotkeys.ShowHide] = "";
        Assert.Null(GlobalHotkeys.Effective(settings, GlobalHotkeys.ShowHide));
    }

    [Fact]
    public void Commands_quote_the_program_path()
    {
        Assert.Equal("\"C:\\Apps\\Amarin.exe\" --tray", AutoStart.Command(@"C:\Apps\Amarin.exe"));
        Assert.Equal("\"C:\\Apps\\Amarin.exe\" --ask-path \"%1\"", ExplorerMenu.Command(@"C:\Apps\Amarin.exe", "%1"));
    }

    /// <summary>
    /// Запись в реестр — в отдельном тестовом ключе, а не в настоящем автозапуске: тест не должен
    /// прописывать себя человеку в Windows.
    /// </summary>
    [Fact]
    public void Autostart_and_explorer_entries_follow_the_setting_and_leave_foreign_values_alone()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var name = @"Software\AmarinAdminAITests\" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(name, writable: true);
        try
        {
            Assert.True(AutoStart.Apply(root, enabled: true, @"C:\Apps\Amarin.exe"));
            using (var run = root.OpenSubKey(AutoStart.RunKey)!)
            {
                Assert.Equal("\"C:\\Apps\\Amarin.exe\" --tray", run.GetValue(AutoStart.ValueName));
            }

            // Программу перенесли — путь в записи обновляется.
            Assert.True(AutoStart.Apply(root, enabled: true, @"D:\New\Amarin.exe"));
            using (var run = root.OpenSubKey(AutoStart.RunKey)!)
            {
                Assert.Equal("\"D:\\New\\Amarin.exe\" --tray", run.GetValue(AutoStart.ValueName));
            }

            Assert.True(AutoStart.Apply(root, enabled: false, @"D:\New\Amarin.exe"));
            using (var run = root.OpenSubKey(AutoStart.RunKey)!)
            {
                Assert.Null(run.GetValue(AutoStart.ValueName));
                run.Close();
            }

            // Чужая запись под тем же именем выключением не стирается.
            using (var run = root.CreateSubKey(AutoStart.RunKey, writable: true))
            {
                run.SetValue(AutoStart.ValueName, "\"C:\\Other\\app.exe\"");
            }

            AutoStart.Apply(root, enabled: false, @"D:\New\Amarin.exe");
            using (var run = root.OpenSubKey(AutoStart.RunKey)!)
            {
                Assert.Equal("\"C:\\Other\\app.exe\"", run.GetValue(AutoStart.ValueName));
            }

            Assert.True(ExplorerMenu.Apply(root, enabled: true, @"C:\Apps\Amarin.exe", "Ask Amarin"));
            foreach (var (classPath, placeholder) in ExplorerMenu.Targets)
            {
                using var command = root.OpenSubKey(classPath + @"\command")!;
                Assert.Equal(ExplorerMenu.Command(@"C:\Apps\Amarin.exe", placeholder), command.GetValue(""));
            }

            Assert.True(ExplorerMenu.Apply(root, enabled: false, @"C:\Apps\Amarin.exe", "Ask Amarin"));
            Assert.All(ExplorerMenu.Targets, target => Assert.Null(root.OpenSubKey(target.ClassPath)));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
    }
}

/// <summary>Значок трея рисуется с точкой состояния цветом из темы.</summary>
[Collection(WpfCollection.Name)]
[Trait(WpfCollection.Category, WpfCollection.Trait)]
public sealed class TrayIconRenderTests
{
    private readonly WpfFixture _wpf;

    public TrayIconRenderTests(WpfFixture wpf) => _wpf = wpf;

    [Fact]
    public void Busy_adds_a_dot_in_the_corner_and_idle_does_not()
    {
        var (idle, busy) = _wpf.Ui.Invoke(() =>
        {
            var blank = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
            return (Corner(TrayIcon.Render(blank, TrayState.Idle, 32)), Corner(TrayIcon.Render(blank, TrayState.Busy, 32)));
        });

        Assert.Equal(0, idle);
        Assert.True(busy > 0, "в углу значка нет точки");
    }

    /// <summary>Непрозрачность пикселя у правого нижнего угла, где стоит точка.</summary>
    private static byte Corner(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var x = bitmap.PixelWidth - 7;
        var y = bitmap.PixelHeight - 7;
        return pixels[(y * bitmap.PixelWidth + x) * 4 + 3];
    }
}
