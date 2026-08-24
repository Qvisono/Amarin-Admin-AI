using System.Text.Json;
using Amarin.Core;
using Amarin.Tools;

namespace Amarin.AdminAI.Tests;

public sealed class VeniceJsonContextTests
{
    [Fact]
    public void Chat_completion_request_roundtrips_through_source_gen()
    {
        var request = new ChatCompletionRequest
        {
            Model = "grok-4-6",
            Messages =
            [
                new ChatMessage { Role = "user", Content = ChatContent.Text("hello") }
            ],
            Tools =
            [
                new ToolDefinition
                {
                    Function = new FunctionDefinition
                    {
                        Name = "system_info",
                        Description = "info",
                        Parameters = JsonSchema.Parse("""{"type":"object"}""")
                    }
                }
            ],
            ToolChoice = "auto",
            VeniceParameters = new VeniceParameters
            {
                IncludeVeniceSystemPrompt = false,
                EnableWebSearch = "off",
                DisableThinking = true
            }
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, VeniceJsonContext.Default.ChatCompletionRequest);
        var json = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.Contains("\"model\":\"grok-4-6\"", json, StringComparison.Ordinal);
        Assert.Contains("\"venice_parameters\"", json, StringComparison.Ordinal);
        Assert.Contains("system_info", json, StringComparison.Ordinal);

        var responseJson = """
            {"choices":[{"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"cost":{"usd":0.01,"diem":0}}
            """;
        var response = JsonSerializer.Deserialize(responseJson, VeniceJsonContext.Default.ChatCompletionResponse);
        Assert.NotNull(response);
        Assert.Single(response.Choices);
        Assert.Equal("ok", ChatContent.ReadText(response.Choices[0].Message.Content));
        Assert.Equal(0.01m, response.Cost?.Usd);
    }

    [Fact]
    public void Tool_call_arguments_object_is_read_as_json_string()
    {
        var json = """
            {"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"system_info","arguments":{"scope":"summary"}}}]},"finish_reason":"tool_calls"}]}
            """;
        var response = JsonSerializer.Deserialize(json, VeniceJsonContext.Default.ChatCompletionResponse);
        Assert.NotNull(response);
        var args = response.Choices[0].Message.ToolCalls![0].Function.Arguments;
        Assert.Contains("scope", args, StringComparison.Ordinal);
    }
}
