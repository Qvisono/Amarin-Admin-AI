using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Пути файловых инструментов чата и шлюз: шлюз проверяет тот же путь, по которому запишет
/// инструмент, — а не сырую строку модели.
/// </summary>
/// <remarks>
/// До 1.33.0 относительный путь шлюз видел голым именем, а инструмент раскрывал от «Загрузок»:
/// новый файл в «Загрузках» спрашивался, а <c>..\..\AppData\Roaming\Amarin Admin AI\settings.json</c>
/// проходил мимо запрета на данные программы.
/// </remarks>
[Collection(SafeZoneCollection.Name)]
public sealed class FileToolGateTests : IDisposable
{
    private readonly Func<IReadOnlyList<string>> _roots = SafeZone.Roots;

    public FileToolGateTests() => SafeZone.Roots = () => [DownloadPaths.DownloadsDirectory, DownloadPaths.DesktopDirectory];

    public void Dispose() => SafeZone.Roots = _roots;

    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);

    private static string NewName(string extension) => "amarin-gate-test-" + Guid.NewGuid().ToString("N") + extension;

    [Fact]
    public void A_relative_path_is_checked_and_written_as_the_full_one_in_Downloads()
    {
        var name = NewName(".txt");

        var check = ToolGate.Check("write_file", Args(new { path = name, content = "x" }), new AppSettings());

        Assert.Null(check.Refusal);
        Assert.Null(check.Question);
        Assert.Equal(Path.Combine(DownloadPaths.DownloadsDirectory, name), check.Arguments.GetProperty("path").GetString());
        Assert.True(check.Arguments.GetProperty(SafeZone.CreateNewFlag).GetBoolean());
    }

    [Theory]
    [InlineData("create_document", ".docx")]
    [InlineData("save_image", ".png")]
    [InlineData("create_folder", "")]
    public void A_new_file_or_folder_in_Downloads_is_made_without_a_question(string tool, string extension)
    {
        var check = ToolGate.Check(tool, Args(new { path = NewName(extension), image = "amarin-image:00000000" }), new AppSettings());

        Assert.Null(check.Refusal);
        Assert.Null(check.Question);
        Assert.True(check.Arguments.GetProperty(SafeZone.CreateNewFlag).GetBoolean());
    }

    [Fact]
    public void Desktop_and_Documents_lead_to_those_folders()
    {
        Assert.True(FileToolPaths.TryResolve(@"Desktop\план.docx", out var desktop, out _));
        Assert.Equal(Path.Combine(DownloadPaths.DesktopDirectory, "план.docx"), desktop);

        Assert.True(FileToolPaths.TryResolve("Документы/смета.xlsx", out var documents, out _));
        Assert.Equal(Path.Combine(DownloadPaths.DocumentsDirectory, "смета.xlsx"), documents);

        Assert.True(FileToolPaths.TryResolve("отчёт.pdf", out var bare, out _));
        Assert.Equal(Path.Combine(DownloadPaths.DownloadsDirectory, "отчёт.pdf"), bare);
    }

    [Fact]
    public void Editing_a_file_in_place_is_always_asked_even_in_Downloads()
    {
        var path = Path.Combine(DownloadPaths.DownloadsDirectory, NewName(".txt"));

        var check = ToolGate.Check("edit_file", Args(new { path, old_string = "a", new_string = "b" }), new AppSettings());

        Assert.Null(check.Refusal);
        Assert.NotNull(check.Question);
    }

    [Theory]
    [InlineData("write_file", "path")]
    [InlineData("edit_file", "path")]
    [InlineData("create_folder", "path")]
    [InlineData("create_document", "path")]
    [InlineData("save_image", "path")]
    [InlineData("edit_document", "path")]
    [InlineData("edit_document", "save_as")]
    public void Nothing_is_written_into_the_program_data_folder(string tool, string field)
    {
        var target = Path.Combine(SensitivePaths.ProgramDataRoot(), "settings.json");
        var outside = Path.Combine(DownloadPaths.DownloadsDirectory, NewName(".docx"));
        var arguments = new Dictionary<string, object?>
        {
            ["path"] = field == "path" ? target : outside,
            ["content"] = "x",
            ["old_string"] = "a",
            ["new_string"] = "b",
            ["image"] = "amarin-image:00000000",
            ["operations"] = Array.Empty<object>()
        };
        if (field == "save_as")
        {
            arguments["save_as"] = target;
        }

        var check = ToolGate.Check(tool, Args(arguments), new AppSettings { ApprovalMode = ApprovalMode.AlwaysApprove });

        Assert.NotNull(check.Refusal);
    }

    [Fact]
    public void A_relative_path_that_climbs_into_program_data_is_refused()
    {
        var target = Path.Combine(SensitivePaths.ProgramDataRoot(), "settings.json");
        var climb = Path.GetRelativePath(DownloadPaths.DownloadsDirectory, target);
        Assert.StartsWith("..", climb, StringComparison.Ordinal);

        var check = ToolGate.Check("edit_file", Args(new { path = climb, old_string = "a", new_string = "b" }), new AppSettings());

        Assert.NotNull(check.Refusal);
    }

    [Fact]
    public void An_attachment_handle_is_not_turned_into_a_path()
    {
        const string handle = "amarin-attachment:0123456789ab/отчёт.docx";

        var check = ToolGate.Check("read_file", Args(new { path = handle }), new AppSettings());

        Assert.Null(check.Refusal);
        Assert.Equal(handle, check.Arguments.GetProperty("path").GetString());
    }

    [Fact]
    public void Other_tools_keep_their_arguments_as_given()
    {
        var arguments = Args(new { action = "read", path = "relative.txt" });

        Assert.Equal(arguments.GetRawText(), FileToolPaths.CanonicalArguments("filesystem", arguments).GetRawText());
    }
}
