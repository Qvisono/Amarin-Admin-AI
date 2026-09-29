using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Секреты на диске инструменты не читают: пароли и куки браузеров, ключи SSH, хранилища
/// Windows и менеджеров паролей, ключи самой программы, кусты SAM/SECURITY/SYSTEM. А в папку
/// данных программы не пишут.
/// </summary>
[Collection(SensitivePathsCollection.Name)]
public sealed class SensitivePathsTests : IDisposable
{
    private readonly Func<string> _programRoot = SensitivePaths.ProgramDataRoot;
    private readonly Func<string> _snapshots = SensitivePaths.SnapshotsRoot;

    public SensitivePathsTests()
    {
        SensitivePaths.ProgramDataRoot = () => @"C:\Users\Анна\AppData\Roaming\Amarin Admin AI";
        SensitivePaths.SnapshotsRoot = () => @"C:\Users\Анна\AppData\Local\AmarinAdminAI\snapshots";
    }

    public void Dispose()
    {
        SensitivePaths.ProgramDataRoot = _programRoot;
        SensitivePaths.SnapshotsRoot = _snapshots;
    }

    [Theory]
    [InlineData(@"C:\Users\Анна\AppData\Local\Google\Chrome\User Data\Default\Login Data")]
    [InlineData(@"C:\Users\Анна\AppData\Local\Google\Chrome\User Data\Local State")]
    [InlineData(@"C:\Users\Анна\AppData\Local\Microsoft\Edge\User Data\Default\Network\Cookies")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Mozilla\Firefox\Profiles\x.default\logins.json")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Mozilla\Firefox\Profiles\x.default\key4.db")]
    [InlineData(@"C:\Users\Анна\.ssh\id_ed25519")]
    [InlineData(@"C:\Users\Анна\.ssh")]
    [InlineData(@"C:\Users\Анна\.gnupg\private-keys-v1.d\A.key")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Microsoft\Credentials\ABC")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Microsoft\Protect\S-1-5-21\key")]
    [InlineData(@"C:\Users\Анна\AppData\Local\Microsoft\Vault\4BF4C442")]
    [InlineData(@"D:\Документы\пароли.kdbx")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Bitwarden\data.json")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI\keys.json")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI\profiles\p1\keys.json")]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"C:\Windows\System32\config\SECURITY")]
    [InlineData(@"C:\Windows\System32\config\SYSTEM")]
    [InlineData(@"C:\Windows\System32\config\SYSTEM.LOG1")]
    [InlineData(@"C:\Windows\System32\config\RegBack\SAM")]
    [InlineData(@"C:\Windows\repair\SAM")]
    [InlineData(@"C:\Windows\NTDS\ntds.dit")]
    public void A_secret_is_not_read(string path)
    {
        Assert.True(SensitivePaths.IsSensitive(path, out var reason));
        Assert.StartsWith("ЗАПРЕЩЕНО", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\Анна\AppData\Local\Google\Chrome\User Data\Default\Login Data.")]
    [InlineData(@"C:\Users\Анна\AppData\Local\Google\Chrome\User Data\Default\Login Data  ")]
    [InlineData(@"C:\Users\Анна\AppData\Local\Google\Chrome\User Data\Default\Login Data::$DATA")]
    [InlineData(@"\\?\C:\Users\Анна\.ssh\id_rsa")]
    [InlineData(@"C:/Users/Анна/.ssh/id_rsa")]
    [InlineData(@"C:\Users\Анна\Documents\..\.ssh\id_rsa")]
    [InlineData(@"C:\Windows\System32\config\.\SAM")]
    [InlineData("\"C:\\Users\\Анна\\.ssh\\id_rsa\"")]
    public void Windows_spellings_of_the_same_file_do_not_slip_through(string path)
    {
        // Windows отбрасывает хвостовые точки и пробелы и открывает основной поток по «::$DATA»,
        // так что каждое из этих написаний читает тот же секрет.
        Assert.True(SensitivePaths.IsSensitive(path, out _));
    }

    [Theory]
    [InlineData(@"C:\Users\Анна\Documents\отчёт.docx")]
    [InlineData(@"C:\Users\Анна\Downloads\setup.exe")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Windows\System32\config\systemprofile\readme.txt")]
    [InlineData(@"D:\projects\keys.json")]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI\settings.json")]
    public void Ordinary_files_are_read(string path)
    {
        Assert.False(SensitivePaths.IsSensitive(path, out _));
    }

    [Theory]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI\settings.json", true)]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI\instructions\x.md", true)]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI", true)]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI\chats\..\keys.json", true)]
    [InlineData(@"C:\Users\Анна\AppData\Local\AmarinAdminAI\snapshots\20260101_000000\meta.json", true)]
    [InlineData(@"C:\Users\Анна\AppData\Roaming\Amarin Admin AI Backup\x.json", false)]
    [InlineData(@"C:\Users\Анна\Downloads\settings.json", false)]
    public void The_programs_own_data_is_recognised(string path, bool expected)
    {
        Assert.Equal(expected, SensitivePaths.IsProgramData(path));
    }

    [Theory]
    [InlineData("SAM", true)]
    [InlineData(@"SECURITY\Policy\Secrets", true)]
    [InlineData(@"\SAM\Domains", true)]
    [InlineData(@"SOFTWARE\Microsoft", false)]
    [InlineData(@"SYSTEM\CurrentControlSet", false)]
    public void Secret_registry_hives_are_recognised(string subKey, bool expected)
    {
        Assert.Equal(expected, SensitivePaths.IsSecretHive(subKey));
    }

    [Fact]
    public async Task The_filesystem_tool_refuses_to_read_or_copy_a_secret()
    {
        var tool = new FileSystemTool();

        var read = await tool.ExecuteAsync(Args(new
        {
            action = "read",
            path = @"C:\Users\Анна\.ssh\id_rsa"
        }));
        var copy = await tool.ExecuteAsync(Args(new
        {
            action = "copy",
            path = @"C:\Users\Анна\AppData\Local\Google\Chrome\User Data\Default\Login Data",
            destination = @"C:\Users\Анна\Desktop\x"
        }));

        Assert.False(read.Success);
        Assert.StartsWith("ЗАПРЕЩЕНО", read.Output, StringComparison.Ordinal);
        Assert.False(copy.Success);
        Assert.StartsWith("ЗАПРЕЩЕНО", copy.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_filesystem_tool_does_not_write_into_the_programs_data()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-data-" + Guid.NewGuid().ToString("N"));
        SensitivePaths.ProgramDataRoot = () => root;
        var settings = Path.Combine(root, "settings.json");

        var result = await new FileSystemTool().ExecuteAsync(Args(new
        {
            action = "write",
            path = settings,
            content = """{"approvalMode":"alwaysApprove"}"""
        }));

        Assert.False(result.Success);
        Assert.StartsWith("ЗАПРЕЩЕНО", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(settings));
    }

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SensitivePathsCollection
{
    public const string Name = "SensitivePaths";
}
