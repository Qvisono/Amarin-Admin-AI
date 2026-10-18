using System.Text.Json;
using Amarin.Core;

namespace Amarin.Tools;

/// <summary>Поручение оператору мыши: что сделать в окнах и сделать ли самому или показать.</summary>
internal sealed record ComputerJob(string Task, ComputerMode Mode);

/// <summary>
/// Инструмент агента: передать работу в окнах оператору (<see cref="ComputerOperator"/>).
/// </summary>
/// <remarks>
/// Сам ничего не нажимает и в шлюзе считается чтением, как <c>init_agent</c>: каждое действие
/// оператора проходит шлюз, SynGuard и вопросы само. Есть у агента, только когда человек включил
/// управление компьютером, — иначе модель не видит его вовсе.
/// </remarks>
internal sealed class UseComputerTool(Func<ComputerJob, CancellationToken, Task<ToolResult>> run) : ITool
{
    public const string ToolName = "use_computer";

    private static readonly JsonElement Schema = JsonSchema.Parse("""
        {
          "type": "object",
          "properties": {
            "task": {
              "type": "string",
              "description": "What to do in program windows: the goal and the finished state, with exactly what the user specified (program or site, text, recipient, options). The operator sees only this."
            },
            "mode": {
              "type": "string",
              "enum": ["do", "show"],
              "description": "do: the operator does it. show: the user asked to be shown where to click; the operator highlights each step and the user clicks."
            }
          },
          "required": ["task"]
        }
        """);

    public string Name => ToolName;

    public string Description =>
        "Hand a task in program windows of this PC to an operator that sees the screen and works with the mouse " +
        "and keyboard. Use it when none of your tools can reach the goal, or when the user asked for the work to be " +
        "done in a program's window. The operator returns a report of what it did and what is on the screen.";

    public JsonElement ParametersSchema => Schema;

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var task = arguments.ValueKind == JsonValueKind.Object &&
                   arguments.TryGetProperty("task", out var value) &&
                   value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";
        if (task.Length == 0)
        {
            return Task.FromResult(ToolResult.Fail("Missing required parameter: task"));
        }

        var mode = arguments.ValueKind == JsonValueKind.Object &&
                   arguments.TryGetProperty("mode", out var modeValue) &&
                   modeValue.ValueKind == JsonValueKind.String &&
                   string.Equals(modeValue.GetString()?.Trim(), "show", StringComparison.OrdinalIgnoreCase)
            ? ComputerMode.Show
            : ComputerMode.Do;

        return run(new ComputerJob(task, mode), cancellationToken);
    }
}
