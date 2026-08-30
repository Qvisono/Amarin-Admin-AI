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
        "Start a system-administration agent for a concrete task on this PC. " +
        "Pass complexity as exactly \"lite\" or \"heavy\" — do not pass model names. " +
        "lite = one status check or listing, no repair. " +
        "heavy = repair, root-cause diagnosis, many steps. When unsure, use lite. " +
        "prompt must restate the user's actual request (language, paths, file types, what to measure). " +
        "Do not copy examples from this description. " +
        "The agent has the full admin toolset and reports back when done. " +
        "At most 4 agents may run at once; a fifth call returns an error.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "prompt": {
              "type": "string",
              "description": "Full task in the user's language: restate the user's request with paths, file types, and what to measure or change. Do not copy examples from the tool description."
            },
            "complexity": {
              "type": "string",
              "enum": ["lite", "heavy"],
              "description": "lite = one status check or listing, no repair. heavy = repair or long diagnosis. When unsure, lite. Do not pass a model id."
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
