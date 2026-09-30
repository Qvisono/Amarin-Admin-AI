using Amarin.UI;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// Перезапуск от администратора (C9): преемник ждёт прежний процесс, открывает тот же чат и
/// получает набранный текст файлом, а не строкой, которую видят все процессы машины.
/// </summary>
public sealed class ElevationRestartTests
{
    [Fact]
    public void The_successor_waits_opens_the_chat_and_reads_the_draft_from_a_file()
    {
        var arguments = MainWindow.ElevationArguments(4242, "3f2a9c", "C:\\Temp\\draft.txt");

        Assert.Equal(["--await-exit", "4242", "--open-chat", "3f2a9c", "--prompt-file", "C:\\Temp\\draft.txt"], arguments);
    }

    [Fact]
    public void An_empty_chat_and_an_empty_input_add_nothing()
    {
        Assert.Equal(["--await-exit", "7"], MainWindow.ElevationArguments(7, null, null));
        Assert.Null(MainWindow.WriteDraftFile("   "));
    }

    [Fact]
    public void The_draft_file_holds_exactly_what_was_typed_and_is_read_once()
    {
        var path = MainWindow.WriteDraftFile("проверь диск C:");
        Assert.NotNull(path);

        var parsed = StartupArgs.Parse(["--prompt-file", path]);

        Assert.Equal("проверь диск C:", parsed.Prompt);
        Assert.False(File.Exists(path), "файл черновика остался во временной папке");
        Assert.False(parsed.ShouldSend);
    }

    [Theory]
    [InlineData("3f2a9c1b-77", "3f2a9c1b-77")]
    [InlineData("..\\..\\settings", null)]
    [InlineData("C:\\Users\\x\\chat.json", null)]
    [InlineData("", null)]
    public void Only_a_chat_id_opens_a_chat(string given, string? expected) =>
        Assert.Equal(expected, StartupArgs.Parse(["--open-chat", given]).OpenChatId);

    [Fact]
    public void A_refusal_in_the_uac_dialog_is_error_1223() =>
        Assert.Equal(1223, MainWindow.UacCancelled);
}
