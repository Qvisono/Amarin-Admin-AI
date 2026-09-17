using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

public sealed class InitAgentTool : ITool
{
    public const string ToolName = "init_agent";

    private readonly AgentSlotLimiter _limiter;
    private readonly IAgentHost _host;

    internal InitAgentTool(AgentSlotLimiter limiter, IAgentHost host)
    {
        _limiter = limiter;
        _host = host;
    }

    public string Name => ToolName;

    public string Description =>
        "Start a sysadmin agent on this PC. Invoke this as a tool call, never as chat text. " +
        "Arguments: one JSON object with the key prompt, and notes when you have something to add. " +
        "No other keys, no markdown, no text after the closing brace. " +
        "prompt restates the user's actual request in the user's language (goal, paths, what to change). " +
        "The agent does not see the chat. " +
        "You do not choose which model runs it: the app routes the task by prompt and notes. " +
        "notes is one short line for that router about what matters here and what the user asked " +
        "for — never a model name, never a tier. At most 4 agents at once.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Short complete task in the user's language: restate the user's actual request (goal, paths, what to change). One string. Escape quotes. Do not truncate with ellipsis. Do not copy examples from the tool description. The agent sees only this, not the chat."
            },
            "notes": {
              "type": "string",
              "description": "Optional. One short line for the router that picks the model: what the user asked for (to hurry, to be careful) and what makes this work easy or uncertain. Write it only when you have something real to add; leave it out otherwise. Never a model id and never a tier word — you are not choosing the model."
            }
          },
          "required": ["prompt"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("prompt", out var promptProp) ||
            string.IsNullOrWhiteSpace(promptProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: prompt");
        }

        // Уровень агента сюда больше не приходит, и присланный по старой памяти "complexity"
        // молча игнорируется: отказ стоил бы целого раунда ради аргумента, который всё равно
        // ничего не решает.
        var notes = arguments.TryGetProperty("notes", out var notesProp) &&
                    notesProp.ValueKind == JsonValueKind.String
            ? notesProp.GetString()?.Trim()
            : null;

        if (!_limiter.TryEnter())
        {
            return ToolResult.Fail("Уже запущено 4 агента. Дождитесь завершения одного из них.");
        }

        try
        {
            return await _host.RunAsync(promptProp.GetString()!.Trim(), notes, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _limiter.Exit();
        }
    }
}
