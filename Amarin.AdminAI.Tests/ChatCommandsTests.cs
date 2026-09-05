using Amarin.Core;

namespace Amarin.AdminAI.Tests;

public sealed class ChatCommandsTests
{
    [Theory]
    [InlineData("/agent перезапусти службу спулера", "перезапусти службу спулера", "heavy")]
    [InlineData("  /agent   собери логи  ", "собери логи", "heavy")]
    [InlineData("/AGENT собери логи", "собери логи", "heavy")]
    [InlineData("/agent lite собери логи", "собери логи", "lite")]
    [InlineData("/agent LITE собери логи", "собери логи", "lite")]
    [InlineData("/agent heavy собери логи", "собери логи", "heavy")]
    [InlineData("/agent-lite собери логи", "собери логи", "lite")]
    [InlineData("/agent-heavy собери логи", "собери логи", "heavy")]
    public void Agent_command_is_parsed(string input, string prompt, string complexity)
    {
        var command = ChatCommands.TryParse(input);

        Assert.NotNull(command);
        Assert.Equal(ChatCommands.Agent, command!.Value.Name);
        Assert.Equal(prompt, command.Value.Argument);
        Assert.Equal(complexity, command.Value.Complexity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/")]
    [InlineData("/agent")]              // no prompt — nothing to run
    [InlineData("/agent   ")]
    [InlineData("/agentx сделай что-то")]  // near-miss verb
    [InlineData("/clear")]
    [InlineData("расскажи про /agent")]    // command not at the start
    [InlineData("C:/agent/config.json")]
    public void Non_commands_stay_plain_text(string input)
    {
        Assert.Null(ChatCommands.TryParse(input));
    }

    [Fact]
    public void Bare_lite_is_the_prompt_not_a_modifier()
    {
        // "/agent lite" with nothing after it means the user wants the agent to work on "lite".
        var command = ChatCommands.TryParse("/agent lite");

        Assert.NotNull(command);
        Assert.Equal("lite", command!.Value.Argument);
        Assert.Equal("heavy", command.Value.Complexity);
    }

    [Fact]
    public void Multiline_prompt_is_kept_whole()
    {
        var command = ChatCommands.TryParse("/agent проверь диск C\nи собери отчёт");

        Assert.NotNull(command);
        Assert.Equal("проверь диск C\nи собери отчёт", command!.Value.Argument);
    }
}
