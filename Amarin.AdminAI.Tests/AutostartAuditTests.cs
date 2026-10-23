using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Аудит автозапуска (1.37.0): какой файл на самом деле запускает команда, что считается риском и
/// как читаются расширения браузеров. Подпись и права подменяются — тест не зависит от машины.
/// </summary>
public sealed class AutostartAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "amarin-autostart-" + Guid.NewGuid().ToString("N"));

    public AutostartAuditTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Временная папка, уборка по возможности.
        }
    }

    private static readonly HashSet<string> Disk = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Program Files\Vendor App\app.exe",
        @"C:\Users\me\AppData\Roaming\evil.dll",
        @"C:\Users\me\script.ps1",
        Path.Combine(Environment.SystemDirectory, "svchost.exe"),
        Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
    };

    private static string? Resolve(string command) => CommandLinePath.Resolve(command, Disk.Contains);

    [Theory]
    [InlineData("\"C:\\Program Files\\Vendor App\\app.exe\" --tray", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("C:\\Program Files\\Vendor App\\app.exe --tray", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("C:\\Program Files\\Vendor App\\app --tray", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("\\??\\C:\\Program Files\\Vendor App\\app.exe", @"C:\Program Files\Vendor App\app.exe")]
    [InlineData("rundll32.exe C:\\Users\\me\\AppData\\Roaming\\evil.dll,Start", @"C:\Users\me\AppData\Roaming\evil.dll")]
    [InlineData("\"C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -ExecutionPolicy Bypass -File \"C:\\Users\\me\\script.ps1\"", @"C:\Users\me\script.ps1")]
    public void The_file_behind_a_command_line_is_found(string command, string expected) =>
        Assert.Equal(expected, Resolve(command), ignoreCase: true);

    [Fact]
    public void Bare_names_and_system_root_paths_are_found_in_windows()
    {
        var svchost = Path.Combine(Environment.SystemDirectory, "svchost.exe");
        Assert.Equal(svchost, Resolve("svchost.exe -k netsvcs"), ignoreCase: true);
        Assert.Equal(svchost, Resolve(@"%SystemRoot%\System32\svchost.exe -k LocalService"), ignoreCase: true);
        Assert.Equal(svchost, Resolve(@"\SystemRoot\System32\svchost.exe"), ignoreCase: true);
    }

    [Fact]
    public void A_missing_file_still_names_where_it_should_be()
    {
        Assert.Equal(@"C:\Gone\tool.exe", Resolve("\"C:\\Gone\\tool.exe\" /run"));
        Assert.Null(Resolve("   "));
    }

    [Fact]
    public void Risks_are_unsigned_files_user_writable_places_and_missing_files_only()
    {
        var signed = Path.Combine(_root, "signed.exe");
        var unsigned = Path.Combine(_root, "unsigned.exe");
        var writable = Path.Combine(_root, "writable.exe");
        foreach (var file in new[] { signed, unsigned, writable })
        {
            File.WriteAllText(file, "x");
        }

        var audit = new AutostartAudit(
            path => path == unsigned ? SignatureInfo.None : new SignatureInfo(true, true, "CN=Vendor, O=Vendor"),
            path => path == writable);
        var items = new[]
        {
            new AutostartItem(AutostartSource.Service, "Good", $"\"{signed}\" -k"),
            new AutostartItem(AutostartSource.Run, "NoSign", $"\"{unsigned}\""),
            new AutostartItem(AutostartSource.Task, @"\Writable", $"\"{writable}\""),
            new AutostartItem(AutostartSource.Folder, "Gone", $"\"{Path.Combine(_root, "gone.exe")}\""),
            new AutostartItem(AutostartSource.Extension, "Chrome / Default / x", null, "not from the store")
        };

        var risks = audit.Evaluate(items, CancellationToken.None);

        Assert.Equal(["NoSign", @"\Writable", "Gone", "Chrome / Default / x"], risks.Select(risk => risk.Item.Name));
        Assert.Equal("unsigned", risks[0].Why);
        Assert.Equal("user-writable location", risks[1].Why);
        Assert.Equal("file not found", risks[2].Why);

        var report = AutostartAudit.Format(items.Length, risks, ["WMI subscriptions (administrator rights needed)"]);
        Assert.StartsWith("Autostart audit: 5 items checked, 4 risks.", report, StringComparison.Ordinal);
        Assert.Contains("scheduled task | \\Writable | " + writable + " | user-writable location | Vendor", report, StringComparison.Ordinal);
        Assert.Contains("Not checked: WMI subscriptions (administrator rights needed)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Good", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Chromium_extensions_not_from_the_store_are_risks_and_built_in_ones_are_skipped()
    {
        var profile = Path.Combine(_root, "Chrome", "User Data", "Default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "Secure Preferences"), """
            {"extensions":{"settings":{
              "store":{"location":1,"from_webstore":true,"manifest":{"name":"Store One"}},
              "unpacked":{"location":4,"manifest":{"name":"Dev Thing"}},
              "sideload":{"location":1,"from_webstore":false,"manifest":{"name":"__MSG_appName__"}},
              "builtin":{"location":5,"manifest":{"name":"Component"}},
              "policy":{"location":10,"manifest":{"name":"Forced"}}
            }}}
            """);

        var items = AutostartAudit.ChromiumExtensions("Chrome", profile);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, item => item.Name == "Chrome / Default / Dev Thing (unpacked)" && item.Risk == "unpacked, not from the store");
        Assert.Contains(items, item => item.Name == "Chrome / Default / sideload" && item.Risk == "not from the store");
    }

    [Fact]
    public void Firefox_extensions_without_a_mozilla_signature_are_risks()
    {
        var profile = Path.Combine(_root, "abc.default");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "extensions.json"), """
            {"addons":[
              {"id":"good@x","type":"extension","location":"app-profile","signedState":2,"defaultLocale":{"name":"Good"}},
              {"id":"bad@x","type":"extension","location":"app-profile","signedState":0,"defaultLocale":{"name":"Bad"}},
              {"id":"sys@x","type":"extension","location":"app-system-defaults","signedState":0},
              {"id":"theme@x","type":"theme","location":"app-profile","signedState":0}
            ]}
            """);

        var item = Assert.Single(AutostartAudit.FirefoxExtensions(profile));
        Assert.Equal("Firefox / abc.default / Bad (bad@x)", item.Name);
        Assert.Equal("not signed by Mozilla", item.Risk);
    }

    [Fact]
    public void A_temp_folder_is_user_writable_and_system32_is_not()
    {
        var file = Path.Combine(_root, "probe.exe");
        File.WriteAllText(file, "x");

        Assert.True(AutostartAudit.UserWritable(file));
        Assert.False(AutostartAudit.UserWritable(Path.Combine(Environment.SystemDirectory, "notepad.exe")));
    }

    [Fact]
    public void Windows_files_count_as_signed_without_going_online()
    {
        // svchost подписан каталогом — без проверки каталога он выглядел бы неподписанным.
        var svchost = AuthenticodeCheck.InspectLocal(Path.Combine(Environment.SystemDirectory, "svchost.exe"));
        Assert.True(svchost.Signed);
        Assert.True(svchost.Valid);

        var plain = Path.Combine(_root, "plain.exe");
        File.WriteAllText(plain, "not a program");
        Assert.False(AuthenticodeCheck.InspectLocal(plain).Signed);
    }
}
