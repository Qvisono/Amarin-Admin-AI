using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Задачу отказавшего агента — модели сильнее (1.36.0): лёгкая и быстрая → тяжёлая → обход.
/// </summary>
/// <remarks>
/// Отказ агента (<c>i_refuse</c>) видит чат и решает сам: переубедить агента
/// (<see cref="MessageAgentTool"/>) или поднять его выше. Отказной ответ новая модель не видит.
/// </remarks>
public sealed class AgentRefusedTool : ITool
{
    public const string ToolName = "agent_refused";

    private readonly AgentSlotLimiter _limiter;
    private readonly IAgentHost _host;

    internal AgentRefusedTool(AgentSlotLimiter limiter, IAgentHost host)
    {
        _limiter = limiter;
        _host = host;
    }

    public string Name => ToolName;

    public string Description =>
        "Give the task of an agent that refused all or part of it to a stronger model. Invoke this as a tool call, never as chat text. " +
        "The agent's refusing reply is dropped, and the stronger model carries on from that point with everything the agent did so far. " +
        "Each call moves the agent one step up; the result says when no stronger model is left. " +
        "Call it only after the agent refused; the reply comes back as a new report.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "agent_id": {
              "type": "string",
              "description": "The id from the start of the refusing agent's report."
            }
          },
          "required": ["agent_id"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!AgentToolArguments.TryRead(arguments, "agent_id", out var agentId))
        {
            return ToolResult.Fail("Missing required parameter: agent_id");
        }

        if (!_limiter.TryEnter())
        {
            return ToolResult.Fail(AgentToolArguments.LimiterFull);
        }

        try
        {
            return await _host.EscalateAsync(agentId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _limiter.Exit();
        }
    }
}
