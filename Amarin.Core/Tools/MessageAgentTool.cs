using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>
/// Сообщение чата агенту, уже отчитавшемуся в этом ответе (1.36.0).
/// </summary>
/// <remarks>
/// До 1.36.0 агент жил до своего отчёта: чат, видя ошибку агента, не мог ни переспросить его, ни
/// переубедить и запускал нового, который начинал с нуля. Теперь отчёт — просто реплика, и чат
/// отвечает на неё; агент продолжает свою переписку (<see cref="IAgentHost.ContinueAsync"/>).
/// </remarks>
public sealed class MessageAgentTool : ITool
{
    public const string ToolName = "message_agent";

    private readonly AgentSlotLimiter _limiter;
    private readonly IAgentHost _host;

    internal MessageAgentTool(AgentSlotLimiter limiter, IAgentHost host)
    {
        _limiter = limiter;
        _host = host;
    }

    public string Name => ToolName;

    public string Description =>
        "Write to an agent you started and wait for its reply, which comes back as a new report. " +
        "Invoke this as a tool call, never as chat text. " +
        "The agent keeps everything it did and found: ask it what is unclear, tell it what it got wrong or what you found, " +
        "or give it the next part of the task, instead of starting a new agent. " +
        "agent_id is the id at the start of the agent's report. How long the agent stays available is stated in the " +
        "AGENT LIFETIME rules.";

    public JsonElement ParametersSchema => JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "agent_id": {
              "type": "string",
              "description": "The id from the start of the agent's report."
            },
            "message": {
              "type": "string",
              "description": "What you write to the agent, in the user's language. The agent sees this and its own earlier work, not the chat."
            }
          },
          "required": ["agent_id", "message"]
        }
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!AgentToolArguments.TryRead(arguments, "agent_id", out var agentId))
        {
            return ToolResult.Fail("Missing required parameter: agent_id");
        }

        if (!AgentToolArguments.TryRead(arguments, "message", out var message))
        {
            return ToolResult.Fail("Missing required parameter: message");
        }

        if (!_limiter.TryEnter())
        {
            return ToolResult.Fail(AgentToolArguments.LimiterFull);
        }

        try
        {
            return await _host.ContinueAsync(agentId, message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _limiter.Exit();
        }
    }
}

/// <summary>Общее у инструментов агента: чтение аргумента и отказ переполненного ограничителя.</summary>
internal static class AgentToolArguments
{
    public const string LimiterFull = "Уже запущено 4 агента. Дождитесь завершения одного из них.";

    public static bool TryRead(JsonElement arguments, string name, out string value)
    {
        value = arguments.ValueKind == JsonValueKind.Object &&
                arguments.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim() ?? ""
            : "";
        return value.Length > 0;
    }
}
