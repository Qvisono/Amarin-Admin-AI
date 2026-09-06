using System.Text.Json;
using Amarin.Core;

namespace Amarin.AdminAI.Tests;

/// <summary>
/// A drawn picture costs cents while the conversation around it costs hundredths of one, and
/// until this was priced the message header quietly showed only the text. These cover both ends:
/// the number Venice reports being read at all, and it reaching the row the user looks at.
/// </summary>
public sealed class ImageCostTests
{
    [Fact]
    public void The_cost_venice_reports_on_a_generated_image_is_read()
    {
        // The field was simply not modelled, so System.Text.Json threw the price away in silence.
        var response = JsonSerializer.Deserialize(
            """{"id":"x","images":["AAA"],"cost":{"usd":0.1234,"diem":0}}""",
            VeniceJsonContext.Default.ImageGenerateResponse);

        Assert.NotNull(response?.Cost);
        Assert.Equal(0.1234m, response!.Cost!.ToCost().Usd);
        Assert.True(response.Cost.ToCost().HasData);
    }

    [Fact]
    public void An_image_response_without_a_cost_still_parses()
    {
        var response = JsonSerializer.Deserialize(
            """{"id":"x","images":["AAA"]}""",
            VeniceJsonContext.Default.ImageGenerateResponse);

        Assert.NotNull(response);
        Assert.Null(response!.Cost);
    }

    [Fact]
    public void A_charge_lands_on_the_tool_call_that_caused_it()
    {
        var call = new ToolCallRecord { Name = "generate_image" };
        using (AgentRunScope.Push(new AgentRunContext
        {
            Call = call,
            Assistant = new ChatDisplayMessage { Role = "assistant", Id = "a" },
            Observer = new SilentObserver()
        }))
        {
            AgentRunScope.Charge(new VeniceCost { Usd = 0.10m, HasData = true });
            AgentRunScope.Charge(new VeniceCost { Usd = 0.02m, HasData = true });
        }

        Assert.NotNull(call.Cost);
        Assert.Equal(0.12m, call.Cost!.Usd);
        Assert.Equal("$0,12", ChatFormat.Cost(call.Cost));
    }

    [Fact]
    public void Charging_outside_a_tool_call_is_a_no_op()
    {
        // The engine's own chat completions run with no scope pushed; they must not be blamed
        // on whichever tool happened to run last.
        AgentRunScope.Charge(new VeniceCost { Usd = 1m, HasData = true });
        Assert.Null(AgentRunScope.Current);
    }

    [Fact]
    public void A_call_without_a_price_shows_nothing()
    {
        Assert.Equal("", ChatFormat.Cost(new ToolCallRecord().Cost));
        Assert.Equal("", ChatFormat.Cost(new VeniceCost()));
    }

    [Fact]
    public void The_price_of_a_call_survives_a_round_trip_through_storage()
    {
        var root = Path.Combine(Path.GetTempPath(), "amarin-cost-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChatStore(root);
            var session = new ChatSession { Id = "s1", Title = "проба" };
            var assistant = new ChatDisplayMessage { Role = "assistant", Id = "m1" };
            assistant.ToolRounds.Add(new ToolRound
            {
                Calls =
                {
                    new ToolCallRecord
                    {
                        Name = "generate_image",
                        Cost = new VeniceCost { Usd = 0.1m, HasData = true }
                    }
                }
            });
            session.Messages.Add(assistant);
            store.Save(session);

            var loaded = store.TryLoad(session.Id);
            var call = loaded!.Messages[0].ToolRounds[0].Calls[0];

            Assert.NotNull(call.Cost);
            Assert.Equal(0.1m, call.Cost!.Usd);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class SilentObserver : IChatTurnObserver
    {
        public void OnUserAppended(ChatDisplayMessage user) { }

        public void OnAssistantStarted(ChatDisplayMessage assistant) { }

        public void OnAssistantText(ChatDisplayMessage assistant) { }

        public void OnToolsChanged(ChatDisplayMessage assistant) { }

        public void OnAssistantCompleted(ChatDisplayMessage assistant) { }

        public void OnAssistantCancelled(ChatDisplayMessage assistant) { }

        public void OnError(string message) { }
    }
}
