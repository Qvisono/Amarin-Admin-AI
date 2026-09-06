using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// The model is handed its own generated picture as a vision turn so it can describe it. It also
/// uses that look to judge its own work, and calls generate_image again with a reworded version
/// of the same prompt — then embeds one of the two handles while the user pays for both.
/// </summary>
public sealed class ImageRedrawGuardTests
{
    private const string Pixel = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

    private static ToolCallRecord Drawn(string prompt, string handle) => new()
    {
        Name = "generate_image",
        ArgumentsJson = JsonSerializer.Serialize(new { prompt }),
        Success = true,
        Status = ToolCallStatus.Done,
        Images = { new ImageAttachment(Pixel, "image/png", handle) }
    };

    private static ToolCallRecord Asking(string prompt) => new()
    {
        Name = "generate_image",
        ArgumentsJson = JsonSerializer.Serialize(new { prompt })
    };

    /// <summary>Puts the finished calls in an earlier round and the new one in the current round.</summary>
    private static string? Judge(ToolCallRecord asking, params ToolCallRecord[] earlier)
    {
        var assistant = new ChatDisplayMessage { Role = "assistant", Id = "m1" };
        var past = new ToolRound();
        foreach (var call in earlier)
        {
            past.Calls.Add(call);
        }

        var current = new ToolRound();
        current.Calls.Add(asking);
        assistant.ToolRounds.Add(past);
        assistant.ToolRounds.Add(current);

        return ChatEngine.RedrawOf(assistant, current, asking);
    }

    [Fact]
    public void A_reworded_retry_of_the_same_picture_is_refused()
    {
        // The two prompts the model actually sent when asked once for a photo of space.
        var handle = Judge(
            Asking("Photorealistic deep space photograph, dense starfield, magenta purple teal nebula, distant galaxies"),
            Drawn(
                "Photorealistic photograph of deep outer space, vast starfield with countless stars, colourful nebula, distant galaxies",
                "amarin-image:11112222"));

        Assert.Equal("amarin-image:11112222", handle);
    }

    [Fact]
    public void A_genuinely_different_picture_goes_through()
    {
        Assert.Null(Judge(
            Asking("A fluffy orange cat sleeping on a windowsill, watercolour"),
            Drawn("A golden retriever puppy running across a snowy field, oil painting", "amarin-image:11112222")));
    }

    [Fact]
    public void The_very_first_picture_of_a_turn_is_never_refused() =>
        Assert.Null(Judge(Asking("Photorealistic photograph of deep outer space, vast starfield")));

    [Fact]
    public void A_sibling_running_in_the_same_round_is_not_a_duplicate()
    {
        // Calls inside one round execute in parallel, so a sibling has produced nothing yet and
        // cannot be the picture to reuse.
        var asking = Asking("Photorealistic photograph of deep outer space, vast starfield");
        var assistant = new ChatDisplayMessage { Role = "assistant", Id = "m1" };
        var round = new ToolRound();
        round.Calls.Add(Drawn("Photorealistic photograph of deep outer space, vast starfield", "amarin-image:1"));
        round.Calls.Add(asking);
        assistant.ToolRounds.Add(round);

        Assert.Null(ChatEngine.RedrawOf(assistant, round, asking));
    }

    [Fact]
    public void A_repeat_after_a_failed_attempt_is_allowed()
    {
        var failed = Drawn("Photorealistic photograph of deep outer space, vast starfield", "amarin-image:1");
        failed.Success = false;
        failed.Images.Clear();

        Assert.Null(Judge(
            Asking("Photorealistic photograph of deep outer space, vast starfield"),
            failed));
    }

    [Fact]
    public void Other_tools_are_never_touched()
    {
        var asking = new ToolCallRecord
        {
            Name = "fetch_image",
            ArgumentsJson = JsonSerializer.Serialize(new { url = "https://example.com/a.png" })
        };

        Assert.Null(Judge(asking, Drawn("anything at all here", "amarin-image:1")));
    }
}
