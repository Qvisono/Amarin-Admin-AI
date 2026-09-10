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
        "Arguments: one JSON object with exactly two keys, prompt and complexity. " +
        "No extra keys, no markdown, no text after the closing brace. " +
        "complexity is exactly \"lite\" or \"heavy\" — never a model name. " +
        "lite = one status check or listing. heavy = install, repair, diagnosis, many steps. Unsure: lite. " +
        "prompt restates the user's actual request in the user's language (goal, paths, what to change). " +
        "The agent does not see the chat. At most 4 agents at once.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Short complete task in the user's language: restate the user's actual request (goal, paths, what to change). One string. Escape quotes. Do not truncate with ellipsis. Do not copy examples from the tool description. The agent sees only this, not the chat."
            },
            "complexity": {
              "type": "string",
              "enum": ["lite", "heavy"],
              "description": "Exactly lite or heavy. lite = one check/listing. heavy = install, repair, many steps. Unsure: lite. Not a model id."
            }
          },
          "required": ["prompt", "complexity"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("prompt", out var promptProp) ||
            string.IsNullOrWhiteSpace(promptProp.GetString()))
        {
            return ToolResult.Fail("Missing required parameter: prompt");
        }

        var complexity = arguments.TryGetProperty("complexity", out var complexityProp)
            ? complexityProp.GetString()?.Trim().ToLowerInvariant()
            : null;

        if (complexity is not "lite" and not "heavy")
        {
            return ToolResult.Fail("complexity must be exactly \"lite\" or \"heavy\".");
        }

        if (!_limiter.TryEnter())
        {
            return ToolResult.Fail("Уже запущено 4 агента. Дождитесь завершения одного из них.");
        }

        try
        {
            return await _host.RunAsync(promptProp.GetString()!.Trim(), complexity, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _limiter.Exit();
        }
    }
}
