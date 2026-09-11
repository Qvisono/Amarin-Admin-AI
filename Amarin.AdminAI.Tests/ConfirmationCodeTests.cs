using System.Text.Json;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The dialog is the last place a person can still say no, so what it shows has to be the whole of
/// what will happen. Before these, a .bat could be approved with only its path on screen, and
/// write_file skipped the question altogether.
/// </summary>
public sealed class ConfirmationCodeTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void A_powershell_command_reaches_the_dialog_whole()
    {
        // Comfortably past the 120 characters the summary line keeps and the 200 the details block
        // keeps: those truncate on purpose, and this must not.
        var command = "Stop-Service wuauserv; " + new string('x', 400);
        var info = DangerousActionGuard.DescribeDetailed(
            "run_powershell",
            Args(JsonSerializer.Serialize(new { command })));

        Assert.Equal(command, info.CodeText);
        Assert.Equal("powershell", info.CodeLanguage);
        Assert.True(info.ChangeSummary.Length < command.Length);
    }

    [Fact]
    public void The_contents_of_a_written_file_are_shown_not_just_its_path()
    {
        var content = "@echo off\r\npython -c \"import torch\"\r\npause";
        var info = DangerousActionGuard.DescribeDetailed(
            "filesystem",
            Args(JsonSerializer.Serialize(new { action = "write", path = @"D:\tmp\check.bat", content })));

        Assert.Equal(content, info.CodeText);
        Assert.Equal("bat", info.CodeLanguage);
    }

    [Theory]
    [InlineData(@"C:\a\b.ps1", "powershell")]
    [InlineData(@"C:\a\b.cmd", "bat")]
    [InlineData(@"C:\a\b.py", "python")]
    [InlineData(@"C:\a\b.json", "json")]
    [InlineData(@"C:\a\b.weird", "")]
    [InlineData(@"C:\a\noextension", "")]
    public void The_language_comes_from_the_extension(string path, string expected)
    {
        var info = DangerousActionGuard.DescribeDetailed(
            "write_file",
            Args(JsonSerializer.Serialize(new { path, content = "x" })));

        Assert.Equal(expected, info.CodeLanguage);
    }

    [Fact]
    public void Registry_data_counts_as_code_too()
    {
        var info = DangerousActionGuard.DescribeDetailed(
            "registry",
            Args("""{"action":"write","path":"HKLM\\SOFTWARE\\X","value_data":"1"}"""));

        Assert.Equal("1", info.CodeText);
    }

    [Fact]
    public void A_call_with_nothing_to_run_carries_no_code()
    {
        var info = DangerousActionGuard.DescribeDetailed(
            "windows_service",
            Args("""{"action":"stop","service_name":"Spooler"}"""));

        Assert.Equal("", info.CodeText);
        Assert.Equal("", info.CodeLanguage);
    }

    [Fact]
    public void Write_file_no_longer_slips_past_the_question()
    {
        // It wraps its arguments into a filesystem/write call and executes that directly, so
        // guarding only "filesystem" let every file it wrote through unasked.
        var arguments = Args("""{"path":"C:\\tmp\\a.bat","content":"echo hi"}""");

        Assert.True(DangerousActionGuard.RequiresConfirmation("write_file", arguments));

        // And it is on the list that makes the model send an explanation with the call: the
        // property is only grafted onto schemas of tools that can end up asking.
        var schema = ToolRegistry.EnrichSchemaWithExplanation(
            "write_file", Args("""{"type":"object","properties":{"path":{"type":"string"}}}"""));
        Assert.True(schema.GetProperty("properties").TryGetProperty("explanation", out _));
    }

    [Fact]
    public void Write_file_is_described_as_a_file_write_rather_than_by_its_tool_name()
    {
        var info = DangerousActionGuard.DescribeDetailed(
            "write_file",
            Args("""{"path":"C:\\tmp\\a.bat","content":"echo hi"}"""));

        Assert.Contains("Запись в файл", info.ChangeSummary, StringComparison.Ordinal);
        Assert.Contains("a.bat", info.ChangeSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_models_explanation_still_comes_through()
    {
        var info = DangerousActionGuard.DescribeDetailed(
            "run_powershell",
            Args("""{"command":"Stop-Service wuauserv","explanation":"Останавливаю центр обновления."}"""));

        Assert.Equal("Останавливаю центр обновления.", info.Explanation);
    }
}
